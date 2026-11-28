using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Route order: items due at an earlier stop must all be removable -- unblocked by whatever still
/// rests on them, and slidable straight out a door (all six walls when the container names none)
/// -- before the route moves on. A placement with no stop rides the whole route and still blocks.
/// </summary>
internal static class Unloading
{
    /// <summary>O(s · d² · n) for s stops, d items due at a stop and n placements.</summary>
    public static void Check(JsonValue packed, ImmutableArray<Entry> entries, SupportGraph graph, ImmutableArray<BigInteger> container,
        ImmutableArray<string> doors, List<ResultIssue> issues)
    {
        var stops = new Dictionary<int, JsonValue>();
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Spec is { StopIndex: not JsonNull } spec)
            {
                stops[index] = spec.StopIndex;
            }
        }
        if (stops.Count == 0)
        {
            return;
        }
        var directions = doors.IsEmpty ? RequestSpecs.ExitDirections : doors;
        var present = new HashSet<int>(Enumerable.Range(0, entries.Length));
        var idOrder = Comparer<JsonValue>.Create(Json.Compare);
        foreach (var stop in DistinctStops(stops))
        {
            var due = stops.Where(pair => Json.PyEquals(pair.Value, stop) && present.Contains(pair.Key)).Select(pair => pair.Key).ToHashSet();
            while (due.Count > 0)
            {
                var removable = due
                    .Where(item => !graph.DirectAbove[item].Overlaps(present) && HasClearExit(item, entries, present, container, directions))
                    .ToList();
                if (removable.Count == 0)
                {
                    var names = Json.Sorted(due.OrderBy(item => item).Select(item => entries[item].Id));
                    issues.Add(Issues.Of("unloading_order_violation",
                        $"{Extents.PackedId(packed)}: stop {PyFormat.Str(stop)} cannot fully unload {PyFormat.ListOf(names.Select(PyFormat.Repr))} before the route moves on"));
                    return;
                }
                var chosen = removable.MinBy(item => entries[item].Id, idOrder);
                present.Remove(chosen);
                due.Remove(chosen);
            }
        }
    }

    /// <summary><c>sorted(set(stops.values()))</c>: each stop once, first spelling kept, in Python's order.</summary>
    private static ImmutableArray<JsonValue> DistinctStops(Dictionary<int, JsonValue> stops)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        return Json.Sorted(stops.OrderBy(pair => pair.Key).Select(pair => pair.Value).Where(stop => seen.Add(Json.HashKey(stop))));
    }

    /// <summary>Whether the entry can slide straight out through one of the walls without passing through any present entry.</summary>
    private static bool HasClearExit(int item, ImmutableArray<Entry> entries, HashSet<int> present, ImmutableArray<BigInteger> container,
        ImmutableArray<string> directions) =>
        directions.Any(direction =>
        {
            var region = SweptRegion(entries[item].Envelope, container, direction);
            return !present.Any(other => other != item && region.Intersects(entries[other].Envelope));
        });

    /// <summary>The region between the envelope's own face and the matching container wall along a direction.</summary>
    private static Cuboid SweptRegion(Cuboid envelope, ImmutableArray<BigInteger> container, string direction)
    {
        var (x1, y1, z1, x2, y2, z2) = (envelope.X, envelope.Y, envelope.Z, envelope.X2, envelope.Y2, envelope.Z2);
        switch (direction)
        {
            case "+x":
                (x1, x2) = (x2, container[0]);
                break;
            case "-x":
                (x1, x2) = (BigInteger.Zero, x1);
                break;
            case "+y":
                (y1, y2) = (y2, container[1]);
                break;
            case "-y":
                (y1, y2) = (BigInteger.Zero, y1);
                break;
            case "+z":
                (z1, z2) = (z2, container[2]);
                break;
            default:
                (z1, z2) = (BigInteger.Zero, z1);
                break;
        }
        return new Cuboid(x1, y1, z1, x2 - x1, y2 - y1, z2 - z1);
    }
}
