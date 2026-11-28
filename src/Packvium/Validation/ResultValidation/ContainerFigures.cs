using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// A packed container's limits and reported figures, recomputed from its placements: payload,
/// item and tag counts, used volume and void-fill reserve, centre of mass and axle reactions.
/// </summary>
internal static class ContainerFigures
{
    /// <summary>Payload, item count and per-tag limits. O(p · t) for t tag limits.</summary>
    public static void CheckLimits(JsonValue packed, ImmutableArray<JsonValue> placements, ContainerSpec spec, Ordered<ItemSpec> items,
        BigInteger payload, List<ResultIssue> issues)
    {
        if (spec.MaxPayload is { } maxPayload && payload > maxPayload)
        {
            issues.Add(Issues.Of("payload_exceeded", $"{Extents.PackedId(packed)}: {payload} > {maxPayload} ticks"));
        }
        var reported = Json.Get(Json.Get(packed, "payload_weight", Json.EmptyObject), "ticks");
        if (reported is not JsonNull && !Json.EqualsInt(reported, payload))
        {
            issues.Add(Issues.Of("payload_weight_mismatch", $"{Extents.PackedId(packed)}: reported {PyFormat.Str(reported)}, computed {payload}"));
        }
        if (spec.MaxItems is not JsonNull && Json.CompareInt(spec.MaxItems, placements.Length) < 0)
        {
            issues.Add(Issues.Of("max_items_exceeded", $"{Extents.PackedId(packed)}: {placements.Length} > {PyFormat.Str(spec.MaxItems)}"));
        }
        foreach (var (tag, limit) in spec.TagLimits)
        {
            var count = placements.Count(placement => items.GetOrDefault(Json.At(placement, "item_type")) is { } item && item.Tags.Contains(tag));
            if (count > limit)
            {
                issues.Add(Issues.Of("tag_count_exceeded", $"{Extents.PackedId(packed)}: {tag} count {count} > limit {limit}"));
            }
        }
    }

    /// <summary>
    /// The used volume (every placement through the envelope its id resolves to, less each valid
    /// nesting overlap) and the void-fill reserve by the ppm rule. O(p² + p · v⁴) for hulls of v vertices.
    /// </summary>
    public static void CheckVolumes(JsonValue packed, ImmutableArray<JsonValue> placements, ContainerSpec spec, Ordered<ItemSpec> items,
        Ordered<Entry> byId, ImmutableArray<BigInteger> loads, List<ResultIssue> issues)
    {
        var used = BigInteger.Zero;
        foreach (var placement in placements)
        {
            var itemId = Json.At(placement, "item_id");
            var position = byId.Position(itemId)!.Value;
            used += Solids.OccupiedVolume(placement, byId.ValueAt(position).Envelope, items.GetOrDefault(Json.At(placement, "item_type")), loads[position]);
        }
        var entries = byId.Values.ToImmutableArray();
        for (var first = 0; first < entries.Length; first++)
        {
            for (var second = first + 1; second < entries.Length; second++)
            {
                if (Solids.ValidNesting(entries[first], entries[second]))
                {
                    var left = entries[first];
                    used -= left.Spec!.NestingHeight!.Value * left.Envelope.Length * left.Envelope.Width;
                }
            }
        }
        var reportedVolume = Json.Get(packed, "used_volume_ticks3");
        if (reportedVolume is not JsonNull && Json.PyInt(reportedVolume, "used_volume_ticks3") != used)
        {
            issues.Add(Issues.Of("used_volume_mismatch", $"{Extents.PackedId(packed)}: reported {PyFormat.Str(reportedVolume)}, computed {used}"));
        }
        var volume = spec.Volume;
        var reserved = PartsPerMillion.Reserve(volume, PartsPerMillion.Of(spec.VoidFillReserveRatio));
        var reportedReserve = Json.Get(packed, "void_fill_reserve_ticks3");
        if (reportedReserve is not JsonNull && Json.PyInt(reportedReserve, "void_fill_reserve_ticks3") != reserved)
        {
            issues.Add(Issues.Of("void_fill_reserve_mismatch", $"{Extents.PackedId(packed)}: reported {PyFormat.Str(reportedReserve)}, computed {reserved}"));
        }
        else if (!reserved.IsZero && reportedReserve is JsonNull)
        {
            issues.Add(Issues.Of("void_fill_reserve_mismatch", $"{Extents.PackedId(packed)}: non-zero reserve {reserved} is not reported"));
        }
        if (used > volume - reserved)
        {
            issues.Add(Issues.Of("void_fill_reserve_exceeded", $"{Extents.PackedId(packed)}: used {used} > usable {volume - reserved}"));
        }
    }

