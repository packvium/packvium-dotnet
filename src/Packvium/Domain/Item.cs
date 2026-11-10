using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>
/// One requested item type. Immutable; every invariant is checked at construction, in the
/// reference engine's order, so the first refusal a caller sees is the same in every engine.
/// </summary>
public sealed class Item
{
    /// <summary>
    /// The largest <c>stop_index</c> every engine can carry identically: JavaScript holds numbers
    /// as doubles, so two consecutive stops above this would become one there.
    /// </summary>
    public const long MaxExactStopIndex = (1L << 53) - 1;

    public static FrozenSet<string> GroundContactRules { get; } =
        new[] { "free", "covered", "single", "multiple" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, object?> NoMetadata =
        FrozenDictionary<string, object?>.Empty;

    public Item(
        string id,
        Dimensions dimensions,
        Weight weight = default,
        int quantity = 1,
        IEnumerable<Rotation>? allowedRotations = null,
        bool keepUpright = false,
        bool stackable = true,
        bool mustBeOnFloor = false,
        Weight? maxTopLoad = null,
        long? maxStackedItems = null,
        double minimumSupportRatio = 0.0,
        string? groundContactRule = null,
        string? group = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? incompatibleTags = null,
        long priority = 0,
        IEnumerable<string>? eligibleContainerTags = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Length? nestingHeight = null,
        long? stopIndex = null,
        ShapeType shapeType = ShapeType.RigidCuboid,
        IEnumerable<Vertex>? hullVertices = null,
        long? compressionRatioPpm = null,
        long? maxCompressionPressureKpa = null,
        long? value = null)
    {
        Id = id;
        Dimensions = dimensions;
        Weight = weight;
        Quantity = quantity;
        KeepUpright = keepUpright;
        Stackable = stackable;
        MustBeOnFloor = mustBeOnFloor;
        MaxTopLoad = maxTopLoad;
        MaxStackedItems = maxStackedItems;
        MinimumSupportRatio = minimumSupportRatio;
        GroundContactRule = groundContactRule;
        Group = group;
        Priority = priority;
        Metadata = metadata ?? NoMetadata;
        NestingHeight = nestingHeight;
        StopIndex = stopIndex;
        ShapeType = shapeType;
        CompressionRatioPpm = compressionRatioPpm;
        MaxCompressionPressureKpa = maxCompressionPressureKpa;
        Value = value;
        ValidateScalars();
        HullVertices = ValidateShape(hullVertices?.ToImmutableArray());
        AllowedRotations = AdmittedRotations(allowedRotations ?? Rotations.All);
        Tags = ToSet(tags);
        IncompatibleTags = ToSet(incompatibleTags);
        EligibleContainerTags = ToSet(eligibleContainerTags);
    }

    public string Id { get; }

    public Dimensions Dimensions { get; }

    public Weight Weight { get; }

    public int Quantity { get; }

    public ImmutableArray<Rotation> AllowedRotations { get; }

    public bool KeepUpright { get; }

    public bool Stackable { get; }

    public bool MustBeOnFloor { get; }

    public Weight? MaxTopLoad { get; }

    public long? MaxStackedItems { get; }

    public double MinimumSupportRatio { get; }

    public string? GroundContactRule { get; }

    public string? Group { get; }

    public FrozenSet<string> Tags { get; }

    public FrozenSet<string> IncompatibleTags { get; }

    public long Priority { get; }

    public FrozenSet<string> EligibleContainerTags { get; }

    /// <summary>Opaque caller data: accepted, never interpreted, never echoed into a result.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <summary>How far this item sinks into an identical item directly beneath it.</summary>
    public Length? NestingHeight { get; }

    /// <summary>Which stop along a route this item leaves at, lower first; null when not on a route.</summary>
    public long? StopIndex { get; }

    public ShapeType ShapeType { get; }

    public ImmutableArray<Vertex>? HullVertices { get; }

    public long? CompressionRatioPpm { get; }

    public long? MaxCompressionPressureKpa { get; }

    /// <summary>Exact, unit-less worth for the <c>maximum_value</c> objective.</summary>
    public long? Value { get; }

    /// <summary>Whether what rests on this item can change a verdict.</summary>
    public bool IsStackSensitive =>
        !Stackable || MaxTopLoad is not null || MaxStackedItems is not null || MaxCompressionPressureKpa is not null;

    public ImmutableArray<ItemInstance> Instances() =>
        [.. Enumerable.Range(1, Quantity).Select(sequence => new ItemInstance(this, sequence))];

    /// <summary>
    /// Whether this item's collisions may be decided by its hull rather than its box: only a
    /// hull, only when no clearance inflated the envelope, and only off a route, whose sweeps
    /// reason in boxes. Every fallback over-reserves space, the only safe direction.
    /// </summary>
    public bool HullCollisionIsExact(bool envelopeMatchesPhysical) =>
        ShapeType == ShapeType.ConvexHull && envelopeMatchesPhysical && StopIndex is null;

    private void ValidateScalars()
    {
        if (Id.Length == 0)
        {
            throw new ArgumentException("item id is required");
        }
        if (Quantity <= 0)
        {
            throw new ArgumentException("item quantity must be positive");
        }
        if (!(MinimumSupportRatio >= 0 && MinimumSupportRatio <= 1))
        {
            throw new ArgumentException("minimum_support_ratio must be between 0 and 1");
        }
        if (MaxStackedItems is < 1)
        {
            throw new ArgumentException("max_stacked_items must be at least 1");
        }
        if (StopIndex is < 0 or > MaxExactStopIndex)
        {
            throw new ArgumentException("stop_index must be a non-negative safe integer");
        }
        if (Value is < 0)
        {
            throw new ArgumentException("value must be non-negative");
        }
        if (NestingHeight is { } nesting && nesting.Ticks >= Dimensions.Height.Ticks)
        {
            throw new ArgumentException(
                "nesting_height must be at least zero and strictly less than the item's own height");
        }
        if (GroundContactRule is not null && !GroundContactRules.Contains(GroundContactRule))
        {
            throw new ArgumentException(
                "ground_contact_rule must be one of ['covered', 'free', 'multiple', 'single']");
        }
    }

    /// <summary>Admit the item's shape, or refuse it with the reason; returns the canonical hull.</summary>
    private ImmutableArray<Vertex>? ValidateShape(ImmutableArray<Vertex>? hullVertices)
    {
        foreach (var (name, present) in FieldsForeignToShape(hullVertices is not null))
        {
            if (present)
            {
                throw new ArgumentException($"{name} is not part of a {ShapeType.Value()} item");
            }
        }
        if (NestingHeight is not null && ShapeType != ShapeType.RigidCuboid)
        {
            throw new ArgumentException($"nesting_height with shape_type {ShapeType.Value()} is not supported yet");
        }
        if (ShapeType == ShapeType.ConvexHull)
        {
            return ValidateHull(hullVertices);
        }
        if (ShapeType == ShapeType.Compressible)
        {
            ValidateCompression();
        }
        return hullVertices;
    }

    private (string Name, bool Present)[] FieldsForeignToShape(bool hasHull)
    {
        (string, bool)[] compression =
        [
            ("compression_ratio_ppm", CompressionRatioPpm is not null),
            ("max_compression_pressure_kpa", MaxCompressionPressureKpa is not null),
        ];
        (string, bool)[] vertices = [("hull_vertices", hasHull)];
        return ShapeType switch
        {
            ShapeType.ConvexHull => compression,
            ShapeType.Compressible => vertices,
            _ => [.. vertices, .. compression],
        };
    }

    private ImmutableArray<Vertex> ValidateHull(ImmutableArray<Vertex>? hullVertices)
    {
        if (hullVertices is not { } authored)
        {
            throw new ArgumentException("a convex_hull item requires hull_vertices");
        }
        var points = Hull.Validate(authored);
        var (lower, upper) = Hull.BoundingExtent(points);
        var extent = (upper.X - lower.X, upper.Y - lower.Y, upper.Z - lower.Z);
        var declared = (Dimensions.Length.Ticks, Dimensions.Width.Ticks, Dimensions.Height.Ticks);
        if (extent.Item1 > declared.Item1 || extent.Item2 > declared.Item2 || extent.Item3 > declared.Item3)
        {
            throw new ArgumentException(
                $"hull_vertices span {extent} does not fit inside dimensions {declared}");
        }
        return points;
    }

    private void ValidateCompression()
    {
        if (CompressionRatioPpm is not { } ratio || MaxCompressionPressureKpa is not { } pressure)
        {
            throw new ArgumentException(
                "a compressible item requires both compression_ratio and max_compression_pressure_kpa");
        }
        if (ratio is < 0 or > Compression.Ppm)
        {
            throw new ArgumentException("compression_ratio must be between zero and one");
        }
        if (pressure < 0)
        {
            throw new ArgumentException("max_compression_pressure_kpa cannot be negative");
        }
    }

    private ImmutableArray<Rotation> AdmittedRotations(IEnumerable<Rotation> allowed)
    {
        var rotations = allowed.Where(rotation => !KeepUpright || Rotations.Upright.Contains(rotation)).ToImmutableArray();
        return rotations.IsEmpty ? throw new ArgumentException("at least one rotation must be allowed") : rotations;
    }

    private static FrozenSet<string> ToSet(IEnumerable<string>? values) =>
        (values ?? []).ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>One physical unit of an item type; <c>sequence</c> runs from 1 to the quantity.</summary>
public sealed class ItemInstance(Item item, int sequence)
{
    public Item Item { get; } = item;

    public int Sequence { get; } = sequence;

    public string Id => $"{Item.Id}#{Sequence}";

    public Dimensions Dimensions => Item.Dimensions;

    public Weight Weight => Item.Weight;
}
