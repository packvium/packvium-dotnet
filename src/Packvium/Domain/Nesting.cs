using System;
using System.Collections.Generic;
using System.Linq;

namespace Packvium.Domain;

/// <summary>Occupied volume of placed items, with nested-column overlap removed.</summary>
public static class Nesting
{
    /// <summary>
    /// Whether two placements are exactly a nested column: same item type, same footprint, one
    /// sunk into the other by precisely its declared nesting height.
    /// </summary>
    public static bool IsValidNesting(Placement a, Placement b)
    {
        if (a.Instance.Item.Id != b.Instance.Item.Id)
        {
            return false;
        }
        if (a.Instance.Item.NestingHeight is not { } nesting)
        {
            return false;
        }
        var boxA = a.EnvelopeBox;
        var boxB = b.EnvelopeBox;
        if ((boxA.Origin.X, boxA.Origin.Y, boxA.X2, boxA.Y2) != (boxB.Origin.X, boxB.Origin.Y, boxB.X2, boxB.Y2))
        {
            return false;
        }
        var (low, high) = boxA.Origin.Z <= boxB.Origin.Z ? (boxA, boxB) : (boxB, boxA);
        if (low.Origin.Z == high.Origin.Z)
        {
            return false;
        }
        return low.Z2 - high.Origin.Z == nesting.Ticks;
    }

    /// <summary>
    /// How much space one placement takes: its hull for a hull, its compressed height for a
    /// loaded compressible item, otherwise its box.
    /// </summary>
    public static Int128 OccupiedVolume(Placement placement)
    {
        var item = placement.Instance.Item;
        if (item.ShapeType == ShapeType.ConvexHull)
        {
            var vertices = item.HullVertices
                ?? throw new InvalidOperationException("a convex_hull item always carries its vertices");
            return Hull.ShapeFor(vertices, placement.Rotation).Volume;
        }
        if (item.MaxCompressionPressureKpa is not { } limit)
        {
            return placement.Dimensions.Volume;
        }
        var dimensions = placement.Dimensions;
        var footprint = dimensions.BaseArea;
        var pressure = Compression.AppliedPressure(placement.TopLoad, footprint);
        if (pressure.ExceedsKpa(limit))
        {
            // Already invalid and reported by the validator; the uncompressed figure keeps it a
            // reported issue rather than an exception out of a volume property.
            return dimensions.Volume;
        }
        return checked(footprint * Compression.EffectiveHeightTicks(dimensions.Height.Ticks,
            item.CompressionRatioPpm
            ?? throw new InvalidOperationException("a compressible item always carries its ratio"), limit, pressure));
    }

    /// <summary>Physical volume occupied, nesting overlap removed. O(n log n).</summary>
    public static Int128 UsedVolume(IReadOnlyList<Placement> placements)
    {
        Int128 total = 0;
        foreach (var placement in placements)
        {
            total = checked(total + OccupiedVolume(placement));
        }
        return total - NestingOverlapVolume(placements);
    }

    /// <summary>
    /// Exact volume added by appending <paramref name="placement"/>: O(1) without nesting, an
    /// O(n) scan only for an item that declares <c>nesting_height</c>.
    /// </summary>
    public static Int128 UsedVolumeDelta(IReadOnlyList<Placement> placements, Placement placement)
    {
        if (placement.Instance.Item.NestingHeight is not { } nesting)
        {
            return OccupiedVolume(placement);
        }
        Int128 overlap = 0;
        foreach (var existing in placements.Where(existing => IsValidNesting(existing, placement)))
        {
            overlap += nesting.Ticks * placement.EnvelopeDimensions.BaseArea;
        }
        return placement.Dimensions.Volume - overlap;
    }

    /// <summary>
    /// Double-counted volume between adjacent nested layers, grouped by item and footprint and
    /// checked only against the immediate z-neighbour: O(n log n) rather than all pairs.
    /// </summary>
    private static Int128 NestingOverlapVolume(IReadOnlyList<Placement> placements)
    {
        var groups = new Dictionary<(string, long, long, long, long), List<Placement>>();
        var order = new List<(string, long, long, long, long)>();
        foreach (var placement in placements.Where(p => p.Instance.Item.NestingHeight is not null))
        {
            var box = placement.EnvelopeBox;
            var key = (placement.Instance.Item.Id, box.Origin.X, box.Origin.Y, box.X2, box.Y2);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = [];
                order.Add(key);
            }
            group.Add(placement);
        }
        Int128 overlap = 0;
        foreach (var group in order.Select(key => groups[key]).Where(group => group.Count >= 2))
        {
            var ordered = group.OrderBy(p => p.EnvelopeBox.Origin.Z).ToList();
            for (var index = 0; index + 1 < ordered.Count; index++)
            {
                var lower = ordered[index];
                if (IsValidNesting(lower, ordered[index + 1]))
                {
                    overlap += lower.Instance.Item.NestingHeight!.Value.Ticks * lower.EnvelopeDimensions.BaseArea;
                }
            }
        }
        return overlap;
    }
}
