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

/// <summary>Unloading replay over a finished scene, using the same sweep geometry the constraints use.</summary>
public static class RouteSequence
{
    /// <summary>
    /// An unloading order that fully empties each stop, ascending, before the next. An item with
    /// no stop is never removed but still blocks. Each step picks the lowest removable index.
    /// O(s * n^2 * d) for s steps, n boxes and d directions.
    /// </summary>
    public static List<int> SafeRouteRemovalOrder(IReadOnlyList<AxisAlignedBox> boxes, IReadOnlyList<long?> stops,
        Dimensions container, IReadOnlyList<string>? directions = null)
    {
        directions ??= Directions.All;
        foreach (var direction in directions.Where(direction => !Directions.All.Contains(direction)))
        {
            throw new InvalidDirectionException(direction);
        }
        var contacts = new ContactGraph(boxes);
        var dependsOn = Enumerable.Range(0, boxes.Count).Select(index => contacts.Children(index).ToHashSet()).ToList();
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

    private static bool ClearDirection(int index, IReadOnlyList<AxisAlignedBox> boxes, HashSet<int> present,
        Dimensions container, IReadOnlyList<string> directions) =>
        directions.Any(direction =>
        {
            var sweep = Directions.SweptVolume(boxes[index], container, direction);
            return !present.Any(other => other != index && sweep.Intersects(boxes[other]));
        });
}
