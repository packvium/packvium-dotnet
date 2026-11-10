using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>
/// One opened container and what went into it. When the lattice fast path built it,
/// <see cref="Placements"/> stays empty and <see cref="LatticeSummary"/> carries the same
/// information; <see cref="LatticeItems"/> are the instances it consumed, in placement order.
/// </summary>
public sealed class PackedContainer(
    Container container,
    int sequence,
    ImmutableArray<Placement> placements,
    LatticeSummary? latticeSummary = null,
    ImmutableArray<ItemInstance> latticeItems = default)
{
    public Container Container { get; } = container;

    public int Sequence { get; } = sequence;

    public ImmutableArray<Placement> Placements { get; } = placements;

    public LatticeSummary? LatticeSummary { get; } = latticeSummary;

    public ImmutableArray<ItemInstance> LatticeItems { get; } = latticeItems.IsDefault ? [] : latticeItems;

    public string Id => $"{Container.Id}#{Sequence}";

    public long PlacementCount => LatticeSummary?.Count ?? Placements.Length;

    public Weight PayloadWeight => LatticeSummary is { } summary
        ? new Weight(summary.TotalWeightTicks)
        : new Weight(Placements.Aggregate(0L, (total, placement) => checked(total + placement.Instance.Weight.Ticks)));

    public Weight GrossWeight => new(checked(Container.TareWeight.Ticks + PayloadWeight.Ticks));

    public Int128 UsedVolume => LatticeSummary?.UsedVolumeTicks ?? Nesting.UsedVolume(Placements);

    /// <summary>Used over inner volume as the nearest double, which is how the reference engine divides.</summary>
    public double Utilization => DecimalRendering.TrueDivide(UsedVolume, Container.InnerDimensions.Volume);

    public long MaxZTicks => LatticeSummary?.MaxZTicks
                             ?? (Placements.IsEmpty ? 0 : Placements.Max(placement => placement.EnvelopeBox.Z2));

    public long CentreOfMassOffsetPpm => LatticeSummary is { } summary
        ? summary.CentreOfMassOffsetPpm(Container.InnerDimensions.Length.Ticks, Container.InnerDimensions.Width.Ticks)
        : CentreOfMass.OffsetPpm(Container.InnerDimensions, Placements);

    public ImmutableArray<Placement> ExpandPlacements() =>
        LatticeSummary is { } summary ? summary.Expand(LatticeItems) : Placements;

    /// <summary>This packed container as an item for the next nesting level.</summary>
    public Item AsItem(string? id = null, bool keepUpright = true, Weight? maxTopLoad = null) =>
        new(id ?? Id, Container.OuterDimensions ?? Container.InnerDimensions, GrossWeight, keepUpright: keepUpright,
            maxTopLoad: maxTopLoad,
            metadata: new Dictionary<string, object?> { ["source_packed_container"] = Id }.ToFrozenDictionary());
}
