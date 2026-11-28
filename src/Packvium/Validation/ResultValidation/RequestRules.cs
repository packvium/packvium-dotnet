using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>The request-level rules only the whole result can answer: fixed placements, groups and solver selection.</summary>
internal static class RequestRules
{
    private static readonly ImmutableArray<string> Axes = ["x", "y", "z"];

    /// <summary>
    /// Every fixed placement is reported where the request put it, and nothing else claims to be.
    /// A key is <c>(container id, item type, x, y, z, orientation)</c>. O(k log k + placements) for k fixed placements.
    /// </summary>
    public static void CheckFixedPlacements(JsonValue request, JsonValue result, string unit, List<ResultIssue> issues)
    {
        var requested = new Ordered<JsonArray>();
        foreach (var entry in Json.Items(Json.Get(request, "fixed_placements", Json.EmptyList), "fixed_placements"))
        {
            var containerId = $"{PyFormat.Str(Json.At(entry, "container_type"))}#{PyFormat.Str(Json.Get(entry, "container_instance", new JsonInteger(1)))}";
            var itemType = Json.At(entry, "item_type");
            var position = Json.Get(entry, "position", Json.EmptyObject);
            var ticks = Axes.Select(axis => (JsonValue)new JsonInteger(TickUnits.ToTicks(Json.Get(position, axis, new JsonInteger(0)), unit, TickUnits.Length)));
            Add(requested, [new JsonString(containerId), itemType, .. ticks, Json.At(entry, "orientation")]);
        }
        var reported = new Ordered<JsonArray>();
        var containerTypes = new Ordered<JsonValue>();
        foreach (var container in Json.Items(Json.At(result, "containers"), "result.containers"))
        {
            var containerId = Json.Get(container, "id", new JsonString(string.Empty));
            containerTypes.Set(containerId, Json.At(container, "container_type"));
            foreach (var placement in Json.Items(Json.At(container, "placements"), "placements"))
            {
                if (!Json.IsTrue(Json.Get(placement, "fixed")))
                {
                    continue;
                }
                var itemType = Json.At(placement, "item_type");
                var position = Json.At(placement, "position");
                var ticks = Axes.Select(axis => Json.At(Json.At(position, axis), "ticks"));
                Add(reported, [containerId, itemType, .. ticks, Json.At(placement, "orientation")]);
            }
        }
        foreach (var key in Sorted(requested.Values))
        {
            var containerId = ((JsonString)key.Items[0]).Value;
            var hash = containerId.LastIndexOf('#');
            var declaredType = hash < 0 ? containerId : containerId[..hash];
            if (!Json.IsText(containerTypes.GetOrDefault(key.Items[0]) ?? JsonNull.Instance, declaredType))
            {
                issues.Add(Issues.Of("fixed_container_missing", $"{containerId}"));
            }
            else if (!reported.Has(KeyOf(key)))
            {
                issues.Add(Issues.Of("fixed_placement_moved",
                    $"{PyFormat.Str(key.Items[1])} fixed in {containerId} at {Where(key)} {PyFormat.Str(key.Items[5])}"));
            }
        }
        foreach (var key in Sorted(reported.Values.Where(key => !requested.Has(KeyOf(key)))))
        {
            issues.Add(Issues.Of("unexpected_fixed_placement",
                $"{PyFormat.Str(key.Items[1])} in {PyFormat.Str(key.Items[0])} at {Where(key)} {PyFormat.Str(key.Items[5])}"));
        }
    }

    /// <summary>A group travels together or not at all: one container, and never half-packed. O(placements + g log g).</summary>
    public static void CheckGroups(JsonValue request, JsonValue result, List<ResultIssue> issues)
    {
        var rawItems = Json.Items(Json.At(request, "items"), "request.items");
        var groups = new Ordered<JsonValue>();
        foreach (var raw in rawItems)
        {
            if (Json.Get(raw, "group") is not JsonNull)
            {
                groups.Set(Json.At(raw, "id"), Json.At(raw, "group"));
            }
        }
        if (groups.Count == 0)
        {
            return;
        }
        var located = new Ordered<Ordered<bool>>();
        var packed = new Ordered<BigInteger>();
        foreach (var container in Json.Items(Json.At(result, "containers"), "result.containers"))
        {
            foreach (var placement in Json.Items(Json.At(container, "placements"), "placements"))
            {
                if (groups.GetOrDefault(Json.At(placement, "item_type")) is not { } group)
                {
                    continue;
                }
                var places = located.GetOrDefault(group) ?? new Ordered<bool>();
                places.Set(Json.At(container, "id"), true);
                located.Set(group, places);
                packed.Set(group, (packed.TryGet(group, out var count) ? count : 0) + 1);
            }
        }
        var expected = new Ordered<BigInteger>();
        foreach (var raw in rawItems)
        {
            var group = Json.Get(raw, "group");
            if (group is not JsonNull)
            {
                expected.Set(group, (expected.TryGet(group, out var sum) ? sum : 0) + Json.Int(Json.Get(raw, "quantity", new JsonInteger(1)), "quantity"));
            }
        }
        foreach (var group in Json.Sorted(located.Keys))
        {
            var places = located.GetOrDefault(group)!;
            if (places.Count > 1)
            {
                var where = places.Keys.Select(place => Json.Text(place, "a container id")).Order(CodePointComparer.Instance);
                issues.Add(Issues.Of("group_split", $"{PyFormat.Str(group)}: {string.Join(", ", where)}"));
            }
            var placed = packed.TryGet(group, out var count) ? count : 0;
            var wanted = expected.TryGet(group, out var total) ? total : 0;
            if (placed != wanted)
            {
                issues.Add(Issues.Of("group_partially_packed", $"{PyFormat.Str(group)}: {placed} of {wanted} placed"));
            }
        }
    }

    /// <summary>The reported solver, before any <c>:</c> variant, is one the request selected. O(solvers).</summary>
    public static void CheckSolverSelection(JsonValue request, JsonValue result, List<ResultIssue> issues)
    {
        var selected = Json.Get(Json.Get(request, "configuration", Json.EmptyObject), "solvers", Json.EmptyList);
        if (!Json.Truthy(selected))
        {
            return;
        }
        var reported = Json.Get(Json.At(result, "algorithm"), "solver");
        var solver = reported is JsonString text ? new JsonString(text.Value.Split(':', 2)[0]) : reported;
        if (!Json.In(solver, selected))
        {
            issues.Add(Issues.Of("solver_selection_mismatch", $"reported {PyFormat.Repr(reported)}, requested one of {PyFormat.Repr(selected)}"));
        }
    }

    /// <summary>Adds a key to a set by Python's tuple equality; an equal key already present stays.</summary>
    private static void Add(Ordered<JsonArray> keys, ImmutableArray<JsonValue> key)
    {
        var tuple = new JsonArray(key);
        if (!keys.Has(KeyOf(tuple)))
        {
            keys.Set(KeyOf(tuple), tuple);
        }
    }

    /// <summary>A tuple's hash identity, as a string key every element contributes to.</summary>
    private static JsonString KeyOf(JsonArray key) => new(string.Join("\u0000", key.Items.Select(Json.HashKey)));

    private static ImmutableArray<JsonArray> Sorted(IEnumerable<JsonArray> keys) => [.. Json.Sorted(keys).Cast<JsonArray>()];

    /// <summary><c>key[2:5]</c>, the position as a tuple.</summary>
    private static string Where(JsonArray key) => PyFormat.ValueTuple(key.Items.Skip(2).Take(3));
}
