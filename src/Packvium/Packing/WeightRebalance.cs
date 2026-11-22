using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Algorithms;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Objectives;
using Packvium.Units;
using Packvium.Validation;

namespace Packvium.Packing;

/// <summary>One item relocated from one already-packed container to another.</summary>
public sealed record WeightMove(string ItemId, string FromContainerId, string ToContainerId);

/// <summary>The containers after a rebalance, and the moves that produced them, in order.</summary>
public sealed record RebalanceResult(ImmutableArray<PackedContainer> Containers, ImmutableArray<WeightMove> Moves)
{
    public bool Improved => !Moves.IsEmpty;
}

/// <summary>
/// Redistributes payload weight across an already-solved multi-container packing
/// (<c>rebalance.py</c>): an explicit, opt-in pass a caller runs after
/// <see cref="Packer.Pack(IEnumerable{Item}, IEnumerable{Container})"/>, never part of a solve.
///
/// Every candidate move is simulated in full and checked by the
/// <see cref="IndependentSolutionValidator"/> before it is committed, so an item is never lost,
/// duplicated, or left without the support it needs. Greedy and best-effort: each round moves at
/// most one item, from the heaviest container, trying its placements heaviest first against the
/// other containers lightest first, and commits the first move that fits and strictly narrows the
/// gap between the heaviest and lightest payload. The spread is a strictly decreasing sequence of
/// non-negative integers, so the pass ends on its own, and <c>maxMoves</c> bounds it again. A
/// round costs O(p c (F + V)) for p placements in the source, c containers, F one bounded
/// candidate search and V one validator pass over the whole packing.
/// </summary>
public static class WeightRebalance
{
    public static RebalanceResult Rebalance(PackingRequest request, IReadOnlyList<PackedContainer> containers,
        IReadOnlyList<UnpackedItem>? unpacked = null, PackingConfig? config = null, int maxMoves = 64,
        long timeLimitMs = 1000) =>
        Rebalance(request, containers, unpacked, config, maxMoves, new Deadline(timeLimitMs));

