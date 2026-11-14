using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>Whole-stack load rules over a support graph. Each is O(n log n + E) for n units and E edges.</summary>
public static class LoadPropagation
{
    /// <summary>One square metre in length ticks squared, the area a stack density is expressed per.</summary>
    public static readonly Int128 SquareMetreTicks = (Int128)Length.Mm("1000").Ticks * Length.Mm("1000").Ticks;

    /// <summary>
    /// Weight borne by each unit, propagated down the whole stack and split by contact area;
    /// the integer remainder goes to the last supporter so the total is conserved exactly.
    /// </summary>
    public static long[] TopLoads(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null)
    {
        graph ??= new LoadSupportGraph(units);
        var loads = new long[units.Count];
        foreach (var upper in graph.DescendingIndices())
        {
            var supports = graph.Supporters(upper);
            var totalArea = supports.Aggregate(Int128.Zero, (total, edge) => total + edge.Area);
            if (totalArea == 0)
            {
                continue;
            }
            var downward = checked(units[upper].WeightTicks + loads[upper]);
            long assigned = 0;
            for (var position = 0; position < supports.Length; position++)
            {
                var share = position == supports.Length - 1
                    ? downward - assigned
                    : IntMath.ToLong(checked(downward * supports[position].Area) / totalArea);
                assigned += share;
                loads[supports[position].Index] = checked(loads[supports[position].Index] + share);
            }
        }
        return loads;
    }

    /// <summary>First unit whose bearing limit is exceeded.</summary>
    public static (string, string)? Overloaded(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null)
    {
        if (units.All(unit => unit.MaxTopLoadTicks is null))
        {
            return null;
        }
        var loads = TopLoads(units, graph);
        for (var index = 0; index < units.Count; index++)
        {
            if (units[index].MaxTopLoadTicks is { } limit && loads[index] > limit)
            {
                return ("top_load_exceeded", units[index].Label);
            }
        }
        return null;
    }

    /// <summary>First compressible unit carrying more pressure than it declared it can take.</summary>
    public static (string, string)? Crushed(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null)
    {
        if (units.All(unit => unit.MaxCompressionPressureKpa is null))
        {
            return null;
        }
        var loads = TopLoads(units, graph);
        for (var index = 0; index < units.Count; index++)
        {
            if (units[index].MaxCompressionPressureKpa is not { } limit)
            {
                continue;
            }
            var box = units[index].Box;
            var footprint = (Int128)(box.X2 - box.Origin.X) * (box.Y2 - box.Origin.Y);
            if (Compression.AppliedPressure(new Weight(loads[index]), footprint).ExceedsKpa(limit))
            {
                return ("crush_violation", units[index].Label);
            }
        }
        return null;
    }

    /// <summary>Everything resting anywhere above each unit, following the support graph upward.</summary>
    public static HashSet<int>[] RestingAbove(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null)
    {
        graph ??= new LoadSupportGraph(units);
        var memo = new HashSet<int>?[units.Count];
        HashSet<int> Above(int index)
        {
            if (memo[index] is { } cached)
            {
                return cached;
            }
            var result = new HashSet<int>();
            foreach (var child in graph.Children(index))
            {
                result.Add(child);
                result.UnionWith(Above(child));
            }
            return memo[index] = result;
        }
        return [.. Enumerable.Range(0, units.Count).Select(Above)];
    }

    public static int[] StackedCounts(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null) =>
        [.. RestingAbove(units, graph).Select(above => above.Count)];

    /// <summary>
    /// First item due at an earlier stop with a later-stop item resting above it. Stops use
    /// <see cref="Stops.RidesTheWholeRoute"/> for an item that is never unloaded.
    /// </summary>
    public static (string, string)? RouteOrderViolated(IReadOnlyList<LoadUnit> units, IReadOnlyList<long> stops)
    {
        if (stops.All(stop => stop == Stops.RidesTheWholeRoute))
        {
            return null;
        }
        var above = RestingAbove(units);
        for (var index = 0; index < units.Count; index++)
        {
            // Python iterates a frozenset; the first later-stop item found is reported, so the
            // lowest index is taken here -- the order CPython's small-int sets iterate in.
            foreach (var upper in above[index].Order())
            {
                if (stops[upper] > stops[index])
                {
                    return ("unloading_order_violation", $"{units[index].Label} blocked by {units[upper].Label}");
                }
            }
        }
        return null;
    }

    public static (string, string)? StackLimitExceeded(IReadOnlyList<LoadUnit> units, LoadSupportGraph? graph = null)
    {
        if (units.All(unit => unit.MaxStackedItems is null))
        {
            return null;
        }
        var counts = StackedCounts(units, graph);
        for (var index = 0; index < units.Count; index++)
        {
            if (units[index].MaxStackedItems is { } limit && counts[index] > limit)
            {
                return ("stacked_item_limit_exceeded", units[index].Label);
            }
        }
        return null;
    }

    /// <summary>First unit whose cumulative load, its own weight included, crushes its own footprint.</summary>
    public static (string, string)? StackDensityExceeded(IReadOnlyList<LoadUnit> units, long? maxDensityTicks,
        LoadSupportGraph? graph = null)
    {
        if (maxDensityTicks is not { } density)
        {
            return null;
        }
        var loads = TopLoads(units, graph);
        for (var index = 0; index < units.Count; index++)
        {
            var total = (Int128)units[index].WeightTicks + loads[index];
            if (checked(total * SquareMetreTicks) > checked(density * units[index].Box.Dimensions.BaseArea))
            {
                return ("stack_density_exceeded", units[index].Label);
            }
        }
        return null;
    }

    /// <summary>A direct-support edge may never originate at a non-stackable item.</summary>
    public static ConstraintResult? NonStackableFailure(IReadOnlyList<Placement> placements, ItemInstance item,
        LoadSupportGraph graph, int candidateIndex)
    {
        foreach (var edge in graph.Supporters(candidateIndex))
        {
            var supporter = placements[edge.Index];
            if (!supporter.Instance.Item.Stackable)
            {
                return ConstraintResult.Reject("non_stackable", supporter.Instance.Item.Id);
            }
        }
        if (!item.Item.Stackable && !graph.Children(candidateIndex).IsEmpty)
        {
            return ConstraintResult.Reject("non_stackable", item.Item.Id);
        }
        return null;
    }
}

/// <summary>Route stops as ordering values: an item with no stop rides the whole route and blocks every stop.</summary>
public static class Stops
{
    /// <summary>Above every real stop, which the schema caps at 2^53 - 1.</summary>
    public const long RidesTheWholeRoute = long.MaxValue;

    public static long Of(Item item) => item.StopIndex ?? RidesTheWholeRoute;

    /// <summary>A stop as the reference renders it in a message.</summary>
    public static string Text(long stop) => stop == RidesTheWholeRoute ? "inf" : stop.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
