using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>One direct support: the lower entry's position and the contact area it gives.</summary>
internal readonly record struct Support(int Lower, BigInteger Area);

/// <summary>
/// What rests directly on what, by entry position. Ordinary face contacts keep placement order;
/// nested instances of one column are linked only to their adjacent predecessor, which carries the
/// upper item's full base and replaces any shadowed face contact from the same column.
/// </summary>
internal sealed class SupportGraph
{
    private SupportGraph(ImmutableArray<ImmutableArray<Support>> supporters, ImmutableArray<ImmutableHashSet<int>> directAbove)
    {
        Supporters = supporters;
        DirectAbove = directAbove;
    }

    /// <summary>Each entry's direct supporters, in placement order.</summary>
    public ImmutableArray<ImmutableArray<Support>> Supporters { get; }

    /// <summary>The entries resting directly on each entry.</summary>
    public ImmutableArray<ImmutableHashSet<int>> DirectAbove { get; }

    /// <summary>
    /// O(n log n + q + e) time and O(n + e) space, for n entries, q same-plane candidates and e
    /// support edges.
    /// </summary>
    public static SupportGraph Build(ImmutableArray<Entry> entries)
    {
        var byTop = new Dictionary<BigInteger, List<int>>();
        for (var index = 0; index < entries.Length; index++)
        {
            var top = entries[index].Envelope.Z2;
            if (!byTop.TryGetValue(top, out var level))
            {
                byTop[top] = level = [];
            }
            level.Add(index);
        }
        var supporters = new List<Support>[entries.Length];
        for (var upper = 0; upper < entries.Length; upper++)
        {
            supporters[upper] = [];
            foreach (var lower in byTop.GetValueOrDefault(entries[upper].Envelope.Z, []))
            {
                var area = lower == upper ? BigInteger.Zero : entries[lower].Envelope.OverlapAreaXy(entries[upper].Envelope);
                if (area.Sign > 0)
                {
                    supporters[upper].Add(new Support(lower, area));
                }
            }
        }
        LinkNestedColumns(entries, supporters);
        var directAbove = Enumerable.Range(0, entries.Length).Select(_ => ImmutableHashSet.CreateBuilder<int>()).ToList();
        for (var upper = 0; upper < entries.Length; upper++)
        {
            foreach (var support in supporters[upper])
            {
                directAbove[support.Lower].Add(upper);
            }
        }
        return new SupportGraph([.. supporters.Select(edges => edges.ToImmutableArray())], [.. directAbove.Select(set => set.ToImmutable())]);
    }

    /// <summary>Replaces each nested instance's same-column contacts with one edge to its adjacent predecessor.</summary>
    private static void LinkNestedColumns(ImmutableArray<Entry> entries, List<Support>[] supporters)
    {
        var groupKeys = new Dictionary<int, string>();
        var groups = new List<List<int>>();
        var groupIndex = new Dictionary<string, int>(System.StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Spec?.NestingHeight is not { } nesting)
            {
                continue;
            }
            var envelope = entries[index].Envelope;
            var key = $"{Json.HashKey(Json.At(entries[index].Placement, "item_type"))}\0{envelope.X},{envelope.Y},{envelope.X2},{envelope.Y2},{nesting}";
            groupKeys[index] = key;
            if (!groupIndex.TryGetValue(key, out var position))
            {
                groupIndex[key] = position = groups.Count;
                groups.Add([]);
            }
            groups[position].Add(index);
        }
        var predecessors = new List<(int Upper, int Lower)>();
        foreach (var group in groups)
        {
            var column = group.OrderBy(index => entries[index].Envelope.Z).ThenBy(index => index).ToList();
            for (var position = 0; position + 1 < column.Count; position++)
            {
                var (lower, upper) = (column[position], column[position + 1]);
                if (entries[lower].Envelope.Z2 - entries[upper].Envelope.Z == entries[lower].Spec!.NestingHeight)
                {
                    predecessors.Add((upper, lower));
                }
            }
        }
        foreach (var (upper, lower) in predecessors)
        {
            var key = groupKeys[upper];
            var edges = supporters[upper].Where(edge => groupKeys.GetValueOrDefault(edge.Lower) != key).ToList();
            var at = edges.FindIndex(edge => edge.Lower > lower);
            edges.Insert(at < 0 ? edges.Count : at, new Support(lower, entries[upper].Envelope.BaseArea));
            supporters[upper] = edges;
        }
    }
}
