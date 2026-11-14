using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;

namespace Packvium.Locks;

/// <summary>
/// Refuses a locked type's candidates while that type still owes a locked slot. It can only say
/// no: it runs beside every other constraint, never instead of them, so it cannot be the reason
/// a placement exists.
///
/// A lock reserves rather than forces. While a lock is outstanding its box is closed to other
/// candidates of the same type; the candidate that is the lock is admitted; a candidate that
/// misses the reserved volume is untouched. Refusing by type would turn eight cubes into one,
/// and demanding the slot be filled first would empty a container whose only candidate point is
/// still the origin.
///
/// O(L) per candidate for the L locks naming the item's type, plus one O(P) walk of the
/// container's placements when such a lock is outstanding -- the walk the support and overlap
/// rules already make, so the per-candidate bound is unchanged. A request with no locks never
/// constructs this constraint.
/// </summary>
internal sealed class LockConstraint(ImmutableArray<PlacementLock> locks) : IPlacementConstraint
{
    /// <summary>The rejection code a locked type's non-locked candidate carries.</summary>
    public const string LockViolated = "operator_lock";

    public ImmutableArray<PlacementLock> Locks { get; } = locks;

    public ConstraintResult Evaluate(ConstraintContext context)
    {
        var itemType = context.Item.Item.Id;
        var relevant = Locks.Where(candidate => string.Equals(candidate.ItemType, itemType, StringComparison.Ordinal)
                                                && Applicable(candidate, context)).ToList();
        if (relevant.Count == 0)
        {
            return ConstraintResult.Allow();
        }
        var outstanding = Outstanding(relevant, itemType, context.Placements);
        if (outstanding.Count == 0)
        {
            return ConstraintResult.Allow();
        }
        var candidateSlot = (context.Rotation.Value(), (context.Point.X, context.Point.Y, context.Point.Z));
        var candidateBox = new AxisAlignedBox(context.Point, context.Dimensions);
        foreach (var outstandingLock in outstanding)
        {
            if (outstandingLock.Slot == candidateSlot)
            {
                continue;
            }
            if (candidateBox.Intersects(ReservedBox(outstandingLock, context)))
            {
                return ConstraintResult.Reject(LockViolated,
                    $"{itemType} is locked to {outstandingLock.Orientation}@{outstandingLock.PositionText}");
            }
        }
        return ConstraintResult.Allow();
    }

    /// <summary>The relevant locks whose slot no placement of the type fills yet.</summary>
    private static List<PlacementLock> Outstanding(List<PlacementLock> relevant, string itemType,
        ImmutableArray<Placement> placements)
    {
        var filled = new HashSet<(string, (long, long, long))>();
        foreach (var placement in placements)
        {
            if (string.Equals(placement.Instance.Item.Id, itemType, StringComparison.Ordinal))
            {
                filled.Add((placement.Rotation.Value(), (placement.Position.X, placement.Position.Y, placement.Position.Z)));
            }
        }
        return [.. relevant.Where(candidate => !filled.Contains(candidate.Slot))];
    }

    /// <summary>
    /// Whether this container could hold the locked box at all. The constraint cannot know which
    /// container index it is packing, so without this a lock read from one container would refuse
    /// every candidate in a container too small for it. The physical box, not the clearance
    /// envelope: a lock this admits is still judged by the ordinary geometry rules.
    /// </summary>
    private static bool Applicable(PlacementLock placementLock, ConstraintContext context)
    {
        var box = context.Item.Dimensions.Rotated(placementLock.Rotation);
        var inner = context.Container.InnerDimensions;
        var (x, y, z) = placementLock.PositionTicks;
        // Int128, so a lock far outside every container is inert rather than an overflow.
        return (Int128)x + box.Length.Ticks <= inner.Length.Ticks
               && (Int128)y + box.Width.Ticks <= inner.Width.Ticks
               && (Int128)z + box.Height.Ticks <= inner.Height.Ticks;
    }

    /// <summary>
    /// The volume the locked item will occupy, sized from its own type. The physical box: the
    /// clearance rule already guards the margin around whatever lands here, and reserving it twice
    /// would refuse neighbours the engine is willing to place.
    /// </summary>
    private static AxisAlignedBox ReservedBox(PlacementLock placementLock, ConstraintContext context)
    {
        var (x, y, z) = placementLock.PositionTicks;
        return new AxisAlignedBox(new Point(x, y, z), context.Item.Dimensions.Rotated(placementLock.Rotation));
    }
}
