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
        var state = new ContainerState(container, sequence);
        var spaces = new List<Space> { new(Point.Origin, container.InnerDimensions) };
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
        long nodes = 0;
        var reached = false;
        while (spaces.Count > 0 && remaining.Values.Any(list => list.Count > 0))
        {
            var best = NextBlock(state, spaces, remaining, forms, container, config, stats, deadline, countFirst,
                lattices, ref nodes, ref reached);
            if (best is null || reached)
            {
                break;
            }
            var chosen = remaining[best.ItemId].Take((int)best.Count).ToList();
            remaining[best.ItemId].RemoveRange(0, (int)best.Count);
            FillBlock(state, best, chosen, config.Clearance.Ticks, stats);
            spaces = MaximalSpaces.SubtractAll(spaces, [best.Box], stats);
        }
        var unpacked = remaining.Values.SelectMany(list => list).ToImmutableArray();
        return new SingleContainerSolution(state, unpacked, false, reached || deadline.Expired);
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
    /// One round's winning block. Each (space, type, rotation) group is summarised by
    /// <see cref="Lattice"/>, whose first largest count is the only node of the group that can win,
    /// and its nodes are charged at once. A counted limit falling inside a group is charged exactly
    /// the nodes the per-node loop would have expanded before stopping; a wall-clock expiry inside a
    /// group is seen at the next group instead, and any stop discards the whole round. O(S * T * R)
    /// groups per round, each O(X * Y) on its first appearance and O(1) from the memo.
    /// </summary>
    private static Block? NextBlock(ContainerState state, List<Space> spaces,
        SortedDictionary<string, List<ItemInstance>> remaining, Dictionary<string, List<Form>> forms, Container container,
        PackingConfig config, SearchStats stats, Deadline deadline, bool countFirst,
        Dictionary<LatticeSummaryKey, GroupLattice> lattices, ref long nodes, ref bool reached)
    {
        var nodeLimit = config.ContainerPlanNodeLimit;
        var nodeCeiling = deadline.SearchNodeCeiling(stats);
        Block? best = null;
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
                        return best;
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
                        return best;
                    }
                    nodes += lattice.Nodes;
                    stats.SearchNodesExpanded += lattice.Nodes;
                    // The key's first field is -count or -used volume; a group that loses on it
                    // loses on the key, so its key is never built.
                    if (lattice.Count > 0
                        && (best is null || (countFirst ? -lattice.Count : -lattice.Count * form.Volume) <= best.Lead))
                    {
                        best = BetterBlock(best, countFirst, space, spaceVolume, itemId, form, lattice);
                    }
                }
            }
        }
        return best;
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

    /// <summary><paramref name="best"/>, or this group's winning block when its key ranks strictly ahead.</summary>
    private static Block BetterBlock(Block? best, bool countFirst, Space space, Int128 spaceVolume, string itemId,
        Form form, GroupLattice lattice)
    {
        var count = lattice.Count;
        var usedVolume = count * form.Volume;
        var spaceFillPpm = usedVolume * 1_000_000 / spaceVolume;
        object[] lead = countFirst ? [-count, -spaceFillPpm, -usedVolume] : [-usedVolume, -spaceFillPpm, -count];
        var score = SortKey.Of([.. lead, space.Origin.Z, space.Origin.Y, space.Origin.X, itemId,
            form.Rotation.Value(), lattice.Nx, lattice.Ny, lattice.Nz]);
        if (best is not null && !(score < best.Score))
        {
            return best;
        }
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
