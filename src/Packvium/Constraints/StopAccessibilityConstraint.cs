using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Constraints;

/// <summary>
/// Refuses a placement that walls an earlier-stop item away from every door. Inert unless the
/// caller names exit directions and two distinct stops are in play. Blockers are strictly
/// later stops: the order within one stop is free. O(n^2 d) per placement-list rebuild for d
/// doors, O(n d) per candidate.
/// </summary>
public sealed class StopAccessibilityConstraint : IPlacementConstraint
{
    private readonly ImmutableArray<string> directions;
    private ImmutableArray<Placement> cachedPlacements;
    private Dimensions? cachedContainer;
    private ImmutableArray<ImmutableArray<string>> clear = [];
    private ImmutableArray<long> stops = [];

    public StopAccessibilityConstraint(IEnumerable<string>? directions = null)
    {
        var given = (directions ?? []).ToList();
        foreach (var direction in given.Where(direction => !Directions.All.Contains(direction)))
        {
            throw new InvalidDirectionException(direction);
        }
        // Canonical order rather than as given, so two callers naming the same doors search identically.
        this.directions = [.. Directions.All.Where(given.Contains)];
    }

    public ConstraintResult Evaluate(ConstraintContext context)
    {
        if (directions.IsEmpty || !context.RouteSensitive)
        {
            return ConstraintResult.Allow();
        }
        var candidateStop = Stops.Of(context.Item.Item);
        var inner = context.Container.InnerDimensions;
        BaseFor(context.Placements, inner);
        if (stops.Append(candidateStop).Distinct().Count() < 2)
        {
            return ConstraintResult.Allow();
        }
        var candidate = context.EnvelopeBox;
        for (var index = 0; index < context.Placements.Length; index++)
        {
            if (!(candidateStop > stops[index]) || clear[index].IsEmpty)
            {
                continue;
            }
            var placement = context.Placements[index];
            var stillOpen = clear[index].Any(direction =>
                !Directions.SweptVolume(placement.EnvelopeBox, inner, direction).Intersects(candidate));
            if (!stillOpen)
            {
                return ConstraintResult.Reject("stop_accessibility_violation",
                    $"{placement.Instance.Id} due at stop {Stops.Text(stops[index])} loses its last exit to {context.Item.Id}");
            }
        }
        if (candidateStop == Stops.RidesTheWholeRoute)
        {
            return ConstraintResult.Allow();
        }
        var boxes = context.Placements.Select(placement => placement.EnvelopeBox).ToList();
        var hasExit = directions.Any(direction => !Enumerable.Range(0, boxes.Count).Any(other =>
            stops[other] > candidateStop && Directions.SweptVolume(candidate, inner, direction).Intersects(boxes[other])));
        return hasExit
            ? ConstraintResult.Allow()
            : ConstraintResult.Reject("stop_accessibility_violation",
                $"{context.Item.Id} due at stop {Stops.Text(candidateStop)} would have no exit");
    }

    /// <summary>Per placed box, the doors still open to it against the boxes already placed.</summary>
    private void BaseFor(ImmutableArray<Placement> placements, Dimensions container)
    {
        if (!cachedPlacements.IsDefault && cachedPlacements == placements && cachedContainer == container)
        {
            return;
        }
        stops = [.. placements.Select(placement => Stops.Of(placement.Instance.Item))];
        var boxes = placements.Select(placement => placement.EnvelopeBox).ToList();
        var open = ImmutableArray.CreateBuilder<ImmutableArray<string>>(boxes.Count);
        for (var index = 0; index < boxes.Count; index++)
        {
            if (stops[index] == Stops.RidesTheWholeRoute)
            {
                open.Add(directions);
                continue;
            }
            var self = index;
            open.Add([.. directions.Where(direction => !Enumerable.Range(0, boxes.Count).Any(other =>
                other != self && stops[other] > stops[self]
                && Directions.SweptVolume(boxes[self], container, direction).Intersects(boxes[other])))]);
        }
        clear = open.MoveToImmutable();
        cachedPlacements = placements;
        cachedContainer = container;
    }
}
