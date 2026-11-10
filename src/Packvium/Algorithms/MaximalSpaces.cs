using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>A free cuboid region of a container.</summary>
public sealed record Space(Point Origin, Dimensions Dimensions)
{
    public AxisAlignedBox Box => new(Origin, Dimensions);
}

/// <summary>Maximal free spaces: carve placed boxes out of the free set and keep only non-dominated survivors.</summary>
public static class MaximalSpaces
{
    /// <summary>Past this many survivors the smallest are dropped; far beyond any real request.</summary>
    public const int MaxMaximalSpaces = 256;

    /// <summary>
    /// Carves every box out of a containment-free space list. Only new slabs need the dominance
    /// check, so it is O(new * s) rather than O(s^2) per carve.
    /// </summary>
    public static List<Space> SubtractAll(IReadOnlyList<Space> spaces, IEnumerable<AxisAlignedBox> boxes,
        SearchStats? stats = null)
    {
        var current = spaces.Select(space => (Space: space, IsNew: false)).ToList();
        foreach (var box in boxes)
        {
            if (stats is not null)
            {
                stats.SpacePartitions += current.Count;
            }
            var carved = new List<(Space, bool)>();
            foreach (var (space, isNew) in current)
            {
                if (space.Box.Intersects(box))
                {
                    carved.AddRange(Subtract(space, box).Select(part => (part, true)));
                }
                else
                {
                    carved.Add((space, isNew));
                }
            }
            current = carved;
        }
        var unique = new Dictionary<(long, long, long, long, long, long), (Space Space, bool IsNew)>();
        var firstSeen = new List<(long, long, long, long, long, long)>();
        foreach (var (space, isNew) in current)
        {
            var key = (space.Origin.X, space.Origin.Y, space.Origin.Z, space.Dimensions.Length.Ticks,
                space.Dimensions.Width.Ticks, space.Dimensions.Height.Ticks);
            if (!unique.ContainsKey(key))
            {
                firstSeen.Add(key);
            }
            unique[key] = (space, isNew);
        }
        var kept = firstSeen.Select(key => unique[key])
            .OrderBy(pair => (pair.Space.Origin.Z, pair.Space.Origin.Y, pair.Space.Origin.X))
            .ThenBy(pair => -pair.Space.Dimensions.Volume)
            .ToList();
        var extents = kept.Select(pair => ContainerState.Extent(pair.Space.Box)).ToList();
        var result = new List<Space>();
        for (var index = 0; index < kept.Count; index++)
        {
            var (space, isNew) = kept[index];
            if (isNew && Dominated(extents, index))
            {
                continue;
            }
            result.Add(space);
        }
        if (result.Count > MaxMaximalSpaces)
        {
            result = [.. result.OrderBy(space => -space.Dimensions.Volume).Take(MaxMaximalSpaces)
                .OrderBy(space => (space.Origin.Z, space.Origin.Y, space.Origin.X))];
        }
        return result;
    }

    private static bool Dominated(List<Bound> extents, int index)
    {
        var e = extents[index];
        for (var position = 0; position < extents.Count; position++)
        {
            var other = extents[position];
            if (position != index && other.X1 <= e.X1 && other.Y1 <= e.Y1 && other.Z1 <= e.Z1
                && e.X2 <= other.X2 && e.Y2 <= other.Y2 && e.Z2 <= other.Z2)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The six maximal slabs of a space that survive carving a box out of it.</summary>
    private static List<Space> Subtract(Space space, AxisAlignedBox box)
    {
        var source = space.Box;
        if (!source.Intersects(box))
        {
            return [space];
        }
        var output = new List<Space>();
        void Push(long x1, long y1, long z1, long x2, long y2, long z2)
        {
            if (x2 > x1 && y2 > y1 && z2 > z1)
            {
                output.Add(new Space(new Point(x1, y1, z1), Dimensions.OfTicks(x2 - x1, y2 - y1, z2 - z1)));
            }
        }
        Push(source.Origin.X, source.Origin.Y, source.Origin.Z, Math.Min(source.X2, box.Origin.X), source.Y2, source.Z2);
        Push(Math.Max(source.Origin.X, box.X2), source.Origin.Y, source.Origin.Z, source.X2, source.Y2, source.Z2);
        Push(source.Origin.X, source.Origin.Y, source.Origin.Z, source.X2, Math.Min(source.Y2, box.Origin.Y), source.Z2);
        Push(source.Origin.X, Math.Max(source.Origin.Y, box.Y2), source.Origin.Z, source.X2, source.Y2, source.Z2);
        Push(source.Origin.X, source.Origin.Y, source.Origin.Z, source.X2, source.Y2, Math.Min(source.Z2, box.Origin.Z));
        Push(source.Origin.X, source.Origin.Y, Math.Max(source.Origin.Z, box.Z2), source.X2, source.Y2, source.Z2);
        return output;
    }
}

/// <summary>
/// Places each item at the first maximal-space origin where it fits inside that space. Per item
/// O(s * F) for s spaces and F one point's candidate search, plus one carve.
/// </summary>
public sealed class MaximalSpaceSolver(IReadOnlyList<IPlacementConstraint>? constraints = null) : ISingleContainerSolver
{
    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];

    public string Name => "maximal_spaces";

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        var state = new ContainerState(container, sequence);
        var all = BeamPack.DefaultConstraints(config, constraints);
        var spaces = MaximalSpaces.SubtractAll([new Space(Point.Origin, container.InnerDimensions)],
            container.Obstacles.SelectMany(obstacle => obstacle.Boxes), stats);
        var unplaced = new List<ItemInstance>();
        var batches = CandidateFinder.GroupBatches(items);
        for (var position = 0; position < batches.Count; position++)
        {
            var batch = batches[position];
            var (snapshot, saved) = batch.Length > 1 ? (state.Copy(), spaces.ToList()) : (state, spaces);
            var placed = true;
            try
            {
                foreach (var item in batch)
                {
                    stats.SearchNodesExpanded++;
                    if (Choose(state, spaces, item, config, all, stats, deadline) is not { } chosen)
                    {
                        placed = false;
                        break;
                    }
                    state = CandidateFinder.Extended(state, item, chosen);
                    spaces = MaximalSpaces.SubtractAll(spaces, [new AxisAlignedBox(chosen.Point, chosen.EnvelopeDimensions)], stats);
                }
            }
            catch (TimeLimitReachedException)
            {
                var pending = batches.Skip(position).SelectMany(later => later);
                return new SingleContainerSolution(snapshot, [.. unplaced, .. pending], false, true);
            }
            if (!placed)
            {
                (state, spaces) = (snapshot, saved);
                unplaced.AddRange(batch);
            }
        }
        return new SingleContainerSolution(state, [.. unplaced]);
    }

    private static Candidate? Choose(ContainerState state, List<Space> spaces, ItemInstance item, PackingConfig config,
        IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline)
    {
        foreach (var space in spaces)
        {
            deadline.Check();
            foreach (var candidate in CandidateFinder.Find(state, item, config, constraints, stats, deadline, null,
                         [space.Origin]))
            {
                if (candidate.EnvelopeDimensions.FitsInside(space.Dimensions))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
