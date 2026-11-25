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
        (Axle Front, Axle Rear)? axles = null,
        IEnumerable<string>? accessDirections = null,
        string? deformableBoundary = null,
        MadeToMeasure? madeToMeasure = null)
        : this(id, innerDimensions, outerDimensions, tareWeight, maxPayload, costMinor, rateTable, quantity, obstacles,
            tags, maxItems, voidFillReserveRatio, tagLimits, metadata, maxStackDensity, axles, accessDirections, [],
            deformableBoundary, madeToMeasure)
    {
    }

    private Container(
        string id,
        Dimensions innerDimensions,
        Dimensions? outerDimensions,
        Weight tareWeight,
        Weight? maxPayload,
        long costMinor,
        RateTable? rateTable,
        int? quantity,
        IEnumerable<Obstacle>? obstacles,
        IEnumerable<string>? tags,
        long? maxItems,
        double voidFillReserveRatio,
        IEnumerable<KeyValuePair<string, long>>? tagLimits,
        IReadOnlyDictionary<string, object?>? metadata,
        Weight? maxStackDensity,
        (Axle Front, Axle Rear)? axles,
        IEnumerable<string>? accessDirections,
        ImmutableArray<Placement> preloaded,
        string? deformableBoundary,
        MadeToMeasure? madeToMeasure)
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
        DeformableBoundary = deformableBoundary;
        MadeToMeasure = madeToMeasure;
        var doors = (accessDirections ?? []).ToList();
        Validate(doors);
        // Canonical order rather than as given: two callers naming the same doors in a
        // different order must search identically.
        AccessDirections = [.. Directions.All.Where(doors.Contains)];
        Tags = (tags ?? []).ToFrozenSet(StringComparer.Ordinal);
        Preloaded = preloaded;
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

    /// <summary>
    /// Walls this container can be unloaded through, in canonical order. Empty leaves the
    /// horizontal half of route order unenforced -- it does not mean sealed, and it is not
    /// read as all six, which would change answers for every caller who never set it.
    /// </summary>
    public ImmutableArray<string> AccessDirections { get; }

    /// <summary>
    /// Items already in this one container instance before search starts. Set only by the
    /// orchestrator, on the instance a request's fixed placements name, so every solver's
    /// <c>ContainerState</c> starts from them without knowing they exist; a packed result
    /// carries the container with this emptied again.
    /// </summary>
    public ImmutableArray<Placement> Preloaded { get; }

    /// <summary>
    /// <c>"flat_bag"</c> makes this a flexible bag whose package conforms to its contents;
    /// null is a rigid container.
    /// </summary>
    public string? DeformableBoundary { get; }

    /// <summary>
    /// Cut to measure by an on-demand box machine whose largest box is <see cref="InnerDimensions"/>.
    /// A packed result carries the fabricated box instead.
    /// </summary>
    public MadeToMeasure? MadeToMeasure { get; }

    public bool IsFlatBag => DeformableBoundary is not null;

    /// <summary>This container holding <paramref name="placements"/> before search, or the plain container for none.</summary>
    public Container WithPreloaded(ImmutableArray<Placement> placements) =>
        Copy(InnerDimensions, OuterDimensions, CostMinor, TareWeight, placements);

    /// <summary>The declared container solved inside <paramref name="rung"/>: same id, everything else unchanged.</summary>
    public Container WithEnvelope(Triple rung) => Copy(rung.AsDimensions(), OuterDimensions, CostMinor, TareWeight, Preloaded);

    /// <summary>
    /// The box cut for an achieved extent: this container except its size, outer size, price and
    /// tare. O(1). Containment is a theorem, not a check: every placement lies inside
    /// <paramref name="achieved"/>, and the grid never rounds below it or past the maximum.
    /// </summary>
    public Container Fabricate(Triple achieved)
    {
        var spec = MadeToMeasure ?? throw new InvalidOperationException("only a made-to-measure container is fabricated");
        var maximum = Triple.Of(InnerDimensions);
        var sides = new Triple(spec.Grid(0, achieved.Length), spec.Grid(1, achieved.Width), spec.Grid(2, achieved.Height));
        if (sides.Length > maximum.Length || sides.Width > maximum.Width || sides.Height > maximum.Height)
        {
            throw new InvalidOperationException("fabricated box exceeds the machine");
        }
        var outer = new Triple(checked(sides.Length + spec.OuterAllowance.Length),
            checked(sides.Width + spec.OuterAllowance.Width), checked(sides.Height + spec.OuterAllowance.Height));
        return Copy(sides.AsDimensions(), outer.AsDimensions(), (long)spec.BoxCostMinor(CostMinor, sides),
            new Weight((long)spec.BoxTareTicks(TareWeight.Ticks, sides)), Preloaded);
    }

    private Container Copy(Dimensions inner, Dimensions? outer, long costMinor, Weight tareWeight,
        ImmutableArray<Placement> preloaded) =>
        new(Id, inner, outer, tareWeight, MaxPayload, costMinor, RateTable, Quantity, Obstacles, Tags,
            MaxItems, VoidFillReserveRatio, TagLimits, Metadata, MaxStackDensity, Axles, AccessDirections, preloaded,
            DeformableBoundary, MadeToMeasure);

    private void Validate(List<string> doors)
    {
        if (Id.Length == 0)
        {
            throw new ArgumentException("container id is required");
        }
        if (DeformableBoundary is not null)
        {
            RequireBagFields(doors);
        }
        MadeToMeasure?.Admits(Triple.Of(InnerDimensions));
        MadeToMeasure?.RequireSixtyFourBitBox(Triple.Of(InnerDimensions), CostMinor, TareWeight.Ticks);
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
        if (MaxItems is < 1)
        {
            throw new ArgumentException("container max_items must be at least 1");
        }
        if (doors.FirstOrDefault(door => !Directions.All.Contains(door)) is { } unknown)
        {
            throw new InvalidDirectionException(unknown);
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

    /// <summary>
    /// A bag has no walls, floor plan or chassis for a wall-anchored field to refer to, so those
    /// fields are refused rather than read against a frame that moves with its contents.
    /// </summary>
    private void RequireBagFields(List<string> doors)
    {
        if (!FlatBag.Kinds.Contains(DeformableBoundary))
        {
            throw new ArgumentException($"unknown deformable_boundary kind {Units.PythonText.Repr(DeformableBoundary!)}");
        }
        (string Name, bool Present)[] refused =
        [
            ("obstacles", Obstacles.Length > 0), ("axles", Axles is not null), ("access_directions", doors.Count > 0),
            ("max_stack_density", MaxStackDensity is not null), ("outer_dimensions", OuterDimensions is not null),
            ("void_fill_reserve_ratio", VoidFillReserveRatio > 0),
        ];
        if (refused.FirstOrDefault(field => field.Present) is { Name: { } name })
        {
            throw new ArgumentException($"{name} cannot be combined with deformable_boundary");
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
