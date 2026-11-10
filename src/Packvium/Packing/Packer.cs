using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Algorithms;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Objectives;
using Packvium.Validation;

namespace Packvium.Packing;

/// <summary>Optional Strategies assembled for one packer; nothing here is global mutable state.</summary>
public sealed record ExtensionRegistry(
    ImmutableArray<IPlacementConstraint> PlacementConstraints = default,
    ImmutableArray<IItemOrderStrategy> ItemOrderStrategies = default,
    ImmutableArray<ISingleContainerSolver> Solvers = default,
    IContainerSelector? ContainerSelector = null)
{
    public ImmutableArray<IPlacementConstraint> PlacementConstraints { get; init; } =
        PlacementConstraints.IsDefault ? [] : PlacementConstraints;

    public ImmutableArray<IItemOrderStrategy> ItemOrderStrategies { get; init; } =
        ItemOrderStrategies.IsDefault ? [] : ItemOrderStrategies;

    public ImmutableArray<ISingleContainerSolver> Solvers { get; init; } = Solvers.IsDefault ? [] : Solvers;
}

/// <summary>
/// Runs the portfolio, scores and independently validates every answer, and returns the best
/// valid one with its runners-up. Reusable: all per-call state lives inside <see cref="Pack"/>.
/// </summary>
public sealed class Packer(PackingConfig? config = null, ExtensionRegistry? extensions = null,
    ISolutionScorer? solutionScorer = null, Func<long>? clock = null)
{
    private readonly PackingConfig config = config ?? new PackingConfig();
    private readonly ExtensionRegistry extensions = extensions ?? new ExtensionRegistry();

    public PackingResult Pack(IEnumerable<Item> items, IEnumerable<Container> containers)
    {
        var request = new PackingRequest(items, containers);
        RefuseUnpriceableObjective(request);
        var deadline = new Deadline(config.TimeLimitMs, clock: clock);
        var orchestrator = new SolverOrchestrator(extensions.PlacementConstraints, extensions.ItemOrderStrategies,
            extensions.Solvers, extensions.ContainerSelector);
        var portfolio = orchestrator.Solve(request.Instances, request.Containers, config, deadline);
        var scorer = solutionScorer ?? ObjectiveScorers.Resolve(config.Objective, config);
        var ranked = portfolio.Solutions.Select(raw => Ranked(raw, portfolio, request, scorer, deadline)).ToList();
        if (ranked.Count == 0)
        {
            return new PackingResult(PackingStatus.Infeasible, [], [],
                new AlgorithmReport(config.Profile.Value(), "none", deadline.ElapsedMs, config.Seed), [0],
                objective: config.Objective);
        }
        ranked = [.. ranked.OrderBy(result => SortKey.Of(result.Status == PackingStatus.InvalidResult,
            SortKey.Of([.. result.Score.Select(key => (object)key)]), result.Algorithm.Solver))];
        var valid = ranked.Where(result => result.Status != PackingStatus.InvalidResult).ToList();
        var selected = valid.Count > 0 ? valid : ranked;
        var best = selected[0];
        if (ObjectiveScorers.UnpriceableContainer(best.Containers, config) is { } unpriceable)
        {
            throw new UnratedWeightException(
                $"container '{unpriceable.ContainerId}' bills at {unpriceable.Grams} g, above its rate table's "
                + $"last bracket ({unpriceable.Bound} g); the shipment has no published price");
        }
        var alternatives = selected.Skip(1)
            .Where(runner => ObjectiveScorers.UnpriceableContainer(runner.Containers, config) is null)
            .Take((int)Math.Max(0, Math.Min(config.TopK - 1, int.MaxValue)));
        return new PackingResult(best.Status, best.Containers, best.Unpacked, best.Algorithm, best.Score, best.Warnings,
            alternatives, best.Feasibility, best.Termination, best.Optimality, best.Objective);
    }

    /// <summary>Both weight objectives need the divisor, and landed cost a tariff on every container, before any search.</summary>
    private void RefuseUnpriceableObjective(PackingRequest request)
    {
        if (config.Objective is "shipping_cost" or "lowest_landed_cost" && config.DimensionalWeightDivisor is null)
        {
            throw new UnknownObjectiveException(
                $"the {config.Objective} objective requires configuration.dimensional_weight_divisor");
        }
        if (config.Objective == "lowest_landed_cost"
            && request.Containers.FirstOrDefault(container => container.RateTable is null) is { } unrated)
        {
            throw new UnknownObjectiveException(
                $"the lowest_landed_cost objective requires a rate_table on every container; '{unrated.Id}' has none");
        }
    }

    private PackingResult Ranked(RawSolution raw, PortfolioRun portfolio, PackingRequest request, ISolutionScorer scorer,
        Deadline deadline)
    {
        var score = scorer.ScoreContainers(raw.Containers, raw.Unpacked);
        var status = raw.Unpacked.IsEmpty
            ? raw.Exhaustive ? PackingStatus.Optimal : PackingStatus.Feasible
            : raw.TimeLimitReached ? PackingStatus.TimeLimit : PackingStatus.BestFound;
        ImmutableArray<string> warnings = [];
        if (config.ValidateResult)
        {
            // A lattice container is expanded for the check only; the returned result stays compact.
            var expanded = raw.Containers.Select(packed => packed.LatticeSummary is null
                ? packed
                : new PackedContainer(packed.Container, packed.Sequence, packed.ExpandPlacements())).ToList();
            var report = IndependentSolutionValidator.Validate(request, expanded, config.MinimumSupportRatio,
                config.Clearance, raw.Unpacked);
            if (!report.Valid)
            {
                status = PackingStatus.InvalidResult;
                warnings = [.. report.Issues.Select(issue => $"{issue.Code}: {issue.Detail}")];
            }
        }
        var winnerMarked = false;
        var starts = portfolio.Starts.Select(record =>
        {
            var selectedRecord = !winnerMarked && record.Id == raw.SolverName;
            winnerMarked = winnerMarked || selectedRecord;
            return record with { Selected = selectedRecord };
        }).ToList();
        var termination = ResultFacts.AggregateTermination(starts, status == PackingStatus.InvalidResult);
        if (raw.EffortLimitReached && !raw.TimeLimitReached)
        {
            termination = new ResultFact("effort_limit", termination.Attributes);
        }
        var timedOut = (bool)termination.Attribute("global_deadline_reached") || raw.TimeLimitReached;
        var stats = raw.Stats;
        var metrics = new SolverMetrics(stats.CandidatePointsConsidered, stats.PlacementsAttempted,
            stats.CandidatesEvaluated, stats.CollisionChecks, stats.SupportChecks, stats.SpacePartitions,
            stats.SearchNodesExpanded);
        return new PackingResult(status, raw.Containers, raw.Unpacked,
            new AlgorithmReport(config.Profile.Value(), raw.SolverName, deadline.ElapsedMs, config.Seed, timedOut,
                raw.EffortLimitReached, stats.CandidatesEvaluated, stats.PlacementsAttempted, metrics),
            score.Keys, warnings, termination: termination, objective: config.Objective);
    }
}
