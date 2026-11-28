using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// A made-to-measure container's box, recomputed from its placements alone: the machine grid's
/// smallest box around the tight extent, its outer size, cost, tare and gross weight.
/// </summary>
internal static class MadeToMeasure
{
    /// <summary>One square metre in length ticks squared.</summary>
    public static readonly BigInteger SquareMetreTicks = BigInteger.Pow(TickUnits.TicksPerMillimetre * 1000, 2);

    /// <summary>
    /// Checks the reported box and returns the spec every later check uses: the recomputed box,
    /// never the reported one. O(placements); every product is an exact integer.
    /// </summary>
    public static ContainerSpec Check(JsonValue packed, bool compact, ContainerSpec spec, Ordered<ItemSpec> items, BigInteger clearance,
        List<ResultIssue> issues)
    {
        var containerId = PyFormat.Str(Json.Get(packed, "id", new JsonString("?")));
        var block = Json.Get(packed, "made_to_measure");
        if (spec.MadeToMeasure is not { } machine)
        {
            if (block is not JsonNull)
            {
                issues.Add(Issues.Of("made_to_measure_unexpected", $"{containerId}"));
            }
            return spec;
        }
        if (block is not JsonObject)
        {
            issues.Add(Issues.Of("made_to_measure_missing", $"{containerId}"));
            block = Json.EmptyObject;
        }
        if (compact)
        {
            issues.Add(Issues.Of("lattice_summary_on_made_to_measure", $"{containerId}"));
        }
        var achieved = Extents.AchievedExtent(packed, clearance);
        var fabricated = Enumerable.Range(0, 3)
            .Select(axis => FabricatedSide(achieved[axis], machine.Minimum[axis], machine.Increment[axis])).ToImmutableArray();
        if (Enumerable.Range(0, 3).Any(axis => fabricated[axis] > spec.Inner[axis]))
        {
            issues.Add(Issues.Of("fabricated_dimensions_exceed_machine",
                $"{containerId}: {PyFormat.IntTuple(fabricated)} beyond {PyFormat.IntTuple(spec.Inner)}"));
        }
        var reportedInner = Extents.ReportedTicks(Json.Get(packed, "inner_dimensions"));
        if (!Extents.Matches(reportedInner, fabricated))
        {
            issues.Add(Issues.Of("fabricated_dimensions_mismatch",
                $"{containerId}: reported {PyFormat.OptionalTuple(reportedInner)}, computed {PyFormat.IntTuple(fabricated)}"));
        }
        var outer = Enumerable.Range(0, 3).Select(axis => fabricated[axis] + machine.Allowance[axis]).ToImmutableArray();
        var reportedOuter = Extents.ReportedTicks(Json.Get(packed, "outer_dimensions"));
        if (!Extents.Matches(reportedOuter, outer))
        {
            issues.Add(Issues.Of("outer_dimensions_mismatch",
                $"{containerId}: reported {PyFormat.OptionalTuple(reportedOuter)}, computed {PyFormat.IntTuple(outer)}"));
        }
        var cost = Json.Int(spec.CostMinor, "cost_minor")
            + PerSquareMetreCeiling(Json.Int(machine.PricePerSquareMetreMinor, "cost_per_square_metre_minor"), fabricated);
        Compare(Json.Get(block, "cost_minor"), cost, "made_to_measure_cost_mismatch", containerId, issues);
        var tare = spec.TareWeight + PerSquareMetreCeiling(machine.TarePerSquareMetre, fabricated);
        Compare(Extents.NestedTicks(block, "tare_weight"), tare, "made_to_measure_tare_mismatch", containerId, issues);
        var gross = tare + Payload(packed, items);
        Compare(Extents.NestedTicks(packed, "gross_weight"), gross, "gross_weight_mismatch", containerId, issues);
        return spec with { Inner = fabricated, Outer = outer, CostMinor = new JsonInteger(cost), TareWeight = tare };
    }

    /// <summary>The summed weight of every placement whose type the request declares. O(placements).</summary>
    public static BigInteger Payload(JsonValue packed, Ordered<ItemSpec> items)
    {
        var payload = BigInteger.Zero;
        foreach (var placement in Json.Items(Json.At(packed, "placements"), "placements"))
        {
            if (items.GetOrDefault(Json.At(placement, "item_type")) is { } spec)
            {
                payload += spec.Weight;
            }
        }
        return payload;
    }

    private static void Compare(JsonValue reported, BigInteger computed, string code, string containerId, List<ResultIssue> issues)
    {
        if (!Json.EqualsInt(reported, computed))
        {
            issues.Add(Issues.Of(code, $"{containerId}: reported {PyFormat.Str(reported)}, computed {computed}"));
        }
    }

    /// <summary>The smallest size on the machine grid <c>minimum + k * increment</c> that is at least <c>achieved</c>.</summary>
    private static BigInteger FabricatedSide(BigInteger achieved, BigInteger minimum, BigInteger increment) =>
        minimum + Exact.CeilDiv(BigInteger.Max(achieved, minimum) - minimum, increment) * increment;

    /// <summary><c>ceil(rate * 2(LW + LH + WH) / 1 m²)</c>, the material and board rounding.</summary>
    private static BigInteger PerSquareMetreCeiling(BigInteger rate, ImmutableArray<BigInteger> sides)
    {
        var area = 2 * (sides[0] * sides[1] + sides[0] * sides[2] + sides[1] * sides[2]);
        return Exact.CeilDiv(rate * area, SquareMetreTicks);
    }
}
