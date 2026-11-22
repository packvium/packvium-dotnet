using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>
/// The request's <c>fixed_placements</c> as JSON gives them (<c>fixed_placements.require_fixed_placement_shapes</c>),
/// or a refusal naming the first entry that is not the schema's shape. Nothing is coerced:
/// <c>"1"</c> and <c>true</c> are not instances, and a list is not a position. An absent or null
/// field is no fixed placements. O(f) for f entries.
/// </summary>
public static class FixedPlacementShapes
{
    public static readonly ImmutableArray<string> Orientations = ["LWH", "LHW", "WLH", "WHL", "HLW", "HWL"];

    private static readonly string[] RequiredFields = ["item_type", "container_type", "orientation"];

    private static readonly string[] Fields = [.. RequiredFields, "container_instance", "position"];

    private static readonly string[] Axes = ["x", "y", "z"];

    /// <summary>The entries, each checked, then read into domain placements in request order.</summary>
    public static ImmutableArray<FixedPlacement> Read(JsonValue? raw, string unit)
    {
        if (raw is null or JsonNull)
        {
            return [];
        }
        if (raw is not JsonArray entries)
        {
            throw Malformed("fixed_placements is a list", "/fixed_placements");
        }
        for (var index = 0; index < entries.Items.Length; index++)
        {
            RequireEntryShape(entries.Items[index], $"fixed_placements[{index}]", JsonPointer.Of("fixed_placements", index), unit);
        }
        return [.. entries.Items.Cast<JsonObject>().Select(entry => ReadEntry(entry, unit))];
    }

    /// <summary>
    /// A point is an object of <c>x</c>, <c>y</c> and <c>z</c> measures; units are the length
    /// parser's. <paramref name="error"/> builds the refusal from its message and the pointer of
    /// the bad value.
    /// </summary>
    public static void RequirePointShape(JsonValue? point, string where, string field, Func<string, string, Exception> error)
    {
        if (point is not JsonObject members)
        {
            throw error($"{where} is a point object", field);
        }
        var unknown = members.Keys.Where(key => !Axes.Contains(key, StringComparer.Ordinal))
            .Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw error($"{where} cannot carry {Names(unknown)}", field);
        }
        foreach (var axis in Axes)
        {
            if (members.Get(axis) is JsonNull or JsonBool or JsonArray)
            {
                throw error($"{where}.{axis} is a measure", field + JsonPointer.Of(axis));
            }
        }
    }

    /// <summary>A list of names as canonical JSON, the spelling every engine shares.</summary>
    public static string Names(IEnumerable<string> names) =>
        JsonValues.Spelling(new JsonArray([.. names.Select(name => (JsonValue)new JsonString(name))]));

    private static FixedPlacementException Malformed(string detail, string field) => new(detail, "malformed", field);

    private static void RequireEntryShape(JsonValue raw, string where, string field, string unit)
    {
        if (raw is not JsonObject entry)
        {
            throw Malformed($"{where} is an object", field);
        }
        var unknown = entry.Keys.Where(key => !Fields.Contains(key, StringComparer.Ordinal)).Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw Malformed($"{where} cannot carry {Names(unknown)}", field);
        }
        var missing = RequiredFields.Where(name => !entry.Contains(name)).ToList();
        if (missing.Count > 0)
        {
            throw Malformed($"{where} needs {Names(missing)}", field);
        }
        foreach (var name in (ReadOnlySpan<string>)["item_type", "container_type"])
        {
            if (entry.Require(name) is not JsonString { Value.Length: > 0 })
            {
                throw Malformed($"{where}.{name} is a non-empty string", field + JsonPointer.Of(name));
            }
        }
        if (entry.Require("orientation") is not JsonString { Value: var orientation } || !Orientations.Contains(orientation))
        {
            throw Malformed($"{where}.orientation is one of the six codes", field + "/orientation");
        }
        if (JsonValues.WholeNumber(entry.Get("container_instance") ?? new JsonInteger(1)) is not >= 1)
        {
            throw Malformed($"{where}.container_instance counts from 1", field + "/container_instance");
        }
        var position = entry.Get("position") ?? new JsonObject([]);
        RequirePointShape(position, $"{where}.position", field + "/position", Malformed);
        var point = (JsonObject)position;
        foreach (var axis in Axes)
        {
            if (point.Get(axis) is { } coordinate)
            {
                RequireCoordinate(coordinate, $"{where}.position.{axis}", $"{field}/position/{axis}", unit);
            }
        }
    }

    private static void RequireCoordinate(JsonValue value, string where, string field, string unit)
    {
        try
        {
            _ = Measures.Length(value, unit);
        }
        catch (Exception error) when (error is ArgumentException or ArithmeticException or FormatException
                                          or KeyNotFoundException)
        {
            throw error.Message.Contains("cannot be negative", StringComparison.Ordinal)
                ? Malformed($"{where} cannot be negative", field)
                : Malformed($"{where} is a measure", field);
        }
    }

    private static FixedPlacement ReadEntry(JsonObject entry, string unit)
    {
        var position = (JsonObject?)entry.Get("position") ?? new JsonObject([]);
        long Axis(string axis) => Measures.Length(position.Get(axis) ?? new JsonInteger(0), unit).Ticks;
        return new FixedPlacement(
            ((JsonString)entry.Require("item_type")).Value,
            ((JsonString)entry.Require("container_type")).Value,
            new Point(Axis("x"), Axis("y"), Axis("z")),
            Rotations.Parse(((JsonString)entry.Require("orientation")).Value),
            JsonValues.WholeNumber(entry.Get("container_instance") ?? new JsonInteger(1))!.Value);
    }
}
