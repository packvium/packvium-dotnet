using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>
/// The Session for one container during one solve: placed boxes plus the candidate points they
/// expose -- every solid's corners and their projections onto the surfaces below, behind and
/// to the left. A point is discarded only when it falls outside the container or inside a
/// box-shaped solid, never for being dominated.
/// </summary>
public sealed class ContainerState
{
    private readonly Dictionary<(long, long, long), Point> points;

    public ContainerState(Container container, int sequence)
    {
        Container = container;
        Sequence = sequence;
        Placements = [];
        Occupied = [.. container.Obstacles.SelectMany(obstacle => obstacle.Boxes)];
        Bounds = [.. Occupied.Select(Extent)];
        HullShapes = [.. Occupied.Select(_ => (HullShape?)null)];
        var inner = container.InnerDimensions;
        Index = new SpatialIndex(inner.Length.Ticks, inner.Width.Ticks, inner.Height.Ticks);
        for (var position = 0; position < Bounds.Count; position++)
        {
            Index.Add(position, Bounds[position]);
        }
        points = [];
        OrderedPoints = [];
        Absorb([Point.Origin]);
        foreach (var box in Occupied.ToList())
        {
            Absorb(ExposedPoints(box));
        }
    }

    private ContainerState(ContainerState source)
    {
        Container = source.Container;
        Sequence = source.Sequence;
        Placements = [.. source.Placements];
        points = new Dictionary<(long, long, long), Point>(source.points);
        OrderedPoints = [.. source.OrderedPoints];
        PayloadTicks = source.PayloadTicks;
        UsedVolumeTicks = source.UsedVolumeTicks;
        StackSensitive = source.StackSensitive;
        RouteSensitive = source.RouteSensitive;
        CompressionSensitive = source.CompressionSensitive;
        MaxZ = source.MaxZ;
        Occupied = [.. source.Occupied];
        Bounds = [.. source.Bounds];
        HullShapes = [.. source.HullShapes];
        Index = source.Index.Copy();
        LatticeSummary = source.LatticeSummary;
        LatticeItems = source.LatticeItems;
    }

    public Container Container { get; }

    public int Sequence { get; }

    public List<Placement> Placements { get; }

    public List<Point> OrderedPoints { get; private set; }

    public IEnumerable<Point> Points => points.Values;

    public long PayloadTicks { get; private set; }

    public Int128 UsedVolumeTicks { get; private set; }

    public bool StackSensitive { get; private set; }

    public bool RouteSensitive { get; private set; }

    public bool CompressionSensitive { get; private set; }

    public long MaxZ { get; private set; }

    public List<AxisAlignedBox> Occupied { get; }

    /// <summary>Integer extents of every solid; the candidate scan tests these millions of times.</summary>
    public List<Bound> Bounds { get; }

    /// <summary>Parallel to <see cref="Bounds"/>: the solid's rotated hull, or null for a box.</summary>
    public List<HullShape?> HullShapes { get; }

    public SpatialIndex Index { get; }

    public LatticeSummary? LatticeSummary { get; private set; }

    public ImmutableArray<ItemInstance> LatticeItems { get; private set; } = [];

    public long PlacementCount => LatticeSummary?.Count ?? Placements.Count;

    public ContainerState Copy() => new(this);

    public static Bound Extent(AxisAlignedBox box) =>
        new(box.Origin.X, box.Origin.Y, box.Origin.Z, box.X2, box.Y2, box.Z2);

    /// <summary>Records a whole regular-lattice run in O(1): no per-item placement is built.</summary>
    public void AddLattice(LatticeSummary summary, ImmutableArray<ItemInstance> items)
    {
        LatticeSummary = summary;
        LatticeItems = items;
        PayloadTicks = checked(PayloadTicks + summary.TotalWeightTicks);
        UsedVolumeTicks = checked(UsedVolumeTicks + summary.UsedVolumeTicks);
        StackSensitive = StackSensitive || items.Any(item => item.Item.IsStackSensitive);
        RouteSensitive = RouteSensitive || items.Any(item => item.Item.StopIndex is not null);
        MaxZ = Math.Max(MaxZ, summary.MaxZTicks);
    }

