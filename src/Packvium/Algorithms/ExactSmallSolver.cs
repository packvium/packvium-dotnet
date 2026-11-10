using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;


namespace Packvium.Algorithms;

/// <summary>
/// Depth-first branch and bound over whole group batches. Exact only for the discrete
/// candidate model and the item-count objective, so it never claims global optimality.
/// Exponential in the batch count, which <c>exact_item_limit</c> bounds.
/// </summary>
public sealed class ExactSmallSolver(IReadOnlyList<IPlacementConstraint>? constraints = null) : ISingleContainerSolver
{
    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];

    public string Name => "exact_small";

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        if (items.Count > config.ExactItemLimit)
        {
            throw new ArgumentException("exact-small item limit exceeded");
        }
        var search = new Search(container, sequence, items, config, BeamPack.DefaultConstraints(config, constraints),
            stats, deadline);
        try
        {
            search.Dfs(0, new ContainerState(container, sequence), items.Count, 0);
        }
        catch (TimeLimitReachedException)
        {
            // More search time may only add to the incumbent, never erase it.
        }
        var packed = search.Best.Placements.Select(p => p.Instance.Id).ToHashSet(StringComparer.Ordinal);
        return new SingleContainerSolution(search.Best, [.. items.Where(item => !packed.Contains(item.Id))], false,
            deadline.Expired);
    }

    private sealed class Search
    {
        private readonly Container container;
        private readonly IReadOnlyList<ItemInstance> items;
        private readonly PackingConfig config;
        private readonly IReadOnlyList<IPlacementConstraint> constraints;
        private readonly SearchStats stats;
        private readonly Deadline deadline;
        private readonly List<ImmutableArray<ItemInstance>> batches;
        private readonly Int128[] batchVolumes;
        private readonly Int128[] suffixVolumes;
        private Int128 bestVolume;
        private SortKey bestRank;

        public Search(Container container, int sequence, IReadOnlyList<ItemInstance> items, PackingConfig config,
            IReadOnlyList<IPlacementConstraint> constraints, SearchStats stats, Deadline deadline)
        {
            this.container = container;
            this.items = items;
            this.config = config;
            this.constraints = constraints;
            this.stats = stats;
            this.deadline = deadline;
            batches = CandidateFinder.GroupBatches(items);
            batchVolumes = [.. batches.Select(batch => batch.Aggregate(Int128.Zero, (sum, item) => sum + item.Dimensions.Volume))];
            suffixVolumes = new Int128[batches.Count + 1];
            for (var index = batches.Count - 1; index >= 0; index--)
            {
                suffixVolumes[index] = batchVolumes[index] + suffixVolumes[index + 1];
            }
            Best = new ContainerState(container, sequence);
            bestRank = Rank(Best, 0);
        }

        public ContainerState Best { get; private set; }

        public void Dfs(int index, ContainerState state, int reachable, Int128 stateVolume)
        {
            if (deadline.Expired)
            {
                return;
            }
            stats.SearchNodesExpanded++;
            var stateCount = state.Placements.Count;
            var candidateRank = Rank(state, stateVolume);
            if (candidateRank < bestRank)
            {
                (Best, bestVolume, bestRank) = (state, stateVolume, candidateRank);
            }
            if (index >= batches.Count)
            {
                return;
            }
            var potentialCount = stateCount + reachable;
            var bestCount = Best.Placements.Count;
            if (potentialCount < bestCount)
            {
                return;
            }
            if (config.Objective != "lowest_landed_cost" && potentialCount == bestCount
                && stateVolume + suffixVolumes[index] <= bestVolume)
            {
                return;
            }
            var batch = batches[index];
            var remaining = reachable - batch.Length;
            foreach (var child in CandidateFinder.PlaceBatch(state, batch, config, constraints, stats, deadline, null))
            {
                Dfs(index + 1, child, remaining, stateVolume + batchVolumes[index]);
                if (Best.Placements.Count == items.Count || deadline.Expired)
                {
                    return;
                }
            }
            Dfs(index + 1, state, remaining, stateVolume);
        }

        /// <summary>
        /// The incumbent order: count then placed volume, except that landed cost precedes
        /// volume when it is the objective, since a promotional bracket can make a heavier
        /// equal-count subset cheaper.
        /// </summary>
        private SortKey Rank(ContainerState state, Int128 stateVolume)
        {
            var count = state.Placements.Count;
            if (config.Objective != "lowest_landed_cost")
            {
                return SortKey.Of(-count, -stateVolume);
            }
            var billed = Billing.BilledTicks(container, container.TareWeight.Ticks + state.PayloadTicks, config);
            var charge = container.RateTable?.ChargeMinorOrNull(Billing.Grams(billed));
            return SortKey.Of(-count, charge ?? Billing.UnpriceableMinor, -stateVolume);
        }
    }
}
