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
/// and a volume-first mode, and keeps the better. Only for plain boxes where every member is
/// fully supported and no per-placement rule can tell members apart; otherwise it falls back to
/// <see cref="ExtremePointSolver"/>. Enumeration is O(B * S * T * R * X * Y), bounded by the
/// deadline and <c>container_plan_node_limit</c>.
/// </summary>
public sealed class HomogeneousBlockSolver(IReadOnlyList<IPlacementConstraint>? constraints = null) : ISingleContainerSolver
{
    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];

    public string Name => "homogeneous_blocks";

    public bool OrderInsensitive => true;

    private sealed record Block(Space Space, string ItemId, Rotation Rotation, Dimensions Physical, Dimensions Envelope,
        long Nx, long Ny, long Nz, long Count, SortKey Score)
    {
        public AxisAlignedBox Box => new(Space.Origin, Dimensions.OfTicks(Nx * Envelope.Length.Ticks,
            Ny * Envelope.Width.Ticks, Nz * Envelope.Height.Ticks));
    }

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        if (!Supports(container, items))
        {
            return new ExtremePointSolver(constraints).PackOne(container, sequence, items, config, stats, deadline);
        }
        SingleContainerSolution? best = null;
        var reached = false;
        foreach (var countFirst in (bool[])[true, false])
        {
            if (deadline.Expired)
            {
                reached = true;
                break;
            }
            var solution = PackMode(container, sequence, items, config, stats, deadline, countFirst);
            reached = reached || solution.TimeLimitReached;
            if (best is null || SolutionKey(solution) < SolutionKey(best))
            {
                best = solution;
            }
        }
        return best is null
            ? new SingleContainerSolution(new ContainerState(container, sequence), [.. items], false, reached)
            : new SingleContainerSolution(best.State, best.Unpacked, false, reached);
    }

    private bool Supports(Container container, IReadOnlyList<ItemInstance> items)
    {
        if (constraints.Count > 0 || container.Obstacles.Length > 0 || container.Axles is not null
            || !container.TagLimits.IsEmpty || container.MaxStackDensity is not null || container.VoidFillReserveRatio > 0)
        {
            return false;
        }
        return items.All(instance =>
        {
            var item = instance.Item;
            return item.Group is null && item.Tags.Count == 0 && item.IncompatibleTags.Count == 0
                   && item.EligibleContainerTags.Count == 0 && item.Stackable && !item.MustBeOnFloor
                   && item.MaxTopLoad is null && item.MaxStackedItems is null && item.MinimumSupportRatio == 0
                   && item.GroundContactRule is null or "free" && item.NestingHeight is null && item.StopIndex is null;
        });
    }

    private static SortKey SolutionKey(SingleContainerSolution solution)
    {
        var state = solution.State;
        var signature = SortKey.Of([.. state.Placements.Select(p => (object)SortKey.Of(p.Instance.Id,
            p.EnvelopeOrigin.X, p.EnvelopeOrigin.Y, p.EnvelopeOrigin.Z))]);
        return SortKey.Of(solution.Unpacked.Length, -state.UsedVolumeTicks, state.MaxZ, signature);
    }

    private static SingleContainerSolution PackMode(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline, bool countFirst)
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
        long nodes = 0;
        var reached = false;
        while (spaces.Count > 0 && remaining.Values.Any(list => list.Count > 0))
        {
            var best = BestBlock(state, spaces, remaining, container, config, stats, deadline, countFirst, ref nodes,
                ref reached);
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

    private static Block? BestBlock(ContainerState state, List<Space> spaces,
        SortedDictionary<string, List<ItemInstance>> remaining, Container container, PackingConfig config,
        SearchStats stats, Deadline deadline, bool countFirst, ref long nodes, ref bool reached)
    {
        Block? best = null;
        foreach (var space in spaces)
        {
            var spaceVolume = space.Dimensions.Volume;
            foreach (var (itemId, available) in remaining)
            {
                if (available.Count == 0)
                {
                    continue;
                }
                var prototype = available[0];
                long capacity = available.Count;
                if (container.MaxItems is { } maxItems)
                {
                    capacity = Math.Min(capacity, maxItems - state.PlacementCount);
                }
                if (container.MaxPayload is { } payload && prototype.Weight.Ticks > 0)
                {
                    capacity = Math.Min(capacity, IntMath.FloorDiv(payload.Ticks - state.PayloadTicks, prototype.Weight.Ticks));
                }
                if (capacity <= 0)
                {
                    continue;
                }
                foreach (var (rotation, physical) in prototype.Dimensions.UniqueRotations(prototype.Item.AllowedRotations))
                {
                    var envelope = config.Clearance.Ticks != 0 ? physical.Expand(config.Clearance) : physical;
                    var maximumX = space.Dimensions.Length.Ticks / envelope.Length.Ticks;
                    var maximumY = space.Dimensions.Width.Ticks / envelope.Width.Ticks;
                    var maximumZ = space.Dimensions.Height.Ticks / envelope.Height.Ticks;
                    for (long nx = 1; nx <= Math.Min(maximumX, capacity) && !reached; nx++)
                    {
                        var maximumNy = Math.Min(maximumY, capacity / nx);
                        for (long ny = 1; ny <= maximumNy; ny++)
                        {
                            if (nodes >= config.ContainerPlanNodeLimit || deadline.Expired)
                            {
                                reached = true;
                                break;
                            }
                            nodes++;
                            stats.SearchNodesExpanded++;
                            var nz = Math.Min(maximumZ, capacity / (nx * ny));
                            if (nz <= 0)
                            {
                                continue;
                            }
                            var count = nx * ny * nz;
                            var usedVolume = count * physical.Volume;
                            var spaceFillPpm = usedVolume * 1_000_000 / spaceVolume;
                            object[] lead = countFirst
                                ? [-count, -spaceFillPpm, -usedVolume]
                                : [-usedVolume, -spaceFillPpm, -count];
                            var score = SortKey.Of([.. lead, space.Origin.Z, space.Origin.Y, space.Origin.X, itemId,
                                rotation.Value(), nx, ny, nz]);
                            if (best is null || score < best.Score)
                            {
                                best = new Block(space, itemId, rotation, physical, envelope, nx, ny, nz, count, score);
                            }
                        }
                    }
                    if (reached)
                    {
                        return best;
                    }
                }
            }
        }
        return best;
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
                    state.AddDirect(new Placement(chosen[index++], position, block.Rotation, block.Physical, point,
                        block.Envelope));
                    stats.CandidatesEvaluated++;
                    stats.PlacementsAttempted++;
                }
            }
        }
    }
}
