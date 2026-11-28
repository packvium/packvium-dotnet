using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>
/// Packs solid single-type rectangular blocks into maximal free spaces, in both a count-first
/// and a volume-first mode, and keeps the better. Only for rigid plain boxes no per-placement rule
/// can tell apart, and only when no support is required: a block set on a smaller one overhangs
/// it, so the members of its bottom layer report the support they really have. Otherwise it falls
/// back to <see cref="ExtremePointSolver"/>. Enumeration is O(B * S * T * R * X * Y), bounded by the
/// deadline and <c>container_plan_node_limit</c>; filling a block costs O(m) per bottom-layer
/// member for its support, m the placements so far.
/// </summary>
public sealed class HomogeneousBlockSolver(IReadOnlyList<IPlacementConstraint>? constraints = null,
    ImmutableArray<string> modes = default, long? nodeLimit = null) : ISingleContainerSolver
{
    /// <summary>Both enumerations, count first, as <c>quality</c> and an explicit request run them.</summary>
    public static readonly ImmutableArray<string> Modes = ["count", "volume"];

    /// <summary>
    /// Distinct group shapes one block solve remembers. The memo only saves arithmetic, so a full
    /// one is simply not added to.
    /// </summary>
    private const int LatticeMemoLimit = 4096;

    /// <summary>Leading blocks the <c>quality</c> lookahead completes in each round.</summary>
    private const int LookaheadWidth = 4;

    /// <summary>The lookahead's lattice-node budget per mode, as a multiple of <c>container_plan_node_limit</c>.</summary>
    private const long LookaheadNodeFactor = 64;

    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];
    private readonly ImmutableArray<string> modes = modes.IsDefault ? Modes : modes;

    public string Name => "homogeneous_blocks";

    public bool OrderInsensitive => true;

    /// <summary>
    /// The lattice-node bound of the block start <c>fast</c> and <c>balanced</c> add:
    /// their <c>container_plan_node_limit</c> of one would stop the enumeration before its first
    /// block. Null for the profile's own solver, which reads the configured limit.
    /// </summary>
    public long? NodeLimit { get; } = nodeLimit;

    private sealed record Block(Space Space, string ItemId, Rotation Rotation, Dimensions Physical, Dimensions Envelope,
        long Nx, long Ny, long Nz, long Count, SortKey Score)
    {
        /// <summary>The score's first field: minus the count, or minus the used volume.</summary>
        public Int128 Lead { get; init; }

        public AxisAlignedBox Box => new(Space.Origin, Dimensions.OfTicks(Nx * Envelope.Length.Ticks,
            Ny * Envelope.Width.Ticks, Nz * Envelope.Height.Ticks));
    }

    private sealed record Form(Rotation Rotation, Dimensions Physical, Dimensions Envelope, Int128 Volume);

    /// <summary>One (space, type, rotation) group's enumeration, summarised (see <see cref="Lattice"/>).</summary>
    private readonly record struct LatticeSummaryKey(long Columns, long Rows, long Layers, long Capacity);

    private readonly record struct GroupLattice(long Nodes, long Count, long Nx, long Ny, long Nz);

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        if (!Supports(container, items, config))
        {
            return new ExtremePointSolver(constraints).PackOne(container, sequence, items, config, stats, deadline);
        }
        if (NodeLimit is { } limit)
        {
            config = config.WithSearchWidths(config.MaxCandidatesPerItem, config.ContainerPlanBeamWidth, limit);
        }
        SingleContainerSolution? best = null;
        var reached = false;
        var byVolume = config.RanksByVolume;
        // Group enumerations are pure arithmetic, so the second mode and every later round reuse them.
        var lattices = new Dictionary<LatticeSummaryKey, GroupLattice>();
        foreach (var mode in modes)
        {
            if (deadline.Expired)
            {
                reached = true;
                break;
            }
            var solution = PackMode(container, sequence, items, config, stats, deadline, mode == "count", lattices);
            if (config.Profile == SolverProfile.Quality && !deadline.Expired)
            {
                solution = Lookahead(container, sequence, items, config, stats, deadline, mode == "count", lattices,
                    solution);
            }
            reached = reached || solution.TimeLimitReached;
            if (best is null || SolutionKey(solution, byVolume) < SolutionKey(best, byVolume))
            {
                best = solution;
            }
        }
        return best is null
            ? new SingleContainerSolution(new ContainerState(container, sequence), [.. items], false, reached)
            : new SingleContainerSolution(best.State, best.Unpacked, false, reached);
    }

    /// <summary>Whether every block this solver could build honours every rule of the request.</summary>
    public bool Supports(Container container, IReadOnlyList<ItemInstance> items, PackingConfig config)
    {
        if (constraints.Count > 0 || container.Obstacles.Length > 0 || container.Preloaded.Length > 0
            || container.Axles is not null || config.MinimumSupportRatio > 0 || container.IsFlatBag
            || !container.TagLimits.IsEmpty || container.MaxStackDensity is not null || container.VoidFillReserveRatio > 0)
        {
            return false;
        }
        // Block-on-block contacts would need a second implementation of the stacking-group rule;
        // the general solver already walks every edge.
        return items.All(instance =>
        {
            var item = instance.Item;
            return item.ShapeType == ShapeType.RigidCuboid && item.Group is null && item.Tags.Count == 0 && item.IncompatibleTags.Count == 0
                   && item.EligibleContainerTags.Count == 0 && item.Stackable && !item.MustBeOnFloor
                   && item.MaxTopLoad is null && item.MaxStackedItems is null && item.MinimumSupportRatio == 0
                   && item.GroundContactRule is null or "free" && item.NestingHeight is null && item.StopIndex is null
                   && item.AcceptsStackingGroups.Count == 0;
        });
    }

    private static SortKey SolutionKey(SingleContainerSolution solution, bool byVolume)
    {
        var state = solution.State;
        var signature = SortKey.Of([.. state.Placements.Select(p => (object)SortKey.Of(p.Instance.Id,
            p.EnvelopeOrigin.X, p.EnvelopeOrigin.Y, p.EnvelopeOrigin.Z))]);
        return byVolume
            ? SortKey.Of(-state.UsedVolumeTicks, solution.Unpacked.Length, state.MaxZ, signature)
            : SortKey.Of(solution.Unpacked.Length, -state.UsedVolumeTicks, state.MaxZ, signature);
    }

    private static SingleContainerSolution PackMode(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline, bool countFirst,
        Dictionary<LatticeSummaryKey, GroupLattice> lattices)
    {
        var round = Round.Empty(container, sequence, items, config);
        long nodes = 0;
        var (solution, reached) = Complete(round, container, config, stats, deadline, countFirst, lattices, ref nodes);
        return new SingleContainerSolution(solution.State, solution.Unpacked, false, reached || deadline.Expired);
    }

    /// <summary>
    /// The mutable state of one enumeration: the committed placements, the free spaces and each
    /// type's instances still to place, in code-point order of their ids.
    /// </summary>
    private sealed record Round(ContainerState State, List<Space> Spaces,
        SortedDictionary<string, List<ItemInstance>> Remaining, Dictionary<string, List<Form>> TypeForms)
    {
        public List<Space> Spaces { get; set; } = Spaces;

        public static Round Empty(Container container, int sequence, IReadOnlyList<ItemInstance> items, PackingConfig config)
        {
            var remaining = new SortedDictionary<string, List<ItemInstance>>(CodePointComparer.Instance);
            foreach (var instance in items)
            {
                if (!remaining.TryGetValue(instance.Item.Id, out var list))
                {
                    remaining[instance.Item.Id] = list = [];
                }
                list.Add(instance);
            }
            var forms = remaining.ToDictionary(entry => entry.Key, entry => Forms(entry.Value[0], config), StringComparer.Ordinal);
            return new Round(new ContainerState(container, sequence), [new Space(Point.Origin, container.InnerDimensions)],
                remaining, forms);
        }

        public bool HasWork => Spaces.Count > 0 && Remaining.Values.Any(list => list.Count > 0);

        /// <summary>A copy a trial completion may mutate; the forms are shared, being read only.</summary>
        public Round Copy()
        {
            var remaining = new SortedDictionary<string, List<ItemInstance>>(CodePointComparer.Instance);
            foreach (var (itemId, available) in Remaining)
            {
                remaining[itemId] = [.. available];
            }
            return new Round(State.Copy(), Spaces, remaining, TypeForms);
        }

        public ImmutableArray<ItemInstance> Unpacked() => [.. Remaining.Values.SelectMany(list => list)];
    }

    /// <summary>
    /// Commits each round's leading block until nothing more fits; mutates <paramref name="round"/>.
    /// Returns the solution and whether a limit stopped it (a stopped round commits nothing).
    /// </summary>
    private static (SingleContainerSolution Solution, bool Reached) Complete(Round round, Container container,
        PackingConfig config, SearchStats stats, Deadline deadline, bool countFirst,
        Dictionary<LatticeSummaryKey, GroupLattice> lattices, ref long nodes)
    {
        var reached = false;
        while (round.HasWork)
        {
            var leading = LeadingBlocks(round, container, config, stats, deadline, countFirst, lattices, 1, ref nodes,
                ref reached);
            if (leading.Count == 0 || reached)
            {
                break;
            }
            Commit(round, leading[0], config, stats);
        }
        return (new SingleContainerSolution(round.State, round.Unpacked(), false, reached), reached);
    }

    /// <summary>
    /// <paramref name="greedy"/>, or a better answer from completing each round's leading blocks.
    /// Each round ranks its <see cref="LookaheadWidth"/> leading blocks, completes each one greedily
    /// on a copy and commits the block whose completion ranks best. The greedy answer is the first
    /// completion, so the result is never worse than it; the round's first block is the greedy choice
    /// after the block committed last, so its completion is the one already known and is not
    /// recomputed -- unless the greedy pass was stopped by <c>container_plan_node_limit</c>, when the
    /// first round completes it under the lookahead's larger budget. The work is counted against
    /// <see cref="LookaheadNodeFactor"/> times <c>container_plan_node_limit</c> lattice nodes; when that
    /// runs out the best completion so far stands, and only the deadline or the effort budget marks the
    /// answer as stopped by a limit. At most W completions of O(B) rounds per committed block, B the
    /// blocks of one completion, each round costing what a greedy round costs.
    /// </summary>
    private static SingleContainerSolution Lookahead(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline, bool countFirst,
        Dictionary<LatticeSummaryKey, GroupLattice> lattices, SingleContainerSolution greedy)
    {
        var limit = config.ContainerPlanNodeLimit;
        var budget = config.WithSearchWidths(config.MaxCandidatesPerItem, config.ContainerPlanBeamWidth,
            limit > long.MaxValue / LookaheadNodeFactor ? long.MaxValue : limit * LookaheadNodeFactor);
        var byVolume = config.RanksByVolume;
        var round = Round.Empty(container, sequence, items, config);
        var (best, bestKey) = (greedy, SolutionKey(greedy, byVolume));
        var known = greedy.TimeLimitReached ? null : greedy;
        long nodes = 0;
        while (round.HasWork)
        {
            var reached = false;
            var leading = LeadingBlocks(round, container, budget, stats, deadline, countFirst, lattices, LookaheadWidth,
                ref nodes, ref reached);
            if (leading.Count == 0 || reached)
            {
                break;
            }
            (SortKey Key, SingleContainerSolution Solution, Block Block)? chosen =
                known is null ? null : (SolutionKey(known, byVolume), known, leading[0]);
            foreach (var block in known is null ? leading : leading.Skip(1))
            {
                var trial = round.Copy();
                Commit(trial, block, config, stats);
                (var completion, reached) = Complete(trial, container, budget, stats, deadline, countFirst, lattices,
                    ref nodes);
                if (reached)
                {
                    break;
                }
                var key = SolutionKey(completion, byVolume);
                if (chosen is not { } current || key < current.Key)
                {
                    chosen = (key, completion, block);
                }
            }
            if (reached || chosen is not { } winner)
            {
                break;
            }
            known = winner.Solution;
            if (winner.Key < bestKey)
            {
                (best, bestKey) = (winner.Solution, winner.Key);
            }
            Commit(round, winner.Block, config, stats);
        }
        return new SingleContainerSolution(best.State, best.Unpacked, false, best.TimeLimitReached || deadline.Expired);
    }

    /// <summary>Places every member of <paramref name="block"/> and carves its box out of the free spaces.</summary>
    private static void Commit(Round round, Block block, PackingConfig config, SearchStats stats)
    {
        var available = round.Remaining[block.ItemId];
        var chosen = available.Take((int)block.Count).ToList();
        available.RemoveRange(0, (int)block.Count);
        FillBlock(round.State, block, chosen, config.Clearance.Ticks, stats);
        round.Spaces = MaximalSpaces.SubtractAll(round.Spaces, [block.Box], stats);
    }

    /// <summary>Each unique rotation of a type with its envelope and physical volume.</summary>
    private static List<Form> Forms(ItemInstance prototype, PackingConfig config) =>
    [
        .. prototype.Dimensions.UniqueRotations(prototype.Item.AllowedRotations).Select(pair => new Form(pair.Rotation,
            pair.Dimensions, config.Clearance.Ticks != 0 ? pair.Dimensions.Expand(config.Clearance) : pair.Dimensions,
            pair.Dimensions.Volume)),
    ];

    /// <summary>How many of a type one block may still take: stock, item limit and payload.</summary>
    private static long Capacity(Container container, ContainerState state, List<ItemInstance> available)
    {
        long capacity = available.Count;
        if (container.MaxItems is { } maxItems)
        {
            capacity = Math.Min(capacity, maxItems - state.PlacementCount);
        }
        var weight = available[0].Weight.Ticks;
        if (container.MaxPayload is { } payload && weight > 0)
        {
            capacity = Math.Min(capacity, IntMath.FloorDiv(payload.Ticks - state.PayloadTicks, weight));
        }
        return capacity;
    }

    /// <summary>
    /// One round's <paramref name="leaders"/> leading blocks, best first. Each (space, type, rotation)
    /// group is summarised by <see cref="Lattice"/>, whose first largest count is the only node of the
    /// group that can win, and its nodes are charged at once. A counted limit falling inside a group is
    /// charged exactly the nodes the per-node loop would have expanded before stopping; a wall-clock
    /// expiry inside a group is seen at the next group instead, and any stop discards the whole round.
    /// O(S * T * R) groups per round, each O(X * Y) on its first appearance and O(1) from the memo,
    /// plus O(L) to keep each of at most L leaders in order.
    /// </summary>
    private static List<Block> LeadingBlocks(Round round, Container container, PackingConfig config, SearchStats stats,
        Deadline deadline, bool countFirst, Dictionary<LatticeSummaryKey, GroupLattice> lattices, int leaders,
        ref long nodes, ref bool reached)
    {
        var nodeLimit = config.ContainerPlanNodeLimit;
        var nodeCeiling = deadline.SearchNodeCeiling(stats);
        var (state, spaces, remaining, forms) = (round.State, round.Spaces, round.Remaining, round.TypeForms);
        var leading = new List<Block>(leaders + 1);
        // Capacity reads only the committed state, so it is fixed for the whole round.
        var types = new List<(string ItemId, long Capacity, List<Form> Forms)>();
        foreach (var (itemId, available) in remaining)
        {
            if (available.Count > 0 && Capacity(container, state, available) is var capacity and > 0)
            {
                types.Add((itemId, capacity, forms[itemId]));
            }
        }
        foreach (var space in spaces)
        {
            var (length, width, height) = (space.Dimensions.Length.Ticks, space.Dimensions.Width.Ticks,
                space.Dimensions.Height.Ticks);
            var spaceVolume = space.Dimensions.Volume;
            foreach (var (itemId, capacity, typeForms) in types)
            {
                foreach (var form in typeForms)
                {
                    var columns = Math.Min(length / form.Envelope.Length.Ticks, capacity);
                    var rows = width / form.Envelope.Width.Ticks;
                    if (columns == 0 || rows == 0)
                    {
                        continue;
                    }
                    if (nodes >= nodeLimit || deadline.Expired)
                    {
                        reached = true;
                        return leading;
                    }
                    var key = new LatticeSummaryKey(columns, rows, height / form.Envelope.Height.Ticks, capacity);
                    if (!lattices.TryGetValue(key, out var lattice))
                    {
                        lattice = Lattice(key);
                        if (lattices.Count < LatticeMemoLimit)
                        {
                            lattices[key] = lattice;
                        }
                    }
                    var budget = nodeLimit - nodes;
                    if (nodeCeiling is { } ceiling)
                    {
                        budget = Math.Min(budget, ceiling - stats.SearchNodesExpanded);
                    }
                    if (lattice.Nodes > budget)
                    {
                        stats.SearchNodesExpanded += budget;
                        nodes += budget;
                        reached = true;
                        return leading;
                    }
                    nodes += lattice.Nodes;
                    stats.SearchNodesExpanded += lattice.Nodes;
                    // The key's first field is -count or -used volume; a group that loses on it to
                    // the last kept block loses on the key, so its key is never built.
                    var full = leading.Count == leaders;
                    if (lattice.Count > 0
                        && (!full || (countFirst ? -lattice.Count : -lattice.Count * form.Volume) <= leading[^1].Lead))
                    {
                        var block = NewBlock(countFirst, space, spaceVolume, itemId, form, lattice);
                        if (!full || block.Score < leading[^1].Score)
                        {
                            Keep(leading, block, leaders);
                        }
                    }
                }
            }
        }
        return leading;
    }

    /// <summary>Inserts <paramref name="block"/> after every kept block that does not rank behind it, then drops the overflow.</summary>
    private static void Keep(List<Block> leading, Block block, int leaders)
    {
        var at = leading.Count;
        while (at > 0 && block.Score < leading[at - 1].Score)
        {
            at--;
        }
        leading.Insert(at, block);
        if (leading.Count > leaders)
        {
            leading.RemoveAt(leaders);
        }
    }

    /// <summary>
    /// The group's node count and its first largest count in (nx, ny) order with that count's
    /// (nx, ny, nz), all zero when no node has a layer. Nodes are nx in 1..columns and ny in
    /// 1..min(rows, capacity / nx), counted whether or not nz is positive. Within a group every key
    /// field but the lead is equal, and the lead improves strictly with the count in both modes,
    /// so this is the only node that can win. O(columns * rows) time, O(1) space.
    /// </summary>
    private static GroupLattice Lattice(LatticeSummaryKey key)
    {
        long nodes = 0, count = 0, bestNx = 0, bestNy = 0, bestNz = 0;
        for (long nx = 1; nx <= key.Columns; nx++)
        {
            var maximumNy = Math.Min(key.Rows, key.Capacity / nx);
            nodes += maximumNy;
            for (long ny = 1; ny <= maximumNy; ny++)
            {
                var nz = Math.Min(key.Layers, key.Capacity / (nx * ny));
                if (nz > 0 && nx * ny * nz > count)
                {
                    (count, bestNx, bestNy, bestNz) = (nx * ny * nz, nx, ny, nz);
                }
            }
        }
        return new GroupLattice(nodes, count, bestNx, bestNy, bestNz);
    }

    /// <summary>A group's winning block with the key the mode ranks it by, smaller first.</summary>
    private static Block NewBlock(bool countFirst, Space space, Int128 spaceVolume, string itemId, Form form,
        GroupLattice lattice)
    {
        var count = lattice.Count;
        var usedVolume = count * form.Volume;
        var spaceFillPpm = usedVolume * 1_000_000 / spaceVolume;
        object[] lead = countFirst ? [-count, -spaceFillPpm, -usedVolume] : [-usedVolume, -spaceFillPpm, -count];
        var score = SortKey.Of([.. lead, space.Origin.Z, space.Origin.Y, space.Origin.X, itemId,
            form.Rotation.Value(), lattice.Nx, lattice.Ny, lattice.Nz]);
        return new Block(space, itemId, form.Rotation, form.Physical, form.Envelope, lattice.Nx, lattice.Ny, lattice.Nz,
            count, score) { Lead = countFirst ? -count : -usedVolume };
    }

    private static void FillBlock(ContainerState state, Block block, List<ItemInstance> chosen, long clearance,
        SearchStats stats)
    {
        var index = 0;
        for (long z = 0; z < block.Nz; z++)
        {
            for (long y = 0; y < block.Ny; y++)
            {
                for (long x = 0; x < block.Nx; x++)
                {
                    var point = new Point(block.Space.Origin.X + x * block.Envelope.Length.Ticks,
                        block.Space.Origin.Y + y * block.Envelope.Width.Ticks,
                        block.Space.Origin.Z + z * block.Envelope.Height.Ticks);
                    var position = new Point(point.X + clearance, point.Y + clearance, point.Z + clearance);
                    var instance = chosen[index++];
                    // Above its bottom layer a member rests on an identical footprint; the bottom
                    // layer rests on whatever is there.
                    var support = z == 0 ? MemberSupport(state, instance, point, block.Envelope) : 1.0;
                    state.AddDirect(new Placement(instance, position, block.Rotation, block.Physical, point,
                        block.Envelope, support));
                    stats.CandidatesEvaluated++;
                    stats.PlacementsAttempted++;
                }
            }
        }
    }

    /// <summary>The share of a member's base that rests on the floor or on a box beneath it.</summary>
    private static double MemberSupport(ContainerState state, ItemInstance instance, Point point, Dimensions envelope)
    {
        if (point.Z == 0)
        {
            return 1.0;
        }
        var support = DirectSupportView.For(state.Placements, instance, new AxisAlignedBox(point, envelope));
        return DecimalRendering.TrueDivide(support.SupportingArea, envelope.BaseArea);
    }
}
