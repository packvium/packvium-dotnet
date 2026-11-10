using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;

namespace Packvium.Algorithms;

/// <summary>One container's outcome from a single-container solver.</summary>
public sealed record SingleContainerSolution(
    ContainerState State,
    ImmutableArray<ItemInstance> Unpacked,
    bool Exhaustive = false,
    bool TimeLimitReached = false,
    bool DominantLattice = false);

/// <summary>A Strategy that packs one container.</summary>
public interface ISingleContainerSolver
{
    string Name { get; }

    /// <summary>A solver that discards the order it is handed; the portfolio runs it once.</summary>
    bool OrderInsensitive => false;

    SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline);
}

/// <summary>
/// Greedy placement widened into a beam by <c>max_candidates_per_item</c>; a width of one is
/// plain best-fit greedy. With a container-plan width above one it becomes an anytime search
/// seeded by a complete greedy rollout and bounded by <c>container_plan_node_limit</c>. Each
/// layer is O(w * b * F) for beam width w, branch width b and F one candidate search, plus
/// O(w b log(w b)) to rank the layer.
/// </summary>
public static class BeamPack
{
    private sealed record Node(ContainerState State, ImmutableArray<ItemInstance> Unplaced);

    public static IReadOnlyList<IPlacementConstraint> DefaultConstraints(PackingConfig config,
        IReadOnlyList<IPlacementConstraint>? custom = null) =>
    [
        new FloorConstraint(), new ContainerEligibilityConstraint(), new CompatibilityConstraint(),
        new TagCountConstraint(), new SupportConstraint(config.MinimumSupportRatio), new TopLoadConstraint(),
        new RouteOrderConstraint(), new StopAccessibilityConstraint(config.AccessDirections), new AxleLoadConstraint(),
        .. custom ?? [],
    ];

    public static SingleContainerSolution Pack(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline)
    {
        var branchWidth = Math.Max(1, config.MaxCandidatesPerItem);
        var width = Math.Max(1, config.ContainerPlanBeamWidth);
        var batches = CandidateFinder.GroupBatches(items);
        return width == 1
            ? Greedy(container, sequence, batches, config, constraints, stats, deadline, branchWidth)
            : Anytime(container, sequence, batches, config, constraints, stats, deadline, branchWidth, width);
    }

    private static SingleContainerSolution Greedy(Container container, int sequence,
        List<ImmutableArray<ItemInstance>> batches, PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints,
        SearchStats stats, Deadline deadline, long branchWidth)
    {
        List<Node> beam = [new(new ContainerState(container, sequence), [])];
        for (var position = 0; position < batches.Count; position++)
        {
            var batch = batches[position];
            var expansions = new List<Node>();
            var exhausted = false;
            foreach (var node in beam)
            {
                stats.SearchNodesExpanded++;
                var children = Children(node.State, batch, config, constraints, stats, deadline, branchWidth, ref exhausted);
                if (children.Count > 0)
                {
                    expansions.AddRange(children.Select(child => new Node(child, node.Unplaced)));
                }
                else
                {
                    expansions.Add(node with { Unplaced = [.. node.Unplaced, .. batch] });
                }
            }
            beam = [.. expansions.OrderBy(node => NodeKey(node, []))];
            beam = [.. beam.Take((int)Math.Min(branchWidth, int.MaxValue))];
            if (exhausted)
            {
                var pending = batches.Skip(position + 1).SelectMany(later => later);
                return new SingleContainerSolution(beam[0].State, [.. beam[0].Unplaced, .. pending], false, true);
            }
        }
        return new SingleContainerSolution(beam[0].State, beam[0].Unplaced);
    }

    private static SingleContainerSolution Anytime(Container container, int sequence,
        List<ImmutableArray<ItemInstance>> batches, PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints,
        SearchStats stats, Deadline deadline, long branchWidth, long width)
    {
        var nodeLimit = config.ContainerPlanNodeLimit;
        long nodes = 0;
        List<Node> beam = [new(new ContainerState(container, sequence), [])];
        var incumbent = GreedyRollout(container, sequence, batches, config, constraints, stats, deadline);
        for (var position = 0; position < batches.Count; position++)
        {
            var batch = batches[position];
            var expansions = new List<Node>();
            var exhausted = false;
            foreach (var node in beam)
            {
                if (nodes >= nodeLimit)
                {
                    exhausted = true;
                    break;
                }
                nodes++;
                stats.SearchNodesExpanded++;
                var children = Children(node.State, batch, config, constraints, stats, deadline, branchWidth, ref exhausted);
                expansions.AddRange(children.Select(child => new Node(child, node.Unplaced)));
                // An explicit skip child keeps a deliberately skipped large item alive, which
                // turns the beam into an anytime knapsack rather than irrevocable greedy paths.
                expansions.Add(node with { Unplaced = [.. node.Unplaced, .. batch] });
            }
            ImmutableArray<ItemInstance> future = [.. batches.Skip(position + 1).SelectMany(later => later)];
            foreach (var expansion in expansions)
            {
                var candidate = new Node(expansion.State, [.. expansion.Unplaced, .. future]);
                if (NodeKey(candidate, []) < NodeKey(incumbent, []))
                {
                    incumbent = candidate;
                }
            }
            if (expansions.Count == 0)
            {
                break;
            }
            beam = [.. expansions.OrderBy(node => NodeKey(node, future)).Take((int)Math.Min(width, int.MaxValue))];
            if (exhausted)
            {
                return new SingleContainerSolution(incumbent.State, incumbent.Unplaced, false, deadline.Expired);
            }
        }
        var completed = beam.Count > 0 ? beam.MinBy(node => NodeKey(node, []))! : incumbent;
        var chosen = NodeKey(incumbent, []) < NodeKey(completed, []) ? incumbent : completed;
        return new SingleContainerSolution(chosen.State, chosen.Unplaced);
    }

