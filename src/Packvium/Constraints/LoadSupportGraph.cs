using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>
/// Face contact plus exact direct nesting support for load-bearing rules. Nesting candidates are
/// sorted by item, footprint and height, so only adjacent layers of one column are compared:
/// O(n log n) plus O(E). Edge and child lists keep ascending index order. Without nesting the
/// graph is the face graph, so its edges are read straight off it and extending it by one unit
/// copies no edge lists.
/// </summary>
public sealed class LoadSupportGraph
{
    private readonly ImmutableArray<ImmutableArray<ContactEdge>> supporters;
    private readonly ImmutableArray<ImmutableArray<int>> children;
    private readonly ContactGraph face;
    private readonly ImmutableArray<LoadUnit> units;
    private readonly bool nested;

    /// <summary>The settled order of the graph this one extends by one unit, when it has one.</summary>
    private readonly ImmutableArray<int> priorDescending;

    /// <summary>Computed on first use and shared by every reader of this graph; never changes once set.</summary>
    private ImmutableArray<int> descending;

    public LoadSupportGraph(IReadOnlyList<LoadUnit> units, long cellHint = 1)
    {
        this.units = [.. units];
        face = new ContactGraph(this.units.Select(unit => unit.Box), cellHint);
        var nesting = Enumerable.Range(0, this.units.Length)
            .Where(index => this.units[index].NestingItemId is not null && this.units[index].NestingHeightTicks is not null)
            .Select(index => (Column: Column(this.units[index]), this.units[index].Box.Origin.Z, this.units[index].Box.Z2, Index: index))
            .OrderBy(entry => entry, NestingOrder.Instance)
            .ToList();
        nested = nesting.Count > 0;
        if (!nested)
        {
            return;
        }
        var edges = Enumerable.Range(0, this.units.Length).Select(index => face.Supporters(index).ToList()).ToList();
        for (var position = 0; position + 1 < nesting.Count; position++)
        {
            var (lower, upper) = (nesting[position], nesting[position + 1]);
            if (lower.Column != upper.Column || lower.Z >= upper.Z)
            {
                continue;
            }
            var lowerUnit = this.units[lower.Index];
            var upperUnit = this.units[upper.Index];
            if (lowerUnit.Box.Z2 - upperUnit.Box.Origin.Z != lowerUnit.NestingHeightTicks)
            {
                continue;
            }
            var retained = edges[upper.Index].Where(edge => Column(this.units[edge.Index]) != upper.Column).ToList();
            retained.Insert(retained.TakeWhile(edge => edge.Index < lower.Index).Count(),
                new ContactEdge(lower.Index, lowerUnit.Box.OverlapAreaXy(upperUnit.Box)));
            edges[upper.Index] = retained;
        }
        var childLists = this.units.Select(_ => new List<int>()).ToList();
        for (var child = 0; child < edges.Count; child++)
        {
            foreach (var edge in edges[child])
            {
                childLists[edge.Index].Add(child);
            }
        }
        supporters = [.. edges.Select(list => list.ToImmutableArray())];
        children = [.. childLists.Select(list => list.ToImmutableArray())];
    }

    private LoadSupportGraph(ContactGraph face, ImmutableArray<LoadUnit> units, ImmutableArray<int> priorDescending)
    {
        this.face = face;
        this.units = units;
        this.priorDescending = priorDescending;
        nested = false;
    }

    public ImmutableArray<ContactEdge> Supporters(int index) => nested ? supporters[index] : face.Supporters(index);

    public ImmutableArray<int> Children(int index) => nested ? children[index] : face.Children(index);

    /// <summary>
    /// Every unit index, top first: by top face, then bottom face, both descending, then index --
    /// the order loads are propagated down a stack in. A graph extended by one unit derives it
    /// from its parent's by one binary insertion, O(n), instead of an O(n log n) sort per
    /// candidate.
    /// </summary>
    public ImmutableArray<int> DescendingIndices()
    {
        if (!descending.IsDefault)
        {
            return descending;
        }
        if (priorDescending.IsDefault)
        {
            descending = [.. Enumerable.Range(0, units.Length)
                .OrderBy(index => -units[index].Box.Z2).ThenBy(index => -units[index].Box.Origin.Z).ThenBy(index => index)];
            return descending;
        }
        var added = units.Length - 1;
        var (low, high) = (0, priorDescending.Length);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (TopFirst(added, priorDescending[middle]) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        descending = priorDescending.Insert(low, added);
        return descending;
    }

    private int TopFirst(int left, int right)
    {
        var (a, b) = (units[left].Box, units[right].Box);
        var order = b.Z2.CompareTo(a.Z2);
        if (order == 0)
        {
            order = b.Origin.Z.CompareTo(a.Origin.Z);
        }
        return order != 0 ? order : left.CompareTo(right);
    }

    /// <summary>
    /// This graph plus one unit at the next index. Nesting falls back to a rebuild: a nesting
    /// predecessor rewrites edges arbitrarily far from itself, so the delta is not local.
    /// </summary>
    public LoadSupportGraph WithUnit(LoadUnit unit, long cellHint = 1)
    {
        if (nested || unit.NestingItemId is not null)
        {
            return new LoadSupportGraph([.. units, unit], cellHint);
        }
        return new LoadSupportGraph(face.WithBox(unit.Box), units.Add(unit), DescendingIndices());
    }

    private static (string?, long?, long, long, long, long) Column(LoadUnit unit) =>
        (unit.NestingItemId, unit.NestingHeightTicks, unit.Box.Origin.X, unit.Box.Origin.Y, unit.Box.X2, unit.Box.Y2);

    /// <summary>Python's tuple order over (item, height, x1, y1, x2, y2, z1, z2, index), item by code point.</summary>
    private sealed class NestingOrder
        : IComparer<((string?, long?, long, long, long, long) Column, long Z, long Z2, int Index)>
    {
        public static NestingOrder Instance { get; } = new();

        public int Compare(((string?, long?, long, long, long, long) Column, long Z, long Z2, int Index) x,
            ((string?, long?, long, long, long, long) Column, long Z, long Z2, int Index) y)
        {
            var order = CodePointComparer.Instance.Compare(x.Column.Item1, y.Column.Item1);
            if (order != 0)
            {
                return order;
            }
            long[] left = [x.Column.Item2 ?? 0, x.Column.Item3, x.Column.Item4, x.Column.Item5, x.Column.Item6, x.Z, x.Z2, x.Index];
            long[] right = [y.Column.Item2 ?? 0, y.Column.Item3, y.Column.Item4, y.Column.Item5, y.Column.Item6, y.Z, y.Z2, y.Index];
            for (var index = 0; index < left.Length; index++)
            {
                order = left[index].CompareTo(right[index]);
                if (order != 0)
                {
                    return order;
                }
            }
            return 0;
        }
    }
}
