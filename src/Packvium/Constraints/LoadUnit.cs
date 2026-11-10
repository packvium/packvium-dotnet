using System.Collections.Generic;
using System.Collections.Immutable;
using Packvium.Domain;

namespace Packvium.Constraints;

/// <summary>The only facts load propagation needs about a box, real or hypothetical.</summary>
public sealed record LoadUnit(
    AxisAlignedBox Box,
    long WeightTicks,
    long? MaxTopLoadTicks,
    long? MaxStackedItems,
    string Label,
    string? NestingItemId = null,
    long? NestingHeightTicks = null,
    long? CompressionRatioPpm = null,
    long? MaxCompressionPressureKpa = null)
{
    /// <summary>A placed item as a load unit: its envelope, its weight and its bearing limits.</summary>
    public static LoadUnit Of(Placement placement)
    {
        var item = placement.Instance.Item;
        return new LoadUnit(placement.EnvelopeBox, placement.Instance.Weight.Ticks, item.MaxTopLoad?.Ticks,
            item.MaxStackedItems, placement.Instance.Id, item.NestingHeight is null ? null : item.Id,
            item.NestingHeight?.Ticks, item.CompressionRatioPpm, item.MaxCompressionPressureKpa);
    }

    /// <summary>A candidate not yet placed, carrying only what route and axle checks read.</summary>
    public static LoadUnit Hypothetical(AxisAlignedBox box, ItemInstance instance)
    {
        var item = instance.Item;
        return new LoadUnit(box, instance.Weight.Ticks, null, null, instance.Id,
            item.NestingHeight is null ? null : item.Id, item.NestingHeight?.Ticks);
    }

    /// <summary>A candidate not yet placed, with every limit the bearing checks read.</summary>
    public static LoadUnit Candidate(AxisAlignedBox box, ItemInstance instance)
    {
        var item = instance.Item;
        return new LoadUnit(box, instance.Weight.Ticks, item.MaxTopLoad?.Ticks, item.MaxStackedItems, instance.Id,
            item.NestingHeight is null ? null : item.Id, item.NestingHeight?.Ticks, item.CompressionRatioPpm,
            item.MaxCompressionPressureKpa);
    }

    /// <summary><c>load_units</c>: every placement in order, then the optional extra unit.</summary>
    public static ImmutableArray<LoadUnit> From(IReadOnlyList<Placement> placements, LoadUnit? extra = null)
    {
        var units = ImmutableArray.CreateBuilder<LoadUnit>(placements.Count + (extra is null ? 0 : 1));
        foreach (var placement in placements)
        {
            units.Add(Of(placement));
        }
        if (extra is not null)
        {
            units.Add(extra);
        }
        return units.MoveToImmutable();
    }
}
