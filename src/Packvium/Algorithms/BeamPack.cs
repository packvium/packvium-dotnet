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

    /// <summary>
    /// The items of later batches, reduced to what the unpacked bound reads: the prefix sums of
    /// their ascending volumes and weights, built once per layer instead of once per ranked node.
    /// <see cref="Volumes"/> is null under nesting and <see cref="Weights"/> without a payload limit.
    /// </summary>
    private sealed record Future(Int128[]? Volumes, Int128[]? Weights, int Count)
    {
        public static readonly Future None = new(null, null, 0);

        public static Future Of(IReadOnlyList<ItemInstance> items, bool hasMaxPayload)
        {
            if (items.Count == 0)
            {
                return None;
            }
            var volumes = items.Any(item => item.Item.NestingHeight is not null)
                ? null
                : PrefixSums(items.Select(item => item.Dimensions.Volume));
            var weights = hasMaxPayload ? PrefixSums(items.Select(item => (Int128)item.Weight.Ticks)) : null;
            return new Future(volumes, weights, items.Count);
        }

        private static Int128[] PrefixSums(IEnumerable<Int128> costs)
        {
            var sums = costs.Order().ToArray();
            for (var index = 1; index < sums.Length; index++)
            {
                sums[index] += sums[index - 1];
            }
            return sums;
        }
    }

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
            beam = [.. expansions.OrderBy(node => NodeKey(node, Future.None))];
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
        // Kept beside the incumbent, so each expansion costs one key, not two. With no future the
        // key's first component is the unplaced count, kept apart so a loser is skipped unbuilt.
        var incumbentKey = NodeKey(incumbent, Future.None);
        var incumbentUnplaced = incumbent.Unplaced.Length;
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
            var precomputed = Future.Of(future, container.MaxPayload is not null);
            foreach (var expansion in expansions)
            {
                // More unplaced items loses on the key's first component whatever follows, so the
                // signature -- the costly part -- is built only for a candidate that can win, and
                // the node only for one that does.
                var unplaced = expansion.Unplaced.Length + future.Length;
                if (unplaced > incumbentUnplaced)
                {
                    continue;
                }
                var candidateKey = NodeKey(expansion.State, unplaced, Future.None);
                if (candidateKey < incumbentKey)
                {
                    incumbent = new Node(expansion.State, [.. expansion.Unplaced, .. future]);
                    incumbentKey = candidateKey;
                    incumbentUnplaced = unplaced;
                }
            }
            if (expansions.Count == 0)
            {
                break;
            }
            beam = [.. expansions.OrderBy(node => NodeKey(node, precomputed)).Take((int)Math.Min(width, int.MaxValue))];
            if (exhausted)
            {
                return new SingleContainerSolution(incumbent.State, incumbent.Unplaced, false, deadline.Expired);
            }
        }
        var completed = beam.Count > 0 ? beam.MinBy(node => NodeKey(node, Future.None))! : incumbent;
        var chosen = incumbentKey < NodeKey(completed, Future.None) ? incumbent : completed;
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
    private static SortKey NodeKey(Node node, Future future) => NodeKey(node.State, node.Unplaced.Length, future);

    private static SortKey NodeKey(ContainerState state, int unplacedCount, Future future)
    {
        var signature = string.Join("|", state.Placements.Select(p =>
            $"{p.Instance.Id}@{p.EnvelopeOrigin.X},{p.EnvelopeOrigin.Y},{p.EnvelopeOrigin.Z}"));
        return SortKey.Of(UnpackedLowerBound(state, unplacedCount, future), unplacedCount,
            -state.Placements.Count, state.MaxZ, -state.UsedVolumeTicks, signature);
    }

    /// <summary>
    /// An admissible lower bound on final unpacked cardinality from volume and weight alone; the
    /// volume half is disabled for nesting, whose nominal volumes are not additive. O(log f) for f
    /// future items, from their prefix sums.
    /// </summary>
    private static long UnpackedLowerBound(ContainerState state, int unplacedCount, Future future)
    {
        if (future.Count == 0)
        {
            return unplacedCount;
        }
        long possible = future.Count;
        if (future.Volumes is { } volumes)
        {
            var freeVolume = Int128.Max(0, state.Container.InnerDimensions.Volume - state.UsedVolumeTicks);
            possible = Math.Min(possible, CountWithCapacity(volumes, freeVolume));
        }
        if (future.Weights is { } weights && state.Container.MaxPayload is { } payload)
        {
            Int128 freeWeight = Math.Max(0, payload.Ticks - state.PayloadTicks);
            possible = Math.Min(possible, CountWithCapacity(weights, freeWeight));
        }
        return unplacedCount + future.Count - possible;
    }

    /// <summary>
    /// Cheapest units first: an optimistic count for one additive resource, which can only
    /// overestimate what fits. Costs are non-negative, so the prefix sums never decrease and the
    /// count is the number of sums within capacity, one binary search.
    /// </summary>
    internal static long CountWithCapacity(Int128[] cumulativeCosts, Int128 capacity)
    {
        var (low, high) = (0, cumulativeCosts.Length);
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (cumulativeCosts[middle] <= capacity)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }
}
