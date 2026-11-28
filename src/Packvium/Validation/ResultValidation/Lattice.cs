using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using static System.FormattableString;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Compact lattice results, expanded independently before ordinary validation. The summary is
/// untrusted: its parameters must agree with the request before they are used to synthesize
/// placements, which then pass through the same checks as explicit ones. O(placements synthesized).
/// </summary>
internal static class Lattice
{
    private static readonly ImmutableArray<string> Sides = ["length", "width", "height"];

    /// <summary>The result with every valid summary expanded into placements, or the result itself when none is compact.</summary>
    public static JsonValue Materialize(JsonValue request, JsonValue result, RequestContext context, List<ResultIssue> issues)
    {
        var packedContainers = Json.Items(Json.At(result, "containers"), "result.containers");
        if (!AnySummary(packedContainers))
        {
            return result;
        }
        var optional = Json.Get(Json.Get(request, "configuration", Json.EmptyObject), "require_placement_coordinates") is JsonBool { Value: false };
        var expanded = packedContainers.Select(packed => Expand(packed, optional, context, issues) is { } placements
            ? (JsonValue)((JsonObject)packed).With("placements", placements)
            : packed).ToImmutableArray();
        return ((JsonObject)result).With("containers", new JsonArray(expanded));
    }

    /// <summary><c>any("lattice_summary" in packed ...)</c>, stopping at the first, as the generator does.</summary>
    private static bool AnySummary(ImmutableArray<JsonValue> packedContainers)
    {
        foreach (var packed in packedContainers)
        {
            if (Json.Contains(packed, "lattice_summary"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The placements to write into the container, or null to leave it as reported.</summary>
    private static JsonArray? Expand(JsonValue packed, bool optional, RequestContext context, List<ResultIssue> issues)
    {
        var summary = Json.Get(packed, "lattice_summary");
        if (summary is JsonNull)
        {
            return null;
        }
        var containerId = PyFormat.Str(Json.Get(packed, "id", new JsonString("?")));
        if (!optional)
        {
            issues.Add(Issues.Of("unexpected_lattice_summary", $"{containerId}: request requires explicit placement coordinates"));
        }
        if (Json.Truthy(Json.Get(packed, "placements")))
        {
            issues.Add(Issues.Of("lattice_placements_conflict", $"{containerId}: compact summary and explicit placements are mutually exclusive"));
        }
        var itemType = Json.Get(summary, "item_type");
        var item = context.Items.GetOrDefault(itemType);
        var container = context.Containers.GetOrDefault(Json.Get(packed, "container_type"));
        if (item is null || container is null)
        {
            // The ordinary accounting and container checks report the unknown reference.
            return null;
        }
        var fields = Read(summary, containerId);
        var problems = Problems(fields, item, itemType, container, context.Clearance);
        if (problems.Count > 0)
        {
            issues.AddRange(problems.Select(problem => Issues.Of("lattice_summary_mismatch", $"{containerId}: {problem}")));
            return Json.EmptyList;
        }
        return Synthesize(fields, Json.At(summary, "physical_dimensions"), itemType, containerId, context.Clearance);
    }

    /// <summary>The summary's canonical fields; a missing or mistyped one is the reference's contract refusal, naming the container.</summary>
    private static Fields Read(JsonValue summary, string containerId)
    {
        try
        {
            ImmutableArray<JsonValue> Block(string key) =>
                [.. Sides.Select(side => Json.At(Json.At(Json.At(summary, key), side), "ticks"))];

            return new Fields(
                Json.At(summary, "orientation"),
                Block("physical_dimensions"),
                Block("envelope_dimensions"),
                Json.At(summary, "nx"),
                Json.At(summary, "ny"),
                Json.At(summary, "layers_used"),
                Json.At(Json.At(summary, "layer_step"), "ticks"),
                Json.At(summary, "count"));
        }
        catch (MalformedResultException missing)
        {
            throw MalformedResultException.ContractBreak($"{containerId}: lattice_summary is missing a canonical field: {missing.Message}");
        }
    }

    private static List<string> Problems(Fields fields, ItemSpec item, JsonValue itemType, ContainerSpec container, BigInteger clearance)
    {
        var problems = new List<string>();
        if (!Geometry.IsRotation(fields.Orientation))
        {
            problems.Add(Invariant($"unknown orientation {PyFormat.Repr(fields.Orientation)}"));
        }
        else
        {
            var orientation = ((JsonString)fields.Orientation).Value;
            var expected = item.Rotated(orientation);
            if (!Matches(fields.Physical, expected))
            {
                problems.Add(Invariant($"physical dimensions {PyFormat.ValueTuple(fields.Physical)} != rotated item {PyFormat.IntTuple(expected)}"));
            }
            if (!item.Allows(fields.Orientation))
            {
                problems.Add(Invariant($"orientation {orientation} is forbidden for {PyFormat.Str(itemType)}"));
            }
        }
        var expectedEnvelope = fields.Physical.Select(side => Json.Int(side, "physical dimension") + 2 * clearance).ToImmutableArray();
        if (!Matches(fields.Envelope, expectedEnvelope))
        {
            problems.Add(Invariant($"envelope dimensions {PyFormat.ValueTuple(fields.Envelope)} != physical+clearance {PyFormat.IntTuple(expectedEnvelope)}"));
        }
        if (item.NestingHeight is not null)
        {
            problems.Add("compact lattice form is not valid for nesting items");
        }
        if (!Json.PyEquals(fields.LayerStep, fields.Envelope[2]))
        {
            problems.Add(Invariant($"layer_step {PyFormat.Str(fields.LayerStep)} != envelope height {PyFormat.Str(fields.Envelope[2])}"));
        }
        foreach (var (name, value) in fields.Counted())
        {
            if (Json.StrictInt(value) is not { } count || count < 1)
            {
                problems.Add(Invariant($"{name} must be a positive integer, got {PyFormat.Repr(value)}"));
            }
        }
        if (problems.Count == 0)
        {
            IndependentCounts(fields, item, container, problems);
        }
        return problems;
    }

    /// <summary>Reached only once every count is a positive integer and the envelope is the expected one.</summary>
    private static void IndependentCounts(Fields fields, ItemSpec item, ContainerSpec container, List<string> problems)
    {
        var nx = Json.Int(fields.Nx, "nx");
        var ny = Json.Int(fields.Ny, "ny");
        var layersUsed = Json.Int(fields.LayersUsed, "layers_used");
        var layerStep = Json.Int(fields.LayerStep, "layer_step");
        var count = Json.Int(fields.Count, "count");
        var expectedNx = Exact.FloorDiv(container.Inner[0], Json.Int(fields.Envelope[0], "envelope length"));
        var expectedNy = Exact.FloorDiv(container.Inner[1], Json.Int(fields.Envelope[1], "envelope width"));
        var expectedLayers = Exact.FloorDiv(count + nx * ny - 1, nx * ny);
        if (nx != expectedNx)
        {
            problems.Add(Invariant($"nx {nx} != independently computed {expectedNx}"));
        }
        if (ny != expectedNy)
        {
            problems.Add(Invariant($"ny {ny} != independently computed {expectedNy}"));
        }
        if (layersUsed != expectedLayers)
        {
            problems.Add(Invariant($"layers_used {layersUsed} != independently computed {expectedLayers}"));
        }
        if (Json.CompareInt(item.Quantity, count) < 0)
        {
            problems.Add(Invariant($"count {count} exceeds requested quantity {PyFormat.Str(item.Quantity)}"));
        }
        var capacity = Exact.FloorDiv(container.Inner[2], layerStep);
        if (layersUsed > capacity)
        {
            problems.Add(Invariant($"layers_used {layersUsed} exceeds vertical capacity {capacity}"));
        }
    }

    private static JsonArray Synthesize(Fields fields, JsonValue physical, JsonValue itemType, string containerId, BigInteger clearance)
    {
        var nx = Json.Int(fields.Nx, "nx");
        var ny = Json.Int(fields.Ny, "ny");
        var count = Json.Int(fields.Count, "count");
        var (envelopeLength, envelopeWidth) = (Json.Int(fields.Envelope[0], "envelope"), Json.Int(fields.Envelope[1], "envelope"));
        var layerStep = Json.Int(fields.LayerStep, "layer_step");
        var label = PyFormat.Str(itemType);
        var placements = ImmutableArray.CreateBuilder<JsonValue>();
        for (var index = BigInteger.Zero; index < count; index++)
        {
            placements.Add(new JsonObject([
                Member("item_id", new JsonString(Invariant($"{label}#lattice:{containerId}:{index + 1}"))),
                Member("item_type", itemType),
                Member("orientation", fields.Orientation),
                Member("position", new JsonObject([
                    Member("x", Ticks(index % nx * envelopeLength + clearance)),
                    Member("y", Ticks(index / nx % ny * envelopeWidth + clearance)),
                    Member("z", Ticks(index / (nx * ny) * layerStep + clearance)),
                ])),
                Member("dimensions", physical),
            ]));
        }
        return new JsonArray(placements.ToImmutable());
    }

    private static KeyValuePair<string, JsonValue> Member(string key, JsonValue value) => new(key, value);

    private static JsonObject Ticks(BigInteger value) => new([Member("ticks", new JsonInteger(value))]);

    /// <summary>Element-wise <c>reported == expected</c> for a tuple of JSON values.</summary>
    private static bool Matches(ImmutableArray<JsonValue> reported, ImmutableArray<BigInteger> expected) =>
        reported.Zip(expected).All(pair => Json.EqualsInt(pair.First, pair.Second));

    /// <summary>The summary's fields as the reference reads them, still JSON values.</summary>
    private sealed record Fields(JsonValue Orientation, ImmutableArray<JsonValue> Physical, ImmutableArray<JsonValue> Envelope,
        JsonValue Nx, JsonValue Ny, JsonValue LayersUsed, JsonValue LayerStep, JsonValue Count)
    {
        public IEnumerable<(string Name, JsonValue Value)> Counted() =>
            [("nx", Nx), ("ny", Ny), ("layers_used", LayersUsed), ("layer_step", LayerStep), ("count", Count)];
    }
}