    public void Add(Placement placement)
    {
        Record(placement);
        var box = placement.EnvelopeBox;
        Occupied.Add(box);
        var bound = Extent(box);
        Index.Add(Bounds.Count, bound);
        Bounds.Add(bound);
        var shape = placement.HullShape;
        HullShapes.Add(shape);
        // A hull leaves most of its bounding box free, so points inside it stay alive for the
        // exact collision test to decide.
        if (shape is null)
        {
            var retired = points.Keys.Where(key => Inside(bound, key.Item1, key.Item2, key.Item3)).ToList();
            foreach (var key in retired)
            {
                points.Remove(key);
            }
            // OrderedPoints holds exactly the points of the dictionary, so the same test retires
            // the same set there without building a key per point.
            if (retired.Count > 0)
            {
                OrderedPoints = [.. OrderedPoints.Where(point => !Inside(bound, point.X, point.Y, point.Z))];
            }
        }
        Absorb(ExposedPoints(box));
    }

    /// <summary>Records a placement for a solver that never queries extreme points (a proven lattice).</summary>
    public void AddDirect(Placement placement) => Record(placement);

    private void Record(Placement placement)
    {
        var compression = CompressionSensitive || placement.Instance.Item.ShapeType == ShapeType.Compressible;
        UsedVolumeTicks = compression
            ? Loads.UsedVolumeWithCurrentLoads([.. Placements, placement])
            : checked(UsedVolumeTicks + Nesting.UsedVolumeDelta(Placements, placement));
        Placements.Add(placement);
        PayloadTicks = checked(PayloadTicks + placement.Instance.Weight.Ticks);
        var item = placement.Instance.Item;
        StackSensitive = StackSensitive || item.IsStackSensitive;
        RouteSensitive = RouteSensitive || item.StopIndex is not null;
        CompressionSensitive = compression;
        MaxZ = Math.Max(MaxZ, placement.EnvelopeBox.Z2);
    }

    private void Absorb(IEnumerable<Point> candidates)
    {
        var inner = Container.InnerDimensions;
        foreach (var point in candidates)
        {
            var (x, y, z) = (point.X, point.Y, point.Z);
            // A solid containing a point is registered in that point's cell, so only that
            // bucket is checked rather than every bound in the container.
            if (x >= inner.Length.Ticks || y >= inner.Width.Ticks || z >= inner.Height.Ticks
                || points.ContainsKey((x, y, z))
                || Index.Bucket(IntMath.FloorDiv(x, Index.CellX), IntMath.FloorDiv(y, Index.CellY), IntMath.FloorDiv(z, Index.CellZ))
                    .Any(position => Inside(Bounds[position], x, y, z)))
            {
                continue;
            }
            points[(x, y, z)] = point;
            OrderedPoints.Insert(InsertionIndex(point), point);
        }
    }

