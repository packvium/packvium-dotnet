using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Packvium.Execution;
using Packvium.Serialization;

namespace Packvium.Artifacts;

/// <summary>
/// The portable operational artifact (<c>artifacts.py</c>; <c>docs/OPERATIONAL-ARTIFACTS.md</c> is
/// the contract). An execution plan says what to do first; it does not say how big the boxes
/// are, what the load weighs or which request and solver produced it. The artifact is the one
/// document that can be drawn, printed and traced offline, and it adds nothing a solver decided.
///
/// It reads the request, the result and the optional loading orders -- the plan's own inputs --
/// and calls no solver, validator, renderer or clock. The plan inside it is exactly what
/// <see cref="ExecutionPlan.Build"/> emits for the same inputs, and placements are found by the
/// plan's <c>placement_ref</c>, never by <c>item_id</c>. Every engine's builder is held to the
/// same canonical bytes.
/// </summary>
public static class OperationalArtifact
{
    public const string Format = "packvium-operational-artifact/v1";

    /// <summary>
    /// The suite version of this builder, the same string in every engine of one release. The
    /// engine's own name is deliberately not recorded: correct builders naming themselves would
    /// emit different documents.
    /// </summary>
    public const string SuiteVersion = "1.3.0";

    /// <summary>The deterministic part of <c>result.algorithm</c>; <c>duration_ms</c> is wall-clock time and never enters.</summary>
    public static ImmutableArray<string> SolverFields { get; } =
        ["profile", "solver", "seed", "time_limit_reached", "effort_limit_reached"];

    private static readonly string[] DimensionAxes = ["length", "width", "height"];

    private static readonly string[] PositionAxes = ["x", "y", "z"];

    private static readonly BigInteger MaxExactMagnitude = CanonicalJsonWriter.MaxExactMagnitude;

    /// <summary>
    /// Build the artifact for one validated result, in its JSON form. O(R + P) for a request of
    /// size R and P placements, plus key sorting when it is serialized. Throws
    /// <see cref="OperationalArtifactException"/> for anything an artifact cannot carry, including
    /// a value with no canonical spelling: an artifact that exists has one spelling everywhere.
    /// </summary>
    /// <param name="request">The request the result answers, embedded verbatim.</param>
    /// <param name="result">The result document, as <c>ResultWriter</c> writes it or as a golden stores it.</param>
    /// <param name="loadingOrders">A container's index to the engine-computed order its placements are loaded in.</param>
    public static JsonObject Build(JsonValue request, JsonValue result,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? loadingOrders = null)
    {
        if (request is not JsonObject requestObject)
        {
            throw new OperationalArtifactException("invalid_request", "a request is a JSON object");
        }
        if (result is not JsonObject resultObject)
        {
            throw new OperationalArtifactException("invalid_result", "a result is a JSON object");
        }
        RequireObjects(resultObject);
        JsonObject plan;
        try
        {
            plan = ExecutionPlan.Build(requestObject, resultObject, loadingOrders);
        }
        catch (ExecutionPlanException error)
        {
            throw new OperationalArtifactException("invalid_plan_input", error.Message, error);
        }
        var containers = Containers(resultObject);
        var (lengthUnit, weightUnit) = Units(containers);
        var provenance = Provenance(requestObject, resultObject);
        var geometry = containers.Select((container, index) => (JsonValue)Geometry(index, container)).ToImmutableArray();
        var planContainers = ((JsonArray)plan.Require("containers")).Items;
        var workOrder = containers
            .Select((container, index) => (JsonValue)WorkOrderContainer(index, container, (JsonObject)planContainers[index],
                lengthUnit, weightUnit))
            .ToImmutableArray();
        var artifact = Object(
            ("format", new JsonString(Format)),
            ("suite_version", new JsonString(SuiteVersion)),
            ("provenance", provenance),
            ("plan", plan),
            ("geometry", Object(("containers", new JsonArray(geometry)))),
            ("work_order", Object(
                ("length_unit", lengthUnit),
                ("weight_unit", weightUnit),
                ("containers", new JsonArray(workOrder)))));
        CanonicalJson(artifact);
        return artifact;
    }