    /// <summary>The pass with its deadline supplied, so a test can stop it mid-search.</summary>
    internal static RebalanceResult Rebalance(PackingRequest request, IReadOnlyList<PackedContainer> containers,
        IReadOnlyList<UnpackedItem>? unpacked, PackingConfig? config, int maxMoves, Deadline deadline)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(containers);
        config ??= new PackingConfig();
        unpacked ??= [];
        // The admission Packer applies, and the refusal it gives a packing with no published price.
        Packer.RefuseUnpriceableObjective(request, config);
        Packer.RefuseUnpriceable(containers, config);
        var working = containers.ToList();
        var moves = new List<WeightMove>();
        for (var round = 0; round < maxMoves; round++)
        {
            if (working.Count < 2 || deadline.Expired)
            {
                break;
            }
            var committed = Round(request, working, unpacked, config, deadline);
            if (committed is null)
            {
                break;
            }
            moves.Add(committed.Value.Move);
            working = committed.Value.Containers;
        }
        return new RebalanceResult([.. working], [.. moves]);
    }

    /// <summary>The first move of one round that fits and strictly narrows the spread, or null.</summary>
    private static (List<PackedContainer> Containers, WeightMove Move)? Round(PackingRequest request,
        List<PackedContainer> working, IReadOnlyList<UnpackedItem> unpacked, PackingConfig config, Deadline deadline)
    {
        var weights = working.Select(packed => packed.PayloadWeight.Ticks).ToArray();
        var spread = weights.Max() - weights.Min();
        if (spread <= 0)
        {
            return null;
        }
        var sourceIndex = Array.IndexOf(weights, weights.Max());
        var source = working[sourceIndex];
        var ranked = Enumerable.Range(0, source.Placements.Length)
            .OrderBy(index => -source.Placements[index].Instance.Weight.Ticks);
        var destinations = Enumerable.Range(0, working.Count).Where(index => index != sourceIndex)
            .OrderBy(index => weights[index]).ToList();
        foreach (var placementIndex in ranked)
        {
            var moving = source.Placements[placementIndex];
            var weight = moving.Instance.Weight.Ticks;
            if (moving.Fixed || weight <= 0)
            {
                continue;
            }
            foreach (var destinationIndex in destinations)
            {
                if (!Narrows(weights, sourceIndex, destinationIndex, weight, spread))
                {
                    continue;
                }
                var trial = TryMove(request, working, unpacked, sourceIndex, destinationIndex, placementIndex, config,
                    deadline);
                // A move that prices the destination past its tariff is not an improvement: the
                // sentinel must never ride out through a rebalanced packing.
                if (trial is null || ObjectiveScorers.UnpriceableContainer(trial, config) is not null)
                {
                    continue;
                }
                return (trial, new WeightMove(moving.Instance.Id, source.Id, working[destinationIndex].Id));
            }
        }
        return null;
    }

    private static bool Narrows(long[] weights, int sourceIndex, int destinationIndex, long weight, long spread)
    {
        var projected = (long[])weights.Clone();
        projected[sourceIndex] -= weight;
        projected[destinationIndex] += weight;
        return projected.Max() - projected.Min() < spread;
    }

    /// <summary>
    /// The container list with one placement relocated, if the item finds a real spot in the
    /// destination and the whole resulting packing passes the validator; otherwise null and the
    /// caller's list is untouched. Both containers are replaced together, so there is no state in
    /// which the item belongs to neither. A deadline reached mid-search declines the move.
    /// </summary>
    private static List<PackedContainer>? TryMove(PackingRequest request, List<PackedContainer> containers,
        IReadOnlyList<UnpackedItem> unpacked, int sourceIndex, int destinationIndex, int placementIndex,
        PackingConfig config, Deadline deadline)
    {
        var source = containers[sourceIndex];
        var destination = containers[destinationIndex];
        var moving = source.Placements[placementIndex];
        var state = new ContainerState(destination.Container, destination.Sequence);
        foreach (var placement in destination.Placements)
        {
            state.Add(placement);
        }
        List<Candidate> candidates;
        try
        {
            candidates = CandidateFinder.Find(state, moving.Instance, config, BeamPack.DefaultConstraints(config),
                new SearchStats(), deadline, maxCandidates: 1);
        }
        catch (TimeLimitReachedException)
        {
            return null;
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        var candidate = candidates[0];
        var moved = new Placement(moving.Instance, candidate.Position, candidate.Rotation, candidate.Dimensions,
            candidate.Point, candidate.EnvelopeDimensions, SupportRatio(destination.Placements, moving.Instance,
                candidate.Point, candidate.EnvelopeDimensions));
        var trial = containers.ToList();
        trial[sourceIndex] = With(source, source.Placements.RemoveAt(placementIndex));
        trial[destinationIndex] = With(destination, destination.Placements.Add(moved));
        var report = IndependentSolutionValidator.Validate(request, trial, config.MinimumSupportRatio, config.Clearance,
            unpacked);
        return report.Valid ? trial : null;
    }

    /// <summary>
    /// Moving an item changes what rests on what, so every placement's reported top load is
    /// re-derived from the new stack rather than carried forward stale.
    /// </summary>
    private static PackedContainer With(PackedContainer packed, ImmutableArray<Placement> placements) =>
        new(packed.Container, packed.Sequence, Loads.WithTopLoads(placements), packed.LatticeSummary,
            packed.LatticeItems);

    /// <summary>The share of the base resting on the floor or on what is already in the container.</summary>
    private static double SupportRatio(ImmutableArray<Placement> existing, ItemInstance instance, Point origin,
        Dimensions envelope)
    {
        if (origin.Z == 0)
        {
            return 1.0;
        }
        var support = DirectSupportView.For(existing, instance, new AxisAlignedBox(origin, envelope));
        return DecimalRendering.TrueDivide(support.SupportingArea, envelope.BaseArea);
    }
}