    /// <summary>Bisect-right on (z, y, x).</summary>
    private int InsertionIndex(Point point)
    {
        var (low, high) = (0, OrderedPoints.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            var current = OrderedPoints[middle];
            if ((current.Z, current.Y, current.X).CompareTo((point.Z, point.Y, point.X)) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private Point[] ExposedPoints(AxisAlignedBox box)
    {
        var origin = box.Origin;
        return
        [
            new(box.X2, origin.Y, origin.Z), new(origin.X, box.Y2, origin.Z), new(origin.X, origin.Y, box.Z2),
            new(box.X2, box.Y2, origin.Z), new(box.X2, origin.Y, box.Z2), new(origin.X, box.Y2, box.Z2),
            new(box.X2, origin.Y, SurfaceZ(box.X2, origin.Y, origin.Z)),
            new(box.X2, SurfaceY(box.X2, origin.Z, origin.Y), origin.Z),
            new(origin.X, box.Y2, SurfaceZ(origin.X, box.Y2, origin.Z)),
            new(SurfaceX(box.Y2, origin.Z, origin.X), box.Y2, origin.Z),
            new(origin.X, SurfaceY(origin.X, box.Z2, origin.Y), box.Z2),
            new(SurfaceX(origin.Y, box.Z2, origin.X), origin.Y, box.Z2),
        ];
    }

    private static bool Inside(Bound b, long x, long y, long z) =>
        b.X1 <= x && x < b.X2 && b.Y1 <= y && y < b.Y2 && b.Z1 <= z && z < b.Z2;

    // Each projection is the highest face at or below a ceiling among the solids whose
    // footprint covers the point on the other two axes. Such a solid ends inside the ceiling,
    // so it is registered in one of the cells of the ray below it: walking that ray visits
    // every solid that can contribute, and a maximum is indifferent to seeing one twice.
    // O(cells on the ray + their bucket sizes) rather than O(all solids).
    private long SurfaceZ(long x, long y, long ceiling)
    {
        var (ix, iy) = (IntMath.FloorDiv(x, Index.CellX), IntMath.FloorDiv(y, Index.CellY));
        var best = 0L;
        var cellsBelow = IntMath.CeilDiv(ceiling, Index.CellZ);
        for (var iz = 0L; iz < cellsBelow; iz++)
        {
            foreach (var position in Index.Bucket(ix, iy, iz))
            {
                var b = Bounds[position];
                if (best < b.Z2 && b.Z2 <= ceiling && b.X1 <= x && x < b.X2 && b.Y1 <= y && y < b.Y2)
                {
                    best = b.Z2;
                }
            }
        }
        return best;
    }

    private long SurfaceY(long x, long z, long ceiling)
    {
        var (ix, iz) = (IntMath.FloorDiv(x, Index.CellX), IntMath.FloorDiv(z, Index.CellZ));
        var best = 0L;
        var cellsBelow = IntMath.CeilDiv(ceiling, Index.CellY);
        for (var iy = 0L; iy < cellsBelow; iy++)
        {
            foreach (var position in Index.Bucket(ix, iy, iz))
            {
                var b = Bounds[position];
                if (best < b.Y2 && b.Y2 <= ceiling && b.X1 <= x && x < b.X2 && b.Z1 <= z && z < b.Z2)
                {
                    best = b.Y2;
                }
            }
        }
        return best;
    }

    private long SurfaceX(long y, long z, long ceiling)
    {
        var (iy, iz) = (IntMath.FloorDiv(y, Index.CellY), IntMath.FloorDiv(z, Index.CellZ));
        var best = 0L;
        var cellsBelow = IntMath.CeilDiv(ceiling, Index.CellX);
        for (var ix = 0L; ix < cellsBelow; ix++)
        {
            foreach (var position in Index.Bucket(ix, iy, iz))
            {
                var b = Bounds[position];
                if (best < b.X2 && b.X2 <= ceiling && b.Y1 <= y && y < b.Y2 && b.Z1 <= z && z < b.Z2)
                {
                    best = b.X2;
                }
            }
        }
        return best;
    }
}

/// <summary>Placements re-read with the load the finished support graph puts on each.</summary>
public static class Loads
{
    public static ImmutableArray<Placement> WithTopLoads(IReadOnlyList<Placement> placements)
    {
        var loads = LoadPropagation.TopLoads(LoadUnit.From(placements));
        return [.. placements.Select((placement, index) => placement with { TopLoad = new Units.Weight(loads[index]) })];
    }

    /// <summary>
    /// Physical volume after propagating this scene's loads: compression makes appending one
    /// placement non-local, so the composite path rebuilds the support graph. O(n log n + e).
    /// </summary>
    public static Int128 UsedVolumeWithCurrentLoads(IReadOnlyList<Placement> placements) =>
        Nesting.UsedVolume(WithTopLoads(placements));
}
