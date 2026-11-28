using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Everything that follows from what rests on what: support ratio and ground contact, stackability,
/// top load and stacked-item limits, cumulative load, crush and stack density.
/// </summary>
internal static class Stacking
{
    /// <summary>Pascals per kilopascal times the 10^5 denominator of standard gravity, 9.80665 m/s².</summary>
    private static readonly BigInteger PressureDenominator = TickUnits.TicksPerKilogram * 100_000_000L;

    private static readonly BigInteger PressureNumerator = 980_665 * MadeToMeasure.SquareMetreTicks;

    /// <summary>
    /// Per raised entry, its supporters' contact against the ppm support rule and its ground-contact
    /// rule; per supporter, stackability, stacking groups and top load; per entry, the stacked-item
    /// limit over everything above it. O(e) plus O(n · d) for the d entries above each limited one.
    /// </summary>
    public static void CheckSupportAndLoad(ImmutableArray<Entry> entries, SupportGraph graph, Ratio globalSupport, List<ResultIssue> issues)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Spec is not { } spec || entry.Envelope.Z.IsZero)
            {
                continue;
            }
            var supporting = graph.Supporters[index];
            var required = globalSupport.CompareTo(spec.MinimumSupportRatio) < 0 ? spec.MinimumSupportRatio : globalSupport;
            GroundContact.Check(entry, supporting, entries, spec.GroundContactRule, issues);
            if (required.CompareTo(BigInteger.Zero) > 0)
            {
                var area = supporting.Aggregate(BigInteger.Zero, (sum, support) => sum + support.Area);
                if (area < PartsPerMillion.RequiredSupportArea(entry.Envelope.BaseArea, PartsPerMillion.Of(required)))
                {
                    var ratio = Ratio.Of(area, entry.Envelope.BaseArea);
                    issues.Add(Issues.Of("insufficient_support",
                        $"{entry.Label}: {PyFormat.Fixed6(ratio.ToDouble())} < {PyFormat.Fixed6(required.ToDouble())}"));
                }
            }
            foreach (var support in supporting)
            {
                CheckSupporter(entry, spec, entries[support.Lower], issues);
            }
        }
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Spec is not { MaxStackedItems: not JsonNull } spec)
            {
                continue;
            }
            var above = Above(graph, index);
            if (Json.CompareInt(spec.MaxStackedItems, above) < 0)
            {
                issues.Add(Issues.Of("max_stacked_items_exceeded", $"{entries[index].Label}: {above} above > limit {PyFormat.Str(spec.MaxStackedItems)}"));
            }
        }
    }

    private static void CheckSupporter(Entry entry, ItemSpec spec, Entry lower, List<ResultIssue> issues)
    {
        if (lower.Spec is not { } lowerSpec)
        {
            return;
        }
        if (!lowerSpec.Stackable)
        {
            issues.Add(Issues.Of("non_stackable", $"{entry.Label} rests on {lower.Label}"));
        }
        else if (!lowerSpec.AcceptsStackingGroups.IsEmpty && !lowerSpec.AcceptsStackingGroups.Contains(spec.StackingGroup))
        {
            issues.Add(Issues.Of("stacking_group_incompatible", $"{entry.Label} rests on {lower.Label}"));
        }
        if (lowerSpec.MaxTopLoad is { } limit && spec.Weight > limit)
        {
            issues.Add(Issues.Of("top_load_exceeded", $"{entry.Label} ({spec.Weight} ticks) on {lower.Label} (limit {limit})"));
        }
    }

    /// <summary>How many entries rest on this one, however indirectly. O(entries above).</summary>
    private static int Above(SupportGraph graph, int index)
    {
        var descendants = new HashSet<int>();
        var pending = new Stack<int>(graph.DirectAbove[index]);
        while (pending.Count > 0)
        {
            var above = pending.Pop();
            if (descendants.Add(above))
            {
                foreach (var next in graph.DirectAbove[above])
                {
                    pending.Push(next);
                }
            }
        }
        return descendants.Count;
    }

    /// <summary>
    /// The cumulative weight each entry bears from everything resting on it, split across its
    /// supporters by contact area and conserved exactly (the remainder goes to the last supporter),
    /// processed top-down by descending envelope ceiling. O(n log n + e).
    /// </summary>
    public static ImmutableArray<BigInteger> PropagateLoad(ImmutableArray<Entry> entries, SupportGraph graph)
    {
        var order = Json.StableSort(Enumerable.Range(0, entries.Length).ToList(), (left, right) =>
        {
            var (upper, lower) = (entries[left].Envelope, entries[right].Envelope);
            var byCeiling = lower.Z2.CompareTo(upper.Z2);
            var byFloor = byCeiling != 0 ? byCeiling : lower.Z.CompareTo(upper.Z);
            return byFloor != 0 ? byFloor : Json.Compare(entries[left].Id, entries[right].Id);
        });
        var loads = new BigInteger[entries.Length];
        foreach (var upper in order)
        {
            if (entries[upper].Spec is not { } spec)
            {
                continue;
            }
            var supports = graph.Supporters[upper];
            var totalArea = supports.Aggregate(BigInteger.Zero, (sum, support) => sum + support.Area);
            if (totalArea.IsZero)
            {
                continue;
            }
            var downward = spec.Weight + loads[upper];
            var assigned = BigInteger.Zero;
            for (var position = 0; position < supports.Length; position++)
            {
                var share = position == supports.Length - 1
                    ? downward - assigned
                    : Exact.FloorDiv(downward * supports[position].Area, totalArea);
                assigned += share;
                loads[supports[position].Lower] += share;
            }
        }
        return [.. loads];
    }

    /// <summary>
    /// The pressure a footprint carries, in kPa, exactly:
    /// <c>load · g · (ticks per metre)² / (ticks per kg · g denominator · Pa per kPa · area)</c>.
    /// </summary>
    public static Ratio Pressure(BigInteger load, BigInteger baseArea) => Ratio.Of(load * PressureNumerator, PressureDenominator * baseArea);

    /// <summary>No compressible item carries more pressure than it declared; the boundary is inclusive. O(n).</summary>
    public static void CheckCrush(JsonValue packed, ImmutableArray<Entry> entries, ImmutableArray<BigInteger> loads, List<ResultIssue> issues)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Spec is not { MaxCompressionPressureKpa: not JsonNull and var declared })
            {
                continue;
            }
            var limit = Json.Int(declared, "max_compression_pressure_kpa");
            var pressure = Pressure(loads[index], entries[index].Envelope.BaseArea);
            if (pressure.CompareTo(limit) > 0)
            {
                issues.Add(Issues.Of("crush_violation",
                    $"{entries[index].Label} in {Extents.PackedId(packed)}: {PyFormat.Fixed6(pressure.ToDouble())} kPa exceeds {PyFormat.Str(declared)} kPa"));
            }
        }
    }

    /// <summary>Weight per square metre of each entry's own base, at every level of the stack, within the container's limit. O(n).</summary>
    public static void CheckStackDensity(JsonValue packed, ImmutableArray<Entry> entries, ImmutableArray<BigInteger> loads, BigInteger maxDensity,
        List<ResultIssue> issues)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Spec is not { } spec)
            {
                continue;
            }
            var total = spec.Weight + loads[index];
            var baseArea = entries[index].Envelope.BaseArea;
            if (total * MadeToMeasure.SquareMetreTicks > maxDensity * baseArea)
            {
                issues.Add(Issues.Of("stack_density_exceeded",
                    $"{entries[index].Label} in {Extents.PackedId(packed)}: {total} ticks over {baseArea} ticks^2 exceeds {maxDensity} ticks/m^2"));
            }
        }
    }
}

