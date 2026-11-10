using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Commerce;

/// <summary>
/// Strict shape checks for the commerce wire format: a missing required key and an unknown
/// extra key are both input errors, a boolean is never an integer, and an omitted optional
/// field and an explicit <c>null</c> mean the same thing.
/// </summary>
internal static class CommerceJson
{
    public static CommerceInputException Fail(string path, string message) => new($"{path}: {message}");

    public static JsonObject Mapping(JsonValue? value, string path) =>
        value as JsonObject ?? throw Fail(path, "expected an object");

    public static ImmutableArray<JsonValue> Sequence(JsonValue? value, string path) =>
        value is JsonArray array ? array.Items : throw Fail(path, "expected a list");

    public static BigInteger Integer(JsonValue? value, string path) =>
        value is JsonInteger integer ? integer.Value : throw Fail(path, "expected an exact integer");

    public static string Text(JsonValue? value, string path) =>
        value is JsonString text ? text.Value : throw Fail(path, "expected a string");

    /// <summary>The value, or <paramref name="fallback"/> when absent or JSON null.</summary>
    public static JsonValue Default(JsonObject fields, string key, JsonValue fallback) =>
        fields.Get(key) is { } value and not JsonNull ? value : fallback;

    public static JsonValue? Optional(JsonObject fields, string key) => fields.GetPresent(key);

    public static void Keys(JsonObject value, string path, string[] required, string[]? optional = null)
    {
        var missing = required.Where(key => !value.Contains(key)).Order(CodePointComparer.Instance).ToList();
        if (missing.Count > 0)
        {
            throw Fail(path, $"missing required key(s) [{string.Join(", ", missing.Select(PythonText.Repr))}]");
        }
        var allowed = required.Concat(optional ?? []).ToHashSet(StringComparer.Ordinal);
        var unknown = value.Keys.Where(key => !allowed.Contains(key)).Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw Fail(path, $"unrecognised key(s) [{string.Join(", ", unknown.Select(PythonText.Repr))}]");
        }
    }

    /// <summary>Exactly one of the names is present and not null; returns it.</summary>
    public static string ExactlyOne(JsonObject value, string path, string[] names)
    {
        var present = names.Where(name => value.GetPresent(name) is not null).ToList();
        return present.Count == 1
            ? present[0]
            : throw Fail(path, $"expected exactly one of [{string.Join(", ", names.Select(PythonText.Repr))}]");
    }

    /// <summary>A model's own validation, relabelled as an input error at the API boundary.</summary>
    public static T Model<T>(string path, Func<T> build)
    {
        try
        {
            return build();
        }
        catch (ArgumentException error)
        {
            throw Fail(path, error.Message);
        }
        catch (CatalogException error)
        {
            throw Fail(path, error.Message);
        }
    }

    public static int Version(BigInteger number) => checked((int)number);

    public static JsonInteger Number(BigInteger value) => new(value);

    public static JsonString String(string value) => new(value);

    public static JsonObject Object(params (string Key, JsonValue Value)[] members) =>
        new(members.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)));
}
