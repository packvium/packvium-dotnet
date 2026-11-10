using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>One offered container type.</summary>
public sealed class Container
{
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata =
        FrozenDictionary<string, object?>.Empty;

    public Container(
        string id,
        Dimensions innerDimensions,
        Dimensions? outerDimensions = null,
        Weight tareWeight = default,
        Weight? maxPayload = null,
        long costMinor = 0,
        RateTable? rateTable = null,
        int? quantity = null,
        IEnumerable<Obstacle>? obstacles = null,
        IEnumerable<string>? tags = null,
        long? maxItems = null,
        double voidFillReserveRatio = 0.0,
        IEnumerable<KeyValuePair<string, long>>? tagLimits = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Weight? maxStackDensity = null,
        (Axle Front, Axle Rear)? axles = null)
    {
        Id = id;
        InnerDimensions = innerDimensions;
        OuterDimensions = outerDimensions;
        TareWeight = tareWeight;
        MaxPayload = maxPayload;
        CostMinor = costMinor;
        RateTable = rateTable;
        Quantity = quantity;
        Obstacles = obstacles?.ToImmutableArray() ?? [];
        MaxItems = maxItems;
        VoidFillReserveRatio = voidFillReserveRatio;
        TagLimits = tagLimits?.ToImmutableArray() ?? [];
        Metadata = metadata ?? NoMetadata;
        MaxStackDensity = maxStackDensity;
        Axles = axles;
        Validate();
        Tags = (tags ?? []).ToFrozenSet(StringComparer.Ordinal);
    }

    public string Id { get; }

    public Dimensions InnerDimensions { get; }

    public Dimensions? OuterDimensions { get; }

    public Weight TareWeight { get; }

    public Weight? MaxPayload { get; }

    public long CostMinor { get; }

    public RateTable? RateTable { get; }

    /// <summary>How many of this container may be opened; null means unlimited.</summary>
    public int? Quantity { get; }

    public ImmutableArray<Obstacle> Obstacles { get; }

    public FrozenSet<string> Tags { get; }

    public long? MaxItems { get; }

    public double VoidFillReserveRatio { get; }

    /// <summary>Per-tag item ceilings, in the order the request declared them.</summary>
    public ImmutableArray<KeyValuePair<string, long>> TagLimits { get; }

    /// <summary>Opaque caller data: accepted, never interpreted, never echoed into a result.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <summary>Weight per square metre of base area -- floor loading, not a total.</summary>
    public Weight? MaxStackDensity { get; }

    /// <summary>Front and rear axle, the front nearer the container's own x origin.</summary>
    public (Axle Front, Axle Rear)? Axles { get; }

    private void Validate()
    {
        if (Id.Length == 0)
        {
            throw new ArgumentException("container id is required");
        }
        if (Quantity is <= 0)
        {
            throw new ArgumentException("container quantity must be positive");
        }
        if (CostMinor < 0)
        {
            throw new ArgumentException("container cost cannot be negative");
        }
        if (!(VoidFillReserveRatio >= 0 && VoidFillReserveRatio <= 1))
        {
            throw new ArgumentException("void_fill_reserve_ratio must be between 0 and 1");
        }
        ValidateAxles();
        if (TagLimits.Any(limit => limit.Value < 1))
        {
            throw new ArgumentException("tag_limits must be at least 1");
        }
        if (OuterDimensions is not null && !InnerDimensions.FitsInside(OuterDimensions))
        {
            throw new ArgumentException("outer dimensions cannot be smaller than inner dimensions");
        }
        var boundary = new AxisAlignedBox(Point.Origin, InnerDimensions);
        if (Obstacles.FirstOrDefault(obstacle => !obstacle.Boxes.All(boundary.Contains)) is { } outside)
        {
            throw new ArgumentException($"obstacle {outside.Id} lies outside container");
        }
    }

    private void ValidateAxles()
    {
        if (Axles is not { } axles)
        {
            return;
        }
        var (front, rear) = axles;
        if (front.Position.Ticks >= rear.Position.Ticks)
        {
            throw new ArgumentException("the front axle must be strictly nearer the origin than the rear axle");
        }
        if (rear.Position.Ticks > InnerDimensions.Length.Ticks)
        {
            throw new ArgumentException("axle positions must lie within the container's length");
        }
    }
}
