using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>A direct top/bottom contact: the supporting box's index and the shared area.</summary>
public readonly record struct ContactEdge(int Index, Int128 Area);

/// <summary>
/// A uniform XY spatial hash over one z-level's boxes. Correct only while the cell is at least
/// as large as every box indexed or queried, so a box spans at most a 2x2 block of cells.
/// </summary>
internal sealed class LevelIndex
{
    private readonly long cell;
    private readonly Dictionary<(long, long), List<(int Index, AxisAlignedBox Box)>> buckets = [];

    public LevelIndex(IEnumerable<(int Index, AxisAlignedBox Box)> candidates, long cell)
    {
        this.cell = cell;
        foreach (var entry in candidates)
        {
            foreach (var key in Cells(entry.Box))
            {
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    buckets[key] = bucket = [];
                }
                bucket.Add(entry);
            }
        }
    }

    /// <summary>Every candidate that could overlap <paramref name="box"/> in XY, each once; a superset.</summary>
    public IEnumerable<(int Index, AxisAlignedBox Box)> Near(AxisAlignedBox box)
    {
        var seen = new HashSet<int>();
        foreach (var key in Cells(box))
        {
            if (!buckets.TryGetValue(key, out var bucket))
            {
                continue;
            }
            foreach (var entry in bucket.Where(entry => seen.Add(entry.Index)))
            {
                yield return entry;
            }
        }
    }

    private HashSet<(long, long)> Cells(AxisAlignedBox box) =>
    [
        (IntMath.FloorDiv(box.Origin.X, cell), IntMath.FloorDiv(box.Origin.Y, cell)),
        (IntMath.FloorDiv(box.X2 - 1, cell), IntMath.FloorDiv(box.Origin.Y, cell)),
        (IntMath.FloorDiv(box.Origin.X, cell), IntMath.FloorDiv(box.Y2 - 1, cell)),
        (IntMath.FloorDiv(box.X2 - 1, cell), IntMath.FloorDiv(box.Y2 - 1, cell)),
    ];
}

/// <summary>
/// Direct top/bottom contact between boxes, derived from geometry alone and shared by every
/// load-propagation rule so they cannot disagree on what "touches" means. Supporter lists are
/// in ascending index order: load splitting hands the integer remainder to the last edge, so
/// edge order is contract. Build is O(n) expected with the spatial hash; <see cref="WithBox"/>
/// queries only the new box's two planes.
/// </summary>
public sealed class ContactGraph
{
    private readonly ImmutableArray<AxisAlignedBox> boxes;
    private readonly long cell;
    private readonly ImmutableArray<ImmutableArray<ContactEdge>> supporters;
    private readonly ImmutableArray<ImmutableArray<int>> children;
    private readonly Dictionary<long, List<(int, AxisAlignedBox)>> byTop;
    private readonly Dictionary<long, List<(int, AxisAlignedBox)>> byBottom;
    private readonly Dictionary<long, LevelIndex> topIndexes;
    private readonly Dictionary<long, LevelIndex> bottomIndexes;

    /// <param name="boxes">The boxes, indexed by position.</param>
    /// <param name="cellHint">An upper bound on the footprint of any box later appended with <see cref="WithBox"/>.</param>
    public ContactGraph(IEnumerable<AxisAlignedBox> boxes, long cellHint = 1)
    {
        this.boxes = [.. boxes];
        byTop = [];
        byBottom = [];
        for (var index = 0; index < this.boxes.Length; index++)
        {
            Bucket(byTop, this.boxes[index].Z2).Add((index, this.boxes[index]));
            Bucket(byBottom, this.boxes[index].Origin.Z).Add((index, this.boxes[index]));
        }
        cell = Math.Max(this.boxes.IsEmpty ? 1 : this.boxes.Max(Footprint), cellHint);
        topIndexes = [];
        bottomIndexes = [];
        var ownSupporters = new List<ContactEdge>[this.boxes.Length];
        var ownChildren = new List<int>[this.boxes.Length];
        for (var index = 0; index < this.boxes.Length; index++)
        {
            ownSupporters[index] = [];
            ownChildren[index] = [];
        }
        for (var index = 0; index < this.boxes.Length; index++)
        {
            var box = this.boxes[index];
            if (!byTop.ContainsKey(box.Origin.Z))
            {
                continue;
            }
            var matches = Near(byTop, topIndexes, box.Origin.Z, box)
                .Where(entry => entry.Index != index)
                .Select(entry => (entry.Index, Area: entry.Box.OverlapAreaXy(box)))
                .OrderBy(match => match.Index);
            foreach (var (otherIndex, area) in matches.Where(match => match.Area > 0))
            {
                ownSupporters[index].Add(new ContactEdge(otherIndex, area));
                ownChildren[otherIndex].Add(index);
            }
        }
        supporters = [.. ownSupporters.Select(list => list.ToImmutableArray())];
        children = [.. ownChildren.Select(list => list.ToImmutableArray())];
    }

