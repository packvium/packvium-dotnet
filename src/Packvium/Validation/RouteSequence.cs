using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;

namespace Packvium.Validation;

/// <summary>No order empties <see cref="Stop"/> before the route moves on: these placements are still blocked there.</summary>
public sealed class RouteSequenceException(long stop, ImmutableArray<int> stuck)
    : Exception($"stop {stop}: placements [{string.Join(", ", stuck.Order())}] cannot be unloaded there")
{
    public long Stop { get; } = stop;

    public ImmutableArray<int> Stuck { get; } = stuck;
}

/// <summary>No safe order exists: every remaining placement is blocked in every allowed direction.</summary>
public sealed class SequenceException(ImmutableArray<int> stuck)
    : Exception($"no safe order exists: placements [{string.Join(", ", stuck.Order())}] are mutually blocking")
{
    public const string Code = "sequence_stuck";

    public ImmutableArray<int> Stuck { get; } = stuck;
}

/// <summary>
/// A given order has a step that is not feasible against only the placements present at that
/// point. Unlike <see cref="SequenceException"/>, this says the supplied order is wrong, whether
/// or not another order exists.
/// </summary>
public sealed class SequenceReplayException(int index, int step, string reason)
    : Exception($"step {step}: placement {index} is not safe there ({reason})")
{
    public const string Code = "sequence_replay";

    public int Index { get; } = index;

    public int Step { get; } = step;

    public string Reason { get; } = reason;
}

/// <summary>
/// Loading and unloading replay over a finished scene (<c>packing_sequence.py</c>), using the same
/// sweep geometry the constraints use. Unloading starts from the full scene, and a box depends on
/// whatever rests on it; loading starts from an empty container, and a box depends on whatever it
/// rests on. A sweep toward a wall measures the same region whether a box leaves or arrives
/// through it, so the two share one accessibility test.
/// </summary>
public static class RouteSequence
{
    /// <summary>
    /// An order the placements can be removed in, starting from the full scene, without moving a
    /// still-present placement first. Each step takes the lowest removable index. Throws
    /// <see cref="SequenceException"/> when no such order exists. O(n^3 * d) for n boxes and d
    /// directions, plus the replay.
    /// </summary>
    public static List<int> SafeRemovalOrder(IReadOnlyList<AxisAlignedBox> boxes, Dimensions container,
        IReadOnlyList<string>? directions = null)
    {
        directions = Validated(directions);
        var dependsOn = Children(boxes);
        var present = Enumerable.Range(0, boxes.Count).ToHashSet();
        var order = new List<int>();
        while (present.Count > 0)
        {
            var removable = present.Where(index => !dependsOn[index].Overlaps(present)
                                                   && ClearDirection(index, boxes, present, container, directions))
                .ToList();
            if (removable.Count == 0)
            {
                throw new SequenceException([.. present]);
            }
            var chosen = removable.Min();
            order.Add(chosen);
            present.Remove(chosen);
        }
        ReplayRemovalOrder(boxes, container, order, directions);
        return order;
    }

    /// <summary>
    /// An order the placements can be loaded in, starting from an empty container, so that each
    /// box finds its supporters present and a clear insertion sweep. It is the reverse of
    /// <see cref="SafeRemovalOrder"/>, not a forward search: removal only shrinks the present
    /// set, so its greedy choice cannot trap itself, where a forward greedy loader can. The
    /// reversal is then replayed forward by <see cref="ReplayLoadingOrder"/>, which does not
    /// trust it.
    /// </summary>
    public static List<int> SafeLoadingOrder(IReadOnlyList<AxisAlignedBox> boxes, Dimensions container,
        IReadOnlyList<string>? directions = null)
    {
        directions = Validated(directions);
        var order = SafeRemovalOrder(boxes, container, directions);
        order.Reverse();
        ReplayLoadingOrder(boxes, container, order, directions);
        return order;
    }

    /// <summary>
    /// Replays an unloading order from the full scene and throws
    /// <see cref="SequenceReplayException"/> at the first step that is not feasible.
    /// </summary>
    public static void ReplayRemovalOrder(IReadOnlyList<AxisAlignedBox> boxes, Dimensions container,
        IReadOnlyList<int> order, IReadOnlyList<string>? directions = null)
    {
        directions = Validated(directions);
        RequirePermutation(boxes, order);
        var dependsOn = Children(boxes);
        var present = Enumerable.Range(0, boxes.Count).ToHashSet();
        for (var step = 0; step < order.Count; step++)
        {
            var index = order[step];
            ValidateBoxAtStep(index, step, boxes, present, container);
            if (dependsOn[index].Overlaps(present))
            {
                throw new SequenceReplayException(index, step, "something still resting on it has not been removed yet");
            }
            if (!ClearDirection(index, boxes, present, container, directions))
            {
                throw new SequenceReplayException(index, step, "no allowed direction is clear of the remaining placements");
            }
            present.Remove(index);
        }
    }