    /// <summary>A complete greedy pass, so a bounded wider beam always has a finished incumbent.</summary>
    private static Node GreedyRollout(Container container, int sequence, List<ImmutableArray<ItemInstance>> batches,
        PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline)
    {
        var greedy = new ContainerState(container, sequence);
        var unplaced = ImmutableArray<ItemInstance>.Empty;
        for (var position = 0; position < batches.Count; position++)
        {
            List<ContainerState> children;
            try
            {
                children = [.. CandidateFinder.PlaceBatch(greedy, batches[position], config, constraints, stats, deadline, 1)];
            }
            catch (TimeLimitReachedException)
            {
                unplaced = [.. unplaced, .. batches.Skip(position).SelectMany(later => later)];
                break;
            }
            if (children.Count > 0)
            {
                greedy = children[0];
            }
            else
            {
                unplaced = [.. unplaced, .. batches[position]];
            }
        }
        return new Node(greedy, unplaced);
    }

    private static List<ContainerState> Children(ContainerState state, ImmutableArray<ItemInstance> batch,
        PackingConfig config, IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline,
        long branchWidth, ref bool exhausted)
    {
        try
        {
            return [.. CandidateFinder.PlaceBatch(state, batch, config, constraints, stats, deadline, branchWidth)];
        }
        catch (TimeLimitReachedException)
        {
            exhausted = true;
            return [];
        }
    }

    /// <summary>
    /// The reference <c>_node_key</c>: the optimistic unpacked bound, then unplaced count, more
    /// placements, lower stack, more used volume, and the placement signature as the final tie-break.
    /// </summary>
    private static SortKey NodeKey(Node node, IReadOnlyList<ItemInstance> future)
    {
        var state = node.State;
        var signature = string.Join("|", state.Placements.Select(p =>
            $"{p.Instance.Id}@{p.EnvelopeOrigin.X},{p.EnvelopeOrigin.Y},{p.EnvelopeOrigin.Z}"));
        return SortKey.Of(UnpackedLowerBound(state, node.Unplaced, future), node.Unplaced.Length,
            -state.Placements.Count, state.MaxZ, -state.UsedVolumeTicks, signature);
    }

    /// <summary>
    /// An admissible lower bound on final unpacked cardinality from volume and weight alone; the
    /// volume half is disabled for nesting, whose nominal volumes are not additive.
    /// </summary>
    private static long UnpackedLowerBound(ContainerState state, ImmutableArray<ItemInstance> unplaced,
        IReadOnlyList<ItemInstance> future)
    {
        if (future.Count == 0)
        {
            return unplaced.Length;
        }
        long possible = future.Count;
        if (!future.Any(item => item.Item.NestingHeight is not null))
        {
            var freeVolume = Int128.Max(0, state.Container.InnerDimensions.Volume - state.UsedVolumeTicks);
            possible = Math.Min(possible, MaximumCount(future.Select(item => item.Dimensions.Volume), freeVolume));
        }
        if (state.Container.MaxPayload is { } payload)
        {
            Int128 freeWeight = Math.Max(0, payload.Ticks - state.PayloadTicks);
            possible = Math.Min(possible, MaximumCount(future.Select(item => (Int128)item.Weight.Ticks), freeWeight));
        }
        return unplaced.Length + future.Count - possible;
    }

    /// <summary>Cheapest units first: an optimistic count for one additive resource.</summary>
    private static long MaximumCount(IEnumerable<Int128> costs, Int128 capacity)
    {
        Int128 used = 0;
        long count = 0;
        foreach (var cost in costs.Order())
        {
            if (used + cost > capacity)
            {
                break;
            }
            used += cost;
            count++;
        }
        return count;
    }
}
