using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Placements as envelopes, and the sizes that follow from them: the tight extent a flat bag or a
/// made-to-measure box reports, and whether one envelope fits an empty container.
/// </summary>
internal static class Extents
{
    private static readonly ImmutableArray<string> Sides = ["length", "width", "height"];

    /// <summary>
    /// The physical box grown by the clearance margin. The position and dimensions blocks must exist;
    /// a block without integer tick coordinates is the reference's contract refusal, worded as it words it.
    /// </summary>
    public static Cuboid Envelope(JsonValue placement, BigInteger clearance)
    {
        var position = Json.At(placement, "position");
        var dimensions = Json.At(placement, "dimensions");
        try
        {
            return new Cuboid(
                Tick(position, "x") - clearance,
                Tick(position, "y") - clearance,
                Tick(position, "z") - clearance,
                Tick(dimensions, "length") + 2 * clearance,
                Tick(dimensions, "width") + 2 * clearance,
                Tick(dimensions, "height") + 2 * clearance);
        }
        catch (MalformedResultException missing)
        {
            throw MalformedResultException.ContractBreak($"placement is missing tick coordinates: {missing.Message}");
        }
    }

    /// <summary><c>block[key]["ticks"]</c> as an integer.</summary>
    public static BigInteger Tick(JsonValue block, string key) => Json.Int(Json.At(Json.At(block, key), "ticks"), "placement ticks");

    /// <summary>The largest envelope x2, y2 and z2 over a container's placements, or zeros for none. O(placements).</summary>
    public static ImmutableArray<BigInteger> AchievedExtent(JsonValue packed, BigInteger clearance)
    {
        var envelopes = Json.Items(Json.At(packed, "placements"), "placements").Select(placement => Envelope(placement, clearance)).ToList();
        return envelopes.Count == 0
            ? [0, 0, 0]
            : [envelopes.Max(box => box.X2), envelopes.Max(box => box.Y2), envelopes.Max(box => box.Z2)];
    }

    /// <summary>
    /// Whether one envelope of this size fits the empty container: its frame and, for a flat bag,
    /// both girth sums with the envelope alone at the origin.
    /// </summary>
    public static bool FitsEmpty(ContainerSpec container, ImmutableArray<BigInteger> size)
    {
        if (Enumerable.Range(0, 3).Any(axis => size[axis] > container.Inner[axis]))
        {
            return false;
        }
        return !container.FlatBag || (size[0] + size[2] <= container.Inner[0] && size[1] + size[2] <= container.Inner[1]);
    }

    /// <summary>
    /// <c>achieved_dimensions</c> is present exactly when the container declares a soft size, and
    /// equals the tight extent; a flat bag's extent must pass the girth rule. O(placements).
    /// </summary>
    public static void CheckAchievedExtent(JsonValue packed, ContainerSpec spec, BigInteger clearance, List<ResultIssue> issues)
    {
        var reported = Json.Get(packed, "achieved_dimensions");
        if (!spec.ReportsAchievedExtent)
        {
            if (reported is not JsonNull)
            {
                issues.Add(Issues.Of("achieved_dimensions_mismatch", $"{PackedId(packed)}: reported for a container of fixed size"));
            }
            return;
        }
        var extent = AchievedExtent(packed, clearance);
        var stated = reported is JsonObject
            ? Sides.Select(side => Json.Get(Json.Get(reported, side, Json.EmptyObject), "ticks")).ToImmutableArray()
            : (ImmutableArray<JsonValue>?)null;
        if (!Matches(stated, extent))
        {
            issues.Add(Issues.Of("achieved_dimensions_mismatch",
                $"{PackedId(packed)}: reported {PyFormat.OptionalTuple(stated)}, computed {PyFormat.IntTuple(extent)}"));
        }
        var (girthA, girthB) = (extent[0] + extent[2], extent[1] + extent[2]);
        if (spec.FlatBag && (girthA > spec.Inner[0] || girthB > spec.Inner[1]))
        {
            issues.Add(Issues.Of("bag_girth_exceeded",
                $"{PackedId(packed)}: a + c = {girthA}, b + c = {girthB}, flat size {spec.Inner[0]} x {spec.Inner[1]}"));
        }
    }

    /// <summary>
    /// <c>packed["id"]</c> as an issue prints it. The reference reads it only while formatting an
    /// issue, so a container without an id fails only once an issue needs to name it.
    /// </summary>
    public static string PackedId(JsonValue packed) => PyFormat.Str(Json.At(packed, "id"));

    /// <summary>Element-wise <c>stated == expected</c>; a missing tuple never matches.</summary>
    public static bool Matches(IReadOnlyList<JsonValue>? stated, ImmutableArray<BigInteger> expected) =>
        stated is not null && stated.Count == expected.Length && stated.Zip(expected).All(pair => Json.EqualsInt(pair.First, pair.Second));

    /// <summary><c>(value.get(key) or {}).get("ticks")</c> per side, or null when the value is not an object.</summary>
    public static ImmutableArray<JsonValue>? ReportedTicks(JsonValue value) =>
        value is JsonObject ? [.. Sides.Select(side => NestedTicks(value, side))] : null;

    /// <summary><c>(block.get(key) or {}).get("ticks")</c>.</summary>
    public static JsonValue NestedTicks(JsonValue block, string key) =>
        Json.Get(block, key) is var value && Json.Truthy(value) ? Json.Get(value, "ticks") : JsonNull.Instance;
}
