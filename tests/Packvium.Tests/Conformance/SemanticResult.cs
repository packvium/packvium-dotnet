using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Tests.Conformance;

/// <summary>
/// <c>conformance/canonical.py</c> at v1.0.0: the projection of a raw result onto the fields the
/// contract promises are language-independent, and the byte-stable string it is compared as.
/// </summary>
public static class SemanticResult
{
    /// <summary>Sorted keys, compact separators, no floats anywhere in the tree.</summary>
    public static string Canonicalize(JsonValue value)
    {
        RejectFloats(value);
        return PythonJson.Dumps(value, sortKeys: true);
    }

    public static JsonObject Project(JsonObject result)
    {
        var containers = Items(result.Require("containers")).Select(c => SemanticContainer((JsonObject)c))
            .OrderBy(Canonicalize, CodePointComparer.Instance);
        var unpacked = Items(result.Require("unpacked_items")).Select(u => Pick((JsonObject)u, "item_type", "reason",
                "details", "proof"))
            .OrderBy(Canonicalize, CodePointComparer.Instance);
        return Object(
            Field(result, "status"), Field(result, "feasibility"), Field(result, "optimality"), Field(result, "complete"),
            Field(result, "objective"), Field(result, "summary"), Field(result, "score"),
            new("containers", new JsonArray([.. containers])),
            new("unpacked_items", new JsonArray([.. unpacked])));
    }

    private static JsonObject SemanticContainer(JsonObject container)
    {
        var placements = Items(container.Require("placements"))
            .Select(p => Pick((JsonObject)p, "item_type", "position", "dimensions", "orientation", "support_ratio",
                "top_load"))
            .OrderBy(PlacementKey, PlacementKeyComparer.Instance);
        var fields = new List<KeyValuePair<string, JsonValue>>
        {
            Field(container, "container_type"), Field(container, "inner_dimensions"),
            Field(container, "outer_dimensions"), Field(container, "payload_weight"), Field(container, "gross_weight"),
            Field(container, "used_volume_ticks3"), Field(container, "volume_utilization"),
            Field(container, "centre_of_mass_offset_ppm"),
        };
        if (container.Contains("axle_reactions"))
        {
            fields.Add(Field(container, "axle_reactions"));
        }
        fields.Add(Field(container, "void_fill_reserve_ticks3"));
        fields.Add(new("placements", new JsonArray([.. placements])));
        return new JsonObject(fields);
    }

    private static (string, string, System.Numerics.BigInteger[]) PlacementKey(JsonObject placement)
    {
        static System.Numerics.BigInteger Ticks(JsonObject parent, string group, string axis) =>
            ((JsonInteger)((JsonObject)((JsonObject)parent.Require(group)).Require(axis)).Require("ticks")).Value;
        return (((JsonString)placement.Require("item_type")).Value, ((JsonString)placement.Require("orientation")).Value,
        [
            Ticks(placement, "position", "x"), Ticks(placement, "position", "y"), Ticks(placement, "position", "z"),
            Ticks(placement, "dimensions", "length"), Ticks(placement, "dimensions", "width"),
            Ticks(placement, "dimensions", "height"),
        ]);
    }

    private sealed class PlacementKeyComparer : IComparer<(string, string, System.Numerics.BigInteger[])>
    {
        public static PlacementKeyComparer Instance { get; } = new();

        public int Compare((string, string, System.Numerics.BigInteger[]) x, (string, string, System.Numerics.BigInteger[]) y)
        {
            var order = CodePointComparer.Instance.Compare(x.Item1, y.Item1);
            if (order == 0)
            {
                order = CodePointComparer.Instance.Compare(x.Item2, y.Item2);
            }
            for (var index = 0; order == 0 && index < x.Item3.Length; index++)
            {
                order = x.Item3[index].CompareTo(y.Item3[index]);
            }
            return order;
        }
    }

    private static void RejectFloats(JsonValue value)
    {
        switch (value)
        {
            case JsonFloat number:
                throw new ArgumentException($"canonical JSON forbids float values, found {PythonFloat.Repr(number.Value)}");
            case JsonArray array:
                foreach (var item in array.Items)
                {
                    RejectFloats(item);
                }
                break;
            case JsonObject obj:
                foreach (var member in obj.Members)
                {
                    RejectFloats(member.Value);
                }
                break;
        }
    }

    private static ImmutableArray<JsonValue> Items(JsonValue value) => ((JsonArray)value).Items;

    private static KeyValuePair<string, JsonValue> Field(JsonObject source, string key) => new(key, source.Require(key));

    private static JsonObject Pick(JsonObject source, params string[] keys) =>
        new(keys.Select(key => Field(source, key)));

    private static JsonObject Object(params KeyValuePair<string, JsonValue>[] members) => new(members);
}