    /// <summary><see cref="Build"/> from JSON text; text that does not parse is refused as <c>invalid_json</c>.</summary>
    public static JsonObject FromJson(string requestJson, string resultJson,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? loadingOrders = null) =>
        Build(Parse(requestJson), Parse(resultJson), loadingOrders);

    /// <summary>Reads a stored document, or any JSON text, with the number and string rules the builder relies on.</summary>
    public static JsonValue Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonReader.Parse(json);
        }
        catch (JsonException error)
        {
            throw new OperationalArtifactException("invalid_json", $"not JSON text: {error.Message}", error);
        }
    }

    /// <summary>The artifact's RFC 8785 canonical form, the bytes every engine is compared on.</summary>
    public static string CanonicalJson(JsonValue artifact)
    {
        try
        {
            return CanonicalJsonWriter.Write(artifact);
        }
        catch (CanonicalJsonException error)
        {
            throw new OperationalArtifactException(error.Code, error.Message, error);
        }
    }

    // ---------------------------------------------------------------------------- provenance

    private static JsonObject Provenance(JsonObject request, JsonObject result)
    {
        var solver = Solver(result.Get("algorithm"));
        var catalogs = List(result.Get("catalog_versions_used"), "result.catalog_versions_used");
        foreach (var catalog in catalogs)
        {
            Catalog(catalog);
        }
        return Object(
            // Embedded, not digested: only the request itself lets someone replay the artifact
            // without a lookup.
            ("request", request),
            ("catalog_versions_used", new JsonArray(catalogs)),
            ("solver", (JsonValue?)solver ?? JsonNull.Instance),
            ("replay", Replay(solver)));
    }

    /// <summary>
    /// The result schema's closed catalog reference. The work order prints these fields, so a
    /// mistyped one would print differently in every engine.
    /// </summary>
    private static void Catalog(JsonValue catalog)
    {
        if (Field(catalog, "catalog_id", "catalog_versions_used[]") is not JsonString)
        {
            throw new OperationalArtifactException("invalid_result", "catalog_versions_used[].catalog_id is not a string");
        }
        foreach (var name in new[] { "version", "effective_at", "resolved_at" })
        {
            if (Field(catalog, name, "catalog_versions_used[]") is not JsonInteger)
            {
                throw new OperationalArtifactException("invalid_result", $"catalog_versions_used[].{name} is not an integer");
            }
        }
    }

    private static JsonObject? Solver(JsonValue? algorithm)
    {
        if (algorithm is null or JsonNull)
        {
            return null;
        }
        if (algorithm is not JsonObject fields)
        {
            throw new OperationalArtifactException("invalid_result", "result.algorithm is not an object");
        }
        var missing = SolverFields.Where(field => !fields.Contains(field)).ToList();
        if (missing.Count > 0)
        {
            throw new OperationalArtifactException("invalid_result", $"result.algorithm has no {string.Join(", ", missing)}");
        }
        foreach (var flag in new[] { "time_limit_reached", "effort_limit_reached" })
        {
            if (fields.Require(flag) is not JsonBool)
            {
                throw new OperationalArtifactException("invalid_result", $"result.algorithm.{flag} is not a boolean");
            }
        }
        return Object([.. SolverFields.Select(field => (field, fields.Require(field)))]);
    }

    /// <summary>
    /// <c>exact</c> only when a replay must reproduce the result. A search stopped by wall-clock
    /// time cannot be reproduced, and a result that does not say how it was solved cannot be
    /// promised to.
    /// </summary>
    private static JsonObject Replay(JsonObject? solver) => solver switch
    {
        null => Replay("not_guaranteed", new JsonString("provenance.solver")),
        _ when solver.Require("time_limit_reached") is JsonBool { Value: true } =>
            Replay("not_guaranteed", new JsonString("provenance.solver.time_limit_reached")),
        _ => Replay("exact", JsonNull.Instance),
    };

    private static JsonObject Replay(string level, JsonValue because) =>
        Object(("level", new JsonString(level)), ("because", because));

    // ------------------------------------------------------------------------------ geometry

    private static JsonObject Geometry(int index, JsonObject container) => Object(
        ("container_index", new JsonInteger(index)),
        ("inner_dimensions", TickDimensions(Field(container, "inner_dimensions", $"containers[{index}]"))),
        ("placements", new JsonArray([.. Placements(index, container).Select(placement => (JsonValue)Object(
            ("placement", ExecutionPlan.PlacementReference(index, placement)),
            ("dimensions", TickDimensions(Field(placement, "dimensions", $"containers[{index}].placements[]")))))])));

    /// <summary>
    /// Lengths as decimal strings of ticks: the scene contract's spelling, and one no engine has
    /// to hold as a native number.
    /// </summary>
    private static JsonObject TickDimensions(JsonValue dimensions) =>
        Object([.. DimensionAxes.Select(axis => (axis, (JsonValue)new JsonString(Ticks(Field(dimensions, axis, "dimensions")))))]);

    private static string Ticks(JsonValue scalar)
    {
        if (Field(scalar, "ticks", "exact scalar") is not JsonInteger ticks)
        {
            throw new OperationalArtifactException("invalid_result", "ticks is not an integer");
        }
        // Written as a string, so the canonical writer never sees it as a number; the range is
        // checked here instead, because JavaScript has already rounded such a value while parsing.
        if (BigInteger.Abs(ticks.Value) > MaxExactMagnitude)
        {
            throw new OperationalArtifactException("number_out_of_range",
                $"ticks {ticks.Value} is beyond what every engine holds exactly");
        }
        return ticks.Value.ToString(CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------- work order

    /// <summary>
    /// The display units, read from the result rather than re-derived from request defaults: the
    /// result already rendered every value in them.
    /// </summary>
    private static (JsonValue Length, JsonValue Weight) Units(ImmutableArray<JsonObject> containers)
    {
        if (containers.IsEmpty)
        {
            return (JsonNull.Instance, JsonNull.Instance);
        }
        var first = containers[0];
        var length = Field(Field(Field(first, "inner_dimensions", "containers[0]"), "length", "inner_dimensions"), "unit", "length");
        var weight = Field(Field(first, "payload_weight", "containers[0]"), "unit", "payload_weight");
        return (length, weight);
    }

    private static JsonObject WorkOrderContainer(int index, JsonObject container, JsonObject planContainer,
        JsonValue lengthUnit, JsonValue weightUnit)
    {
        var byReference = PlacementsByReference(index, container);
        // One line per plan step, in plan step order: the order is the plan's, looked up, never
        // derived a second time.
        var lines = ((JsonArray)planContainer.Require("steps")).Items.Select(entry =>
        {
            var step = (JsonObject)entry;
            var reference = step.Require("placement");
            var placement = byReference[Key(reference)];
            var line = new List<(string, JsonValue)>();
            if (step.Get("sequence") is { } sequence)
            {
                line.Add(("sequence", sequence));
            }
            var position = Field(placement, "position", "placement");
            var dimensions = Field(placement, "dimensions", "placement");
            line.Add(("placement", reference));
            line.Add(("position", Values(position, PositionAxes, "position", lengthUnit)));
            line.Add(("dimensions", Values(dimensions, DimensionAxes, "dimensions", lengthUnit)));
            return (JsonValue)Object([.. line]);
        }).ToImmutableArray();
        return Object(
            ("container_index", new JsonInteger(index)),
            ("container_type", container.Get("container_type") ?? JsonNull.Instance),
            ("payload_weight", new JsonString(Value(Field(container, "payload_weight", $"containers[{index}]"), weightUnit))),
            ("gross_weight", new JsonString(Value(Field(container, "gross_weight", $"containers[{index}]"), weightUnit))),
            ("lines", new JsonArray(lines)));
    }

    private static JsonObject Values(JsonValue scalars, string[] axes, string where, JsonValue unit) =>
        Object([.. axes.Select(axis => (axis, (JsonValue)new JsonString(Value(Field(scalars, axis, where), unit))))]);

    private static Dictionary<PlacementKey, JsonObject> PlacementsByReference(int index, JsonObject container)
    {
        var placements = Placements(index, container);
        var found = new Dictionary<PlacementKey, JsonObject>();
        foreach (var placement in placements)
        {
            found[Key(ExecutionPlan.PlacementReference(index, placement))] = placement;
        }
        if (found.Count != placements.Length)
        {
            throw new OperationalArtifactException("invalid_result",
                $"two placements in container {index} share an origin, type and orientation");
        }
        return found;
    }

    /// <summary>What a <c>placement_ref</c> names: item type, orientation and position ticks.</summary>
    private readonly record struct PlacementKey(JsonValue ItemType, JsonValue Orientation, JsonValue X, JsonValue Y, JsonValue Z);

    private static PlacementKey Key(JsonValue reference)
    {
        var fields = (JsonObject)reference;
        var ticks = (JsonObject)fields.Require("position_ticks");
        return new PlacementKey(fields.Require("item_type"), fields.Require("orientation"),
            ticks.Require("x"), ticks.Require("y"), ticks.Require("z"));
    }

    /// <summary>The result's rendered value, copied and never re-rendered.</summary>
    private static string Value(JsonValue scalar, JsonValue unit)
    {
        if (Field(scalar, "unit", "exact scalar") != unit)
        {
            throw new OperationalArtifactException("mixed_units", "a value in a unit other than the one the result uses");
        }
        return Field(scalar, "value", "exact scalar") is JsonString value
            ? value.Value
            : throw new OperationalArtifactException("invalid_result", "an exact scalar's value is not a string");
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Every list entry the artifact and its plan read is an object, checked before either reads
    /// one, so a malformed result is refused by name in every engine instead of failing wherever
    /// each language first touches it.
    /// </summary>
    private static void RequireObjects(JsonObject result)
    {
        foreach (var name in new[] { "containers", "unpacked_items", "alternatives", "catalog_versions_used" })
        {
            if (List(result.Get(name), $"result.{name}").Any(entry => entry is not JsonObject))
            {
                throw new OperationalArtifactException("invalid_result", $"result.{name} holds a non-object entry");
            }
        }
        var containers = List(result.Get("containers"), "result.containers");
        for (var index = 0; index < containers.Length; index++)
        {
            if (List(((JsonObject)containers[index]).Get("placements"), $"containers[{index}].placements")
                .Any(placement => placement is not JsonObject))
            {
                throw new OperationalArtifactException("invalid_result", $"containers[{index}].placements holds a non-object entry");
            }
        }
    }

    private static ImmutableArray<JsonObject> Containers(JsonObject result) =>
        [.. List(result.Get("containers"), "result.containers").Cast<JsonObject>()];

    private static ImmutableArray<JsonObject> Placements(int index, JsonObject container) =>
        [.. List(container.Get("placements"), $"containers[{index}].placements").Cast<JsonObject>()];

    private static ImmutableArray<JsonValue> List(JsonValue? value, string where) => value switch
    {
        null or JsonNull => [],
        JsonArray array => array.Items,
        _ => throw new OperationalArtifactException("invalid_result", $"{where} is not a list"),
    };

    private static JsonValue Field(JsonValue mapping, string name, string where) =>
        mapping is JsonObject fields && fields.Get(name) is { } value
            ? value
            : throw new OperationalArtifactException("invalid_result", $"{where} has no {name}");

    private static JsonObject Object(params (string Key, JsonValue Value)[] members) =>
        new(members.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)));
}
