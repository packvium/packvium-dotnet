using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace Packvium.Serialization;

/// <summary>
/// A parsed JSON document with the value types Python's <c>json</c> module produces: integers
/// are unbounded and distinct from floats (<c>1</c> versus <c>1.0</c>), and an object keeps its
/// keys in first-seen order with the last duplicate's value -- what a <c>dict</c> built by
/// assignment holds. Every reading rule downstream depends on those three facts.
/// </summary>
public abstract record JsonValue;

public sealed record JsonNull : JsonValue
{
    public static JsonNull Instance { get; } = new();
}

public sealed record JsonBool(bool Value) : JsonValue;

public sealed record JsonInteger(BigInteger Value) : JsonValue;

public sealed record JsonFloat(double Value) : JsonValue;

public sealed record JsonString(string Value) : JsonValue;

public sealed record JsonArray(ImmutableArray<JsonValue> Items) : JsonValue;

public sealed record JsonObject : JsonValue
{
    private readonly Dictionary<string, JsonValue> values;

    public JsonObject(IEnumerable<KeyValuePair<string, JsonValue>> members)
    {
        values = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        var keys = ImmutableArray.CreateBuilder<string>();
        foreach (var (key, value) in members)
        {
            if (!values.ContainsKey(key))
            {
                keys.Add(key);
            }
            values[key] = value;
        }
        Keys = keys.ToImmutable();
    }

    /// <summary>Keys in first-seen order.</summary>
    public ImmutableArray<string> Keys { get; }

    public IEnumerable<KeyValuePair<string, JsonValue>> Members =>
        Keys.Select(key => new KeyValuePair<string, JsonValue>(key, values[key]));

    public bool Contains(string key) => values.ContainsKey(key);

    /// <summary><c>dict.get(key)</c>: the value, or null when absent.</summary>
    public JsonValue? Get(string key) => values.GetValueOrDefault(key);

    /// <summary><c>dict.get(key)</c> with JSON <c>null</c> read as absent, as <c>raw.get(k) is None</c> does.</summary>
    public JsonValue? GetPresent(string key) => Get(key) is JsonNull ? null : Get(key);

    /// <summary><c>dict[key]</c>: the value, or Python's <c>KeyError</c>.</summary>
    public JsonValue Require(string key) =>
        values.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"'{key}'");
}