/// <summary>
/// An item's <c>ground_contact_rule</c>: <c>covered</c> needs every base corner on a supporter,
/// <c>single</c> exactly one supporter, <c>multiple</c> at least two.
/// </summary>
internal static class GroundContact
{
    public static void Check(Entry entry, ImmutableArray<Support> supporting, ImmutableArray<Entry> entries, JsonValue rule, List<ResultIssue> issues)
    {
        if (rule is JsonNull || Json.IsText(rule, "free"))
        {
            return;
        }
        if (Json.IsText(rule, "covered"))
        {
            var envelope = entry.Envelope;
            var corners = new[] { (envelope.X, envelope.Y), (envelope.X2, envelope.Y), (envelope.X, envelope.Y2), (envelope.X2, envelope.Y2) };
            var covered = corners.All(corner => supporting.Any(support =>
            {
                var surface = entries[support.Lower].Envelope;
                return surface.X <= corner.Item1 && corner.Item1 <= surface.X2 && surface.Y <= corner.Item2 && corner.Item2 <= surface.Y2;
            }));
            if (!covered)
            {
                issues.Add(Issues.Of("ground_contact_violation", $"{entry.Label}: covered requires all four base corners"));
            }
        }
        else if (Json.IsText(rule, "single") && supporting.Length != 1)
        {
            issues.Add(Issues.Of("ground_contact_violation", $"{entry.Label}: single has {supporting.Length} supporters"));
        }
        else if (Json.IsText(rule, "multiple") && supporting.Length < 2)
        {
            issues.Add(Issues.Of("ground_contact_violation", $"{entry.Label}: multiple has {supporting.Length} supporters"));
        }
    }
}