    private ContactGraph(ImmutableArray<AxisAlignedBox> boxes, long cell,
        ImmutableArray<ImmutableArray<ContactEdge>> supporters, ImmutableArray<ImmutableArray<int>> children,
        Dictionary<long, List<(int, AxisAlignedBox)>> byTop, Dictionary<long, List<(int, AxisAlignedBox)>> byBottom,
        Dictionary<long, LevelIndex> topIndexes, Dictionary<long, LevelIndex> bottomIndexes)
    {
        this.boxes = boxes;
        this.cell = cell;
        this.supporters = supporters;
        this.children = children;
        this.byTop = byTop;
        this.byBottom = byBottom;
        this.topIndexes = topIndexes;
        this.bottomIndexes = bottomIndexes;
    }

    public int Count => boxes.Length;

    /// <summary>What <paramref name="index"/> directly rests on, each with the contact area.</summary>
    public ImmutableArray<ContactEdge> Supporters(int index) => supporters[index];

    /// <summary>What rests directly on <paramref name="index"/>, not transitively.</summary>
    public ImmutableArray<int> Children(int index) => children[index];

    /// <summary>
    /// This graph plus one box at the next index, identical to a rebuild over all the boxes:
    /// adding a box cannot change contact between two boxes already present.
    /// </summary>
    public ContactGraph WithBox(AxisAlignedBox box)
    {
        var index = boxes.Length;
        var footprint = Footprint(box);
        if (footprint > cell)
        {
            return new ContactGraph(boxes.Add(box), footprint);
        }
        var newSupporters = supporters.ToBuilder();
        var newChildren = children.ToBuilder();
        var topIndexesCopy = new Dictionary<long, LevelIndex>(topIndexes);
        var bottomIndexesCopy = new Dictionary<long, LevelIndex>(bottomIndexes);

        var ownSupporters = ImmutableArray.CreateBuilder<ContactEdge>();
        foreach (var (otherIndex, area) in Near(byTop, topIndexesCopy, box.Origin.Z, box)
                     .Select(entry => (entry.Index, Area: entry.Box.OverlapAreaXy(box))).OrderBy(match => match.Index))
        {
            if (area > 0)
            {
                ownSupporters.Add(new ContactEdge(otherIndex, area));
                newChildren[otherIndex] = newChildren[otherIndex].Add(index);
            }
        }
        var ownChildren = ImmutableArray.CreateBuilder<int>();
        foreach (var (otherIndex, area) in Near(byBottom, bottomIndexesCopy, box.Z2, box)
                     .Select(entry => (entry.Index, Area: entry.Box.OverlapAreaXy(box))).OrderBy(match => match.Index))
        {
            if (area > 0)
            {
                ownChildren.Add(otherIndex);
                newSupporters[otherIndex] = newSupporters[otherIndex].Add(new ContactEdge(index, area));
            }
        }
        newSupporters.Add(ownSupporters.ToImmutable());
        newChildren.Add(ownChildren.ToImmutable());

        var newByTop = new Dictionary<long, List<(int, AxisAlignedBox)>>(byTop)
        {
            [box.Z2] = [.. byTop.GetValueOrDefault(box.Z2) ?? [], (index, box)],
        };
        var newByBottom = new Dictionary<long, List<(int, AxisAlignedBox)>>(byBottom)
        {
            [box.Origin.Z] = [.. byBottom.GetValueOrDefault(box.Origin.Z) ?? [], (index, box)],
        };
        topIndexesCopy.Remove(box.Z2);
        bottomIndexesCopy.Remove(box.Origin.Z);
        return new ContactGraph(boxes.Add(box), cell, newSupporters.ToImmutable(), newChildren.ToImmutable(),
            newByTop, newByBottom, topIndexesCopy, bottomIndexesCopy);
    }

    private IEnumerable<(int Index, AxisAlignedBox Box)> Near(Dictionary<long, List<(int, AxisAlignedBox)>> buckets,
        Dictionary<long, LevelIndex> cache, long plane, AxisAlignedBox box)
    {
        if (!buckets.TryGetValue(plane, out var entries) || entries.Count == 0)
        {
            return [];
        }
        if (!cache.TryGetValue(plane, out var level))
        {
            cache[plane] = level = new LevelIndex(entries, cell);
        }
        return level.Near(box);
    }

    private static long Footprint(AxisAlignedBox box) => Math.Max(box.X2 - box.Origin.X, box.Y2 - box.Origin.Y);

    private static List<(int, AxisAlignedBox)> Bucket(Dictionary<long, List<(int, AxisAlignedBox)>> buckets, long key)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            buckets[key] = bucket = [];
        }
        return bucket;
    }
}
