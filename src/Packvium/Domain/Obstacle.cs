using System.Collections.Immutable;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>
/// An excluded zone. Extra boxes express a non-rectangular shape (a wheel arch, a tapered roof)
/// as a union of exact boxes, so the geometry stays integral.
/// </summary>
public sealed class Obstacle(string id, AxisAlignedBox box, ImmutableArray<AxisAlignedBox> additionalBoxes = default)
{
    public string Id { get; } = id;

    public AxisAlignedBox Box { get; } = box;

    public ImmutableArray<AxisAlignedBox> AdditionalBoxes { get; } =
        additionalBoxes.IsDefault ? [] : additionalBoxes;

    public ImmutableArray<AxisAlignedBox> Boxes => [Box, .. AdditionalBoxes];
}

/// <summary>One axle's position along the container's length axis and its own limit. Two axles only.</summary>
public sealed record Axle(Length Position, Weight? MaxLoad = null);