    /// <summary>The larger horizontal centre-of-mass offset from the container's centre, in ppm of its side. O(p).</summary>
    public static void CheckCentreOfMass(JsonValue packed, ImmutableArray<JsonValue> placements, ContainerSpec spec, Ordered<ItemSpec> items,
        BigInteger totalWeight, List<ResultIssue> issues)
    {
        var expected = BigInteger.Zero;
        if (!totalWeight.IsZero)
        {
            BigInteger Offset(BigInteger doubled, BigInteger side)
            {
                var centred = totalWeight * side;
                return Exact.FloorDiv(BigInteger.Abs(doubled - centred) * PartsPerMillion.Scale, centred);
            }

            expected = BigInteger.Max(
                Offset(DoubledMoment(placements, items, "x", "length"), spec.Inner[0]),
                Offset(DoubledMoment(placements, items, "y", "width"), spec.Inner[1]));
        }
        var reported = Json.Get(packed, "centre_of_mass_offset_ppm");
        if (!Json.EqualsInt(reported, expected))
        {
            issues.Add(Issues.Of("centre_of_mass_mismatch", $"{Extents.PackedId(packed)}: reported {PyFormat.Str(reported)}, computed {expected}"));
        }
    }

    /// <summary>
    /// The two axle reactions as exact fractions over a common denominator, against what the
    /// container reports and each axle's limit. O(p).
    /// </summary>
    public static void CheckAxles(JsonValue packed, ImmutableArray<JsonValue> placements, ContainerSpec spec, Ordered<ItemSpec> items,
        BigInteger totalWeight, List<ResultIssue> issues)
    {
        if (spec.Axles is not { } axles)
        {
            if (Json.Contains(packed, "axle_reactions"))
            {
                issues.Add(Issues.Of("axle_reaction_mismatch", $"{Extents.PackedId(packed)}: reactions reported without configured axles"));
            }
            return;
        }
        if (axles.Length != 2)
        {
            throw MalformedResultException.ShapeBreak("axles must name exactly a front and a rear axle");
        }
        var (front, rear) = (axles[0], axles[1]);
        var denominator = 2 * (rear.Position - front.Position);
        var gross = spec.TareWeight + totalWeight;
        var doubledWeightedX = spec.TareWeight * spec.Inner[0] + DoubledMoment(placements, items, "x", "length");
        var frontNumerator = 2 * gross * rear.Position - doubledWeightedX;
        var rearNumerator = doubledWeightedX - 2 * gross * front.Position;
        var expected = new JsonObject([
            new KeyValuePair<string, JsonValue>("basis", new JsonString("gross")),
            new KeyValuePair<string, JsonValue>("denominator", new JsonString(PyFormat.Int(denominator))),
            new KeyValuePair<string, JsonValue>("front_numerator", new JsonString(PyFormat.Int(frontNumerator))),
            new KeyValuePair<string, JsonValue>("rear_numerator", new JsonString(PyFormat.Int(rearNumerator))),
        ]);
        var reported = Json.Get(packed, "axle_reactions");
        if (!Json.PyEquals(reported, expected))
        {
            issues.Add(Issues.Of("axle_reaction_mismatch",
                $"{Extents.PackedId(packed)}: reported {PyFormat.Str(reported)}, computed {PyFormat.Repr(expected)}"));
        }
        if ((front.MaxLoad is { } frontLimit && frontNumerator > frontLimit * denominator)
            || (rear.MaxLoad is { } rearLimit && rearNumerator > rearLimit * denominator))
        {
            issues.Add(Issues.Of("axle_overloaded", $"{Extents.PackedId(packed)}"));
        }
    }

    /// <summary><c>Σ weight · (2 · position + extent)</c> along one axis, over the declared item types.</summary>
    private static BigInteger DoubledMoment(ImmutableArray<JsonValue> placements, Ordered<ItemSpec> items, string axis, string side)
    {
        var moment = BigInteger.Zero;
        foreach (var placement in placements)
        {
            if (items.GetOrDefault(Json.At(placement, "item_type")) is not { } spec)
            {
                continue;
            }
            var position = Extents.Tick(Json.At(placement, "position"), axis);
            var extent = Extents.Tick(Json.At(placement, "dimensions"), side);
            moment += spec.Weight * (2 * position + extent);
        }
        return moment;
    }
}
