using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>Plain integer extents of a solid: x1, y1, z1, x2, y2, z2.</summary>
public readonly record struct Bound(long X1, long Y1, long Z1, long X2, long Y2, long Z2);

/// <summary>
/// A uniform 3-D grid over box extents. A query returns a superset of the boxes it could
/// intersect; the caller still runs the exact test, so a fault here can only cost time, never
/// a false "no collision". Cell size is fixed from the container, about eight cells per axis.
/// </summary>
public sealed class SpatialIndex
{
    private readonly long cellX;
    private readonly long cellY;
    private readonly long cellZ;
    private readonly Dictionary<(long, long, long), List<int>> cells;

    public SpatialIndex(long lengthTicks, long widthTicks, long heightTicks, long cellsPerAxis = 8)
    {
        cellX = Math.Max(1, IntMath.CeilDiv(Math.Max(1, lengthTicks), cellsPerAxis));
        cellY = Math.Max(1, IntMath.CeilDiv(Math.Max(1, widthTicks), cellsPerAxis));
        cellZ = Math.Max(1, IntMath.CeilDiv(Math.Max(1, heightTicks), cellsPerAxis));
        cells = [];
    }

    private SpatialIndex(SpatialIndex source)
    {
        (cellX, cellY, cellZ) = (source.cellX, source.cellY, source.cellZ);
        cells = source.cells.ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
    }

    /// <summary>An independent fork: search branches diverge after copying their state.</summary>
    public SpatialIndex Copy() => new(this);

    public void Add(int index, Bound bound)
    {
        var (ix1, ix2, iy1, iy2, iz1, iz2) = CellRange(bound.X1, bound.Y1, bound.Z1, bound.X2, bound.Y2, bound.Z2);
        for (var ix = ix1; ix < ix2; ix++)
        {
            for (var iy = iy1; iy < iy2; iy++)
            {
                for (var iz = iz1; iz < iz2; iz++)
                {
                    if (!cells.TryGetValue((ix, iy, iz), out var bucket))
                    {
                        cells[(ix, iy, iz)] = bucket = [];
                    }
                    bucket.Add(index);
                }
            }
        }
    }

    /// <summary>Bound indices sharing at least one cell with the box, each once, in cell order.</summary>
    public IEnumerable<int> Query(long x1, long y1, long z1, long x2, long y2, long z2)
    {
        var (ix1, ix2, iy1, iy2, iz1, iz2) = CellRange(x1, y1, z1, x2, y2, z2);
        var seen = new HashSet<int>();
        for (var ix = ix1; ix < ix2; ix++)
        {
            for (var iy = iy1; iy < iy2; iy++)
            {
                for (var iz = iz1; iz < iz2; iz++)
                {
                    if (!cells.TryGetValue((ix, iy, iz), out var bucket))
                    {
                        continue;
                    }
                    foreach (var index in bucket)
                    {
                        if (seen.Add(index))
                        {
                            yield return index;
                        }
                    }
                }
            }
        }
    }

    private (long, long, long, long, long, long) CellRange(long x1, long y1, long z1, long x2, long y2, long z2) => (
        IntMath.FloorDiv(x1, cellX), IntMath.CeilDiv(Math.Max(x2, x1 + 1), cellX),
        IntMath.FloorDiv(y1, cellY), IntMath.CeilDiv(Math.Max(y2, y1 + 1), cellY),
        IntMath.FloorDiv(z1, cellZ), IntMath.CeilDiv(Math.Max(z2, z1 + 1), cellZ));
}
