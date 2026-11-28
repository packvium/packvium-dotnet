using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// The solid a placement actually occupies and the space it takes: its exact hull when the request
/// allows one, its box under load when it is compressible, otherwise its box; and whether two
/// instances of one type form a valid nested column.
/// </summary>
internal static class Solids
{
    /// <summary>
    /// The occupied solid as vertices in container coordinates: the exact hull when the item is a
    /// convex hull with no route stop and the reported envelope is its rotated box, otherwise the
    /// envelope's eight corners, so one collision routine answers every pair.
    /// </summary>
    public static ImmutableArray<TickVector> PlacedHull(Entry entry)
    {
        var (envelope, spec) = (entry.Envelope, entry.Spec);
        if (spec is null || !spec.IsConvexHull || spec.HullVertices is not { } hull || spec.StopIndex is not JsonNull)
        {
            return envelope.Vertices();
        }
        var axes = Geometry.Axes(Json.At(entry.Placement, "orientation"));
        var rotated = Geometry.Permute(spec.Dimensions, axes);
        if (envelope.Length != rotated[0] || envelope.Width != rotated[1] || envelope.Height != rotated[2])
        {
            return envelope.Vertices();
        }
        var origin = new TickVector(envelope.X, envelope.Y, envelope.Z);
        return [.. Geometry.RotateHull(hull, axes).Select(vertex => origin + vertex)];
    }

    /// <summary>
    /// Space a placement takes: its compressed box, its hull, or its own reported box. Volume is a
    /// property of the physical hull and does not inherit the box collision falls back to.
    /// </summary>
    public static BigInteger OccupiedVolume(JsonValue placement, Cuboid envelope, ItemSpec? spec, BigInteger load)
    {
        if (spec is not null && spec.MaxCompressionPressureKpa is not JsonNull)
        {
            return envelope.BaseArea * CompressedHeight(envelope, spec, load);
        }
        if (spec is not null && spec.IsConvexHull && spec.HullVertices is { } hull)
        {
            return Geometry.HullVolume(Geometry.RotateHull(hull, Geometry.Axes(Json.At(placement, "orientation"))));
        }
        var dimensions = Json.At(placement, "dimensions");
        return Extents.Tick(dimensions, "length") * Extents.Tick(dimensions, "width") * Extents.Tick(dimensions, "height");
    }

    /// <summary>
    /// Occupied height under load, rounded up, never below one tick. A crushed item keeps its
    /// height: the crush check already reports it.
    /// </summary>
    private static BigInteger CompressedHeight(Cuboid envelope, ItemSpec spec, BigInteger load)
    {
        var limit = Json.Int(spec.MaxCompressionPressureKpa, "max_compression_pressure_kpa");
        if (spec.CompressionRatioPpm is not { } ppm)
        {
            // The reference asserts the pair: a pressure limit without a ratio is not a request it reads.
            throw MalformedResultException.ShapeBreak($"{PyFormat.Str(spec.Id)}: max_compression_pressure_kpa without compression_ratio");
        }
        var pressure = Stacking.Pressure(load, envelope.BaseArea);
        if (pressure.CompareTo(limit) > 0 || limit.IsZero)
        {
            return envelope.Height;
        }
        var divisor = limit * PartsPerMillion.Scale * pressure.Denominator;
        var retained = divisor - ppm * pressure.Numerator;
        return BigInteger.Max(1, Exact.CeilDiv(envelope.Height * retained, divisor));
    }

    /// <summary>Two instances of one nesting type, on one footprint, the upper sunk exactly the nesting height into the lower.</summary>
    public static bool ValidNesting(Entry left, Entry right)
    {
        if (left.Spec is not { } leftSpec || right.Spec is not { } rightSpec || !Json.PyEquals(leftSpec.Id, rightSpec.Id)
            || !Json.PyEquals(Json.At(left.Placement, "item_type"), Json.At(right.Placement, "item_type"))
            || leftSpec.NestingHeight is not { } nesting || rightSpec.NestingHeight != nesting)
        {
            return false;
        }
        var (a, b) = (left.Envelope, right.Envelope);
        if (a.X != b.X || a.Y != b.Y || a.X2 != b.X2 || a.Y2 != b.Y2)
        {
            return false;
        }
        var (lower, upper) = a.Z <= b.Z ? (a, b) : (b, a);
        return lower.Z != upper.Z && lower.Z2 - upper.Z == nesting;
    }
}
