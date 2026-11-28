using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>An item type as the request declares it, read independently of the engine's model.</summary>
internal sealed record ItemSpec
{
    public required JsonValue Id { get; init; }

    public required ImmutableArray<BigInteger> Dimensions { get; init; }

    public required BigInteger Weight { get; init; }

    /// <summary>The request's value, compared and printed as the reference does.</summary>
    public required JsonValue Quantity { get; init; }

    public required ImmutableArray<JsonValue> AllowedRotations { get; init; }

    public required bool Stackable { get; init; }

    public required bool MustBeOnFloor { get; init; }

    public required BigInteger? MaxTopLoad { get; init; }

    public required Ratio MinimumSupportRatio { get; init; }

    public required TagSet Tags { get; init; }

    public required TagSet EligibleContainerTags { get; init; }

    /// <summary>The request's value, compared and printed as the reference does; null when absent.</summary>
    public required JsonValue MaxStackedItems { get; init; }

    public required JsonValue GroundContactRule { get; init; }

    public required BigInteger? NestingHeight { get; init; }

    public required TagSet IncompatibleTags { get; init; }

    /// <summary>The request's value, compared and printed as the reference does; null when absent.</summary>
    public required JsonValue StopIndex { get; init; }

    public required JsonValue Value { get; init; }

    public required JsonValue ShapeType { get; init; }

    public required ImmutableArray<TickVector>? HullVertices { get; init; }

    public required BigInteger? CompressionRatioPpm { get; init; }

    /// <summary>The request's value, read as an integer only where a placed item needs it; null when absent.</summary>
    public required JsonValue MaxCompressionPressureKpa { get; init; }

    public required JsonValue StackingGroup { get; init; }

    public required TagSet AcceptsStackingGroups { get; init; }

    public bool IsConvexHull => Json.IsText(ShapeType, "convex_hull");

    /// <summary><c>orientation in allowed_rotations</c>, by <c>==</c>.</summary>
    public bool Allows(JsonValue orientation) => AllowedRotations.Any(allowed => Json.PyEquals(allowed, orientation));

    public ImmutableArray<BigInteger> Rotated(string orientation) => Geometry.Permute(Dimensions, Geometry.Axes(orientation));

    public BigInteger NominalVolume => Dimensions[0] * Dimensions[1] * Dimensions[2];
}

/// <summary>A made-to-measure container's machine, in ticks, minor units and weight ticks.</summary>
/// <param name="Minimum">The smallest box the machine cuts, per side, in ticks.</param>
/// <param name="Increment">The machine's step per side, in ticks.</param>
/// <param name="PricePerSquareMetreMinor">The request's value, read as an integer only when a box is cut.</param>
/// <param name="TarePerSquareMetre">Board weight per square metre, in weight ticks.</param>
/// <param name="Allowance">What the walls add to each inner side, in ticks.</param>
internal sealed record MachineSpec(ImmutableArray<BigInteger> Minimum, ImmutableArray<BigInteger> Increment, JsonValue PricePerSquareMetreMinor,
    BigInteger TarePerSquareMetre, ImmutableArray<BigInteger> Allowance);

/// <summary>One axle: its position and optional load limit, in ticks.</summary>
internal sealed record Axle(BigInteger Position, BigInteger? MaxLoad);

/// <summary>A container type as the request declares it, read independently of the engine's model.</summary>
internal sealed record ContainerSpec
{
    public required JsonValue Id { get; init; }

    public required ImmutableArray<BigInteger> Inner { get; init; }

    public required ImmutableArray<BigInteger>? Outer { get; init; }

    /// <summary>The request's value, read as an integer only when a packed container is scored.</summary>
    public required JsonValue CostMinor { get; init; }

    public required BigInteger? MaxPayload { get; init; }

    /// <summary>The request's value, compared and printed as the reference does; null when absent.</summary>
    public required JsonValue MaxItems { get; init; }

    /// <summary>The request's value, compared and printed as the reference does; null when absent.</summary>
    public required JsonValue Quantity { get; init; }

    public required ImmutableArray<Cuboid> Obstacles { get; init; }

    public required TagSet Tags { get; init; }

    /// <summary>Sorted by tag in code-point order, the order the reference walks it in.</summary>
    public required ImmutableArray<KeyValuePair<string, BigInteger>> TagLimits { get; init; }

    public required Ratio VoidFillReserveRatio { get; init; }

    public required BigInteger TareWeight { get; init; }

    public required ImmutableArray<Axle>? Axles { get; init; }

    public required BigInteger? MaxStackDensity { get; init; }

    public required JsonValue RateTable { get; init; }

    /// <summary>The walls the container may be unloaded through, in exit-direction order; empty checks all six.</summary>
    public required ImmutableArray<string> AccessDirections { get; init; }

    public required bool FlatBag { get; init; }

    public required bool ReportsAchievedExtent { get; init; }

    public required MachineSpec? MadeToMeasure { get; init; }

    public BigInteger Volume => Inner[0] * Inner[1] * Inner[2];
}
