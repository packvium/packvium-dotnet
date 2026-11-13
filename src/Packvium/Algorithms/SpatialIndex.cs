using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    private readonly Dictionary<(long, long, long), ImmutableArray<int>> cells;

    // Query answers by cell range, valid for the current contents only. A candidate scan asks
    // about far more boxes than there are distinct cell ranges -- the grid is coarse by design
    // -- and an answer is a pure function of the contents, so it is computed once per range
    // per generation of the index.
    private Dictionary<(long, long, long, long, long, long), ImmutableArray<int>> answers;

    public SpatialIndex(long lengthTicks, long widthTicks, long heightTicks, long cellsPerAxis = 8)
    {
        CellX = Math.Max(1, IntMath.CeilDiv(Math.Max(1, lengthTicks), cellsPerAxis));
        CellY = Math.Max(1, IntMath.CeilDiv(Math.Max(1, widthTicks), cellsPerAxis));
        CellZ = Math.Max(1, IntMath.CeilDiv(Math.Max(1, heightTicks), cellsPerAxis));
        cells = [];
        answers = [];
    }

    private SpatialIndex(SpatialIndex source)
    {
        (CellX, CellY, CellZ) = (source.CellX, source.CellY, source.CellZ);
        // Buckets are immutable and replaced on insert, so copying the dictionary is a complete
        // fork. The answers are shared: an insert on either side replaces that side's
        // dictionary rather than mutating it, so the other keeps a valid one.
        cells = new Dictionary<(long, long, long), ImmutableArray<int>>(source.cells);
        answers = source.answers;
    }

    public long CellX { get; }

    public long CellY { get; }

    public long CellZ { get; }

    /// <summary>An independent fork: search branches diverge after copying their state.</summary>
    public SpatialIndex Copy() => new(this);

    /// <summary>The bound indices registered in one cell, in insertion order.</summary>
    public ImmutableArray<int> Bucket(long ix, long iy, long iz) =>
        cells.TryGetValue((ix, iy, iz), out var bucket) ? bucket : [];

    public void Add(int index, Bound bound)
    {
        var (ix1, ix2, iy1, iy2, iz1, iz2) = CellRange(bound.X1, bound.Y1, bound.Z1, bound.X2, bound.Y2, bound.Z2);
        for (var ix = ix1; ix < ix2; ix++)
        {
            for (var iy = iy1; iy < iy2; iy++)
            {
                for (var iz = iz1; iz < iz2; iz++)
                {
                    cells[(ix, iy, iz)] = Bucket(ix, iy, iz).Add(index);
                }
            }
        }
        answers = [];
    }

    /// <summary>
    /// Bound indices sharing at least one cell with the box, each once. Cells are visited in
    /// (x, y, z) order and an index keeps its first position, so the sequence is a
    /// deterministic function of the contents. O(k) for k hits on a range's first query, O(1)
    /// after.
    /// </summary>
    public ImmutableArray<int> Query(long x1, long y1, long z1, long x2, long y2, long z2)
    {
        var range = CellRange(x1, y1, z1, x2, y2, z2);
        if (answers.TryGetValue(range, out var answer))
        {
            return answer;
        }
        answer = Collect(range);
        answers[range] = answer;
        return answer;
    }

    private ImmutableArray<int> Collect((long, long, long, long, long, long) range)
    {
        var (ix1, ix2, iy1, iy2, iz1, iz2) = range;
        var hits = new List<ImmutableArray<int>>();
        for (var ix = ix1; ix < ix2; ix++)
        {
            for (var iy = iy1; iy < iy2; iy++)
            {
                for (var iz = iz1; iz < iz2; iz++)
                {
                    if (cells.TryGetValue((ix, iy, iz), out var bucket))
                    {
                        hits.Add(bucket);
                    }
                }
            }
        }
        // One bucket is already duplicate-free, so it is handed back as it is.
        if (hits.Count <= 1)
        {
            return hits.Count == 0 ? [] : hits[0];
        }
        var seen = new HashSet<int>();
        var found = ImmutableArray.CreateBuilder<int>();
        foreach (var index in hits.SelectMany(bucket => bucket))
        {
            if (seen.Add(index))
            {
                found.Add(index);
            }
        }
        return found.ToImmutable();
    }

    private (long, long, long, long, long, long) CellRange(long x1, long y1, long z1, long x2, long y2, long z2) => (
        IntMath.FloorDiv(x1, CellX), IntMath.CeilDiv(Math.Max(x2, x1 + 1), CellX),
        IntMath.FloorDiv(y1, CellY), IntMath.CeilDiv(Math.Max(y2, y1 + 1), CellY),
        IntMath.FloorDiv(z1, CellZ), IntMath.CeilDiv(Math.Max(z2, z1 + 1), CellZ));
}