    /// <summary>
    /// Replays a loading order from an empty container and throws
    /// <see cref="SequenceReplayException"/> at the first step that is not feasible.
    /// </summary>
    public static void ReplayLoadingOrder(IReadOnlyList<AxisAlignedBox> boxes, Dimensions container,
        IReadOnlyList<int> order, IReadOnlyList<string>? directions = null)
    {
        directions = Validated(directions);
        RequirePermutation(boxes, order);
        var contacts = new ContactGraph(boxes);
        var present = new HashSet<int>();
        for (var step = 0; step < order.Count; step++)
        {
            var index = order[step];
            ValidateBoxAtStep(index, step, boxes, present, container);
            if (!contacts.Supporters(index).All(edge => present.Contains(edge.Index)))
            {
                throw new SequenceReplayException(index, step, "a supporter has not been loaded yet");
            }
            if (!ClearDirection(index, boxes, present, container, directions))
            {
                throw new SequenceReplayException(index, step, "no allowed direction is clear of what has already been loaded");
            }
            present.Add(index);
        }
    }

    /// <summary>
    /// An unloading order that fully empties each stop, ascending, before the next. An item with
    /// no stop is never removed but still blocks. Each step picks the lowest removable index.
    /// O(s * n^2 * d) for s steps, n boxes and d directions.
    /// </summary>
    public static List<int> SafeRouteRemovalOrder(IReadOnlyList<AxisAlignedBox> boxes, IReadOnlyList<long?> stops,
        Dimensions container, IReadOnlyList<string>? directions = null)
    {
        directions = Validated(directions);
        var dependsOn = Children(boxes);
        var remaining = Enumerable.Range(0, boxes.Count).ToHashSet();
        var order = new List<int>();
        foreach (var stop in stops.Where(stop => stop is not null).Select(stop => stop!.Value).Distinct().Order())
        {
            var due = remaining.Where(index => stops[index] == stop).ToHashSet();
            while (due.Count > 0)
            {
                var removable = due.Where(index => !dependsOn[index].Overlaps(remaining)
                                                   && ClearDirection(index, boxes, remaining, container, directions))
                    .ToList();
                if (removable.Count == 0)
                {
                    throw new RouteSequenceException(stop, [.. due]);
                }
                var chosen = removable.Min();
                order.Add(chosen);
                remaining.Remove(chosen);
                due.Remove(chosen);
            }
        }
        return order;
    }

    private static IReadOnlyList<string> Validated(IReadOnlyList<string>? directions)
    {
        directions ??= Directions.All;
        foreach (var direction in directions.Where(direction => !Directions.All.Contains(direction)))
        {
            throw new InvalidDirectionException(direction);
        }
        return directions;
    }

    /// <summary>Each box's children: whatever rests on it must leave before it does.</summary>
    private static List<HashSet<int>> Children(IReadOnlyList<AxisAlignedBox> boxes)
    {
        var contacts = new ContactGraph(boxes);
        return [.. Enumerable.Range(0, boxes.Count).Select(index => contacts.Children(index).ToHashSet())];
    }

    private static void RequirePermutation(IReadOnlyList<AxisAlignedBox> boxes, IReadOnlyList<int> order)
    {
        if (!order.Order().SequenceEqual(Enumerable.Range(0, boxes.Count)))
        {
            throw new SequenceReplayException(-1, -1, "order is not a permutation of every placement index exactly once");
        }
    }

    /// <summary>Containment and non-overlap with what is present: the two invariants a sweep alone cannot prove.</summary>
    private static void ValidateBoxAtStep(int index, int step, IReadOnlyList<AxisAlignedBox> boxes, HashSet<int> present,
        Dimensions container)
    {
        var box = boxes[index];
        if (box.Origin.X < 0 || box.Origin.Y < 0 || box.Origin.Z < 0 || box.X2 > container.Length.Ticks
            || box.Y2 > container.Width.Ticks || box.Z2 > container.Height.Ticks)
        {
            throw new SequenceReplayException(index, step, "placement is outside the container");
        }
        if (present.Any(other => other != index && box.Intersects(boxes[other])))
        {
            throw new SequenceReplayException(index, step, "placement collides with an already present placement");
        }
    }

    private static bool ClearDirection(int index, IReadOnlyList<AxisAlignedBox> boxes, HashSet<int> present,
        Dimensions container, IReadOnlyList<string> directions) =>
        directions.Any(direction =>
        {
            var sweep = Directions.SweptVolume(boxes[index], container, direction);
            return !present.Any(other => other != index && sweep.Intersects(boxes[other]));
        });
}
