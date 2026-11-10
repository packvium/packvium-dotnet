using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Constraints;

public sealed record DirectSupport(int Index, AxisAlignedBox Box, Int128 Area);

/// <summary>Candidate support surfaces and positive-area direct supporters, in placement order.</summary>
public sealed record DirectSupportView(ImmutableArray<AxisAlignedBox> Surfaces, ImmutableArray<DirectSupport> Supporters)
{
    public Int128 SupportingArea => Supporters.Aggregate(Int128.Zero, (total, support) => total + support.Area);

    /// <summary>
    /// Direct supports for one candidate, O(m). Every box whose top is on the candidate's base
    /// plane is a surface and positive-area overlaps are supporters; an exact nesting
    /// predecessor replaces its column's face entries with one full-area supporter.
    /// </summary>
    public static DirectSupportView For(IReadOnlyList<Placement> placements, ItemInstance item, AxisAlignedBox box)
    {
        var faceSurfaces = new List<(int Index, AxisAlignedBox Box)>();
        var faceSupporters = new List<DirectSupport>();
        (int Index, AxisAlignedBox Box)? nearestLower = null;
        var nesting = item.Item.NestingHeight;
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            var other = placement.EnvelopeBox;
            if (other.Z2 == box.Origin.Z)
            {
                faceSurfaces.Add((index, other));
                var area = other.OverlapAreaXy(box);
                if (area > 0)
                {
                    faceSupporters.Add(new DirectSupport(index, other, area));
                }
            }
            if (nesting is not null && placement.Instance.Item.NestingHeight is not null
                && SameNestingColumn(placement, item, box) && other.Origin.Z < box.Origin.Z
                && (nearestLower is null || other.Origin.Z > nearestLower.Value.Box.Origin.Z))
            {
                nearestLower = (index, other);
            }
        }
        if (nearestLower is not { } predecessor || nesting is not { } depth
            || predecessor.Box.Z2 - box.Origin.Z != depth.Ticks)
        {
            return new DirectSupportView([.. faceSurfaces.Select(s => s.Box)], [.. faceSupporters]);
        }
        var surfaces = faceSurfaces.Where(entry => !SameNestingColumn(placements[entry.Index], item, box)).ToList();
        var supporters = faceSupporters.Where(s => !SameNestingColumn(placements[s.Index], item, box)).ToList();
        surfaces.Insert(surfaces.TakeWhile(entry => entry.Index < predecessor.Index).Count(), predecessor);
        supporters.Insert(supporters.TakeWhile(s => s.Index < predecessor.Index).Count(),
            new DirectSupport(predecessor.Index, predecessor.Box, predecessor.Box.OverlapAreaXy(box)));
        return new DirectSupportView([.. surfaces.Select(s => s.Box)], [.. supporters]);
    }

    private static bool SameNestingColumn(Placement placement, ItemInstance item, AxisAlignedBox box)
    {
        var other = placement.EnvelopeBox;
        return placement.Instance.Item.Id == item.Item.Id
               && placement.Instance.Item.NestingHeight is { } placedDepth
               && item.Item.NestingHeight is { } itemDepth
               && placedDepth.Ticks == itemDepth.Ticks
               && (other.Origin.X, other.Origin.Y, other.X2, other.Y2) == (box.Origin.X, box.Origin.Y, box.X2, box.Y2);
    }
}
