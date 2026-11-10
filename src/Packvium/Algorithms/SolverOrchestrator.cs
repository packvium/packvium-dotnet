using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Objectives;

namespace Packvium.Algorithms;

/// <summary>A solver name in <c>configuration.solvers</c> this library does not implement.</summary>
public sealed class UnknownSolverException(string message) : ArgumentException(message);

/// <summary>
/// The Portfolio / Chain of Responsibility: runs every (solver, item order) start under one
/// deadline and returns all of their answers for the packer to rank. Sequential; each start
/// gets an even slice of what remains, and a start that proves a single-type lattice complete
/// ends the portfolio early.
/// </summary>
public sealed class SolverOrchestrator(
    IReadOnlyList<IPlacementConstraint>? customConstraints = null,
    IReadOnlyList<IItemOrderStrategy>? customOrders = null,
    IReadOnlyList<ISingleContainerSolver>? customSolvers = null,
    IContainerSelector? containerSelector = null)
{
    private static readonly SortedDictionary<string, Func<IReadOnlyList<IPlacementConstraint>, ISingleContainerSolver>>
        SolverFactories = new(StringComparer.Ordinal)
        {
            ["grid"] = _ => new GridSolver(),
            ["extreme_points"] = constraints => new ExtremePointSolver(constraints),
            ["homogeneous_blocks"] = constraints => new HomogeneousBlockSolver(constraints),
            ["layer"] = constraints => new LayerSolver(constraints),
            ["maximal_spaces"] = constraints => new MaximalSpaceSolver(constraints),
            ["exact_small"] = constraints => new ExactSmallSolver(constraints),
        };

    private readonly IReadOnlyList<IPlacementConstraint> customConstraints = customConstraints ?? [];
    private readonly IReadOnlyList<IItemOrderStrategy> customOrders = customOrders ?? [];
    private readonly IReadOnlyList<ISingleContainerSolver> customSolvers = customSolvers ?? [];
    private readonly IContainerSelector containerSelector = containerSelector ?? new DefaultContainerSelector();

    private sealed record Start(ISingleContainerSolver Solver, string OrderName, ImmutableArray<ItemInstance> Order);

    public PortfolioRun Solve(ImmutableArray<ItemInstance> items, ImmutableArray<Container> containers,
        PackingConfig config, Deadline deadline)
    {
        var starts = Starts(items, config);
        if (config.EffortBudget?.MaxRestarts is { } restarts)
        {
            starts = [.. starts.Take((int)Math.Min(restarts, int.MaxValue))];
        }
        return SolveSequential(items, containers, config, deadline, starts, config.EffortBudget);
    }

    private List<ISingleContainerSolver> Solvers(IReadOnlyList<ItemInstance> items, PackingConfig config)
    {
        if (!config.Solvers.IsEmpty)
        {
            var unknown = config.Solvers.Where(name => !SolverFactories.ContainsKey(name)).ToList();
            if (unknown.Count > 0)
            {
                throw new UnknownSolverException(
                    $"unknown solver name(s) [{string.Join(", ", unknown.Select(name => $"'{name}'"))}]; expected one of "
                    + $"[{string.Join(", ", SolverFactories.Keys.Select(name => $"'{name}'"))}]");
            }
            return [.. customSolvers, .. config.Solvers.Select(name => SolverFactories[name](customConstraints))];
        }
        List<ISingleContainerSolver> solvers;
        var profile = config.Profile;
        if (GridSolver.Supports(items) && customConstraints.Count == 0)
        {
            solvers = profile == SolverProfile.Fast ? [new GridSolver()] : [new GridSolver(), new ExtremePointSolver()];
        }
        else if (profile == SolverProfile.Fast)
        {
            solvers = [new LayerSolver(customConstraints)];
        }
        else if (profile == SolverProfile.ExactSmall && items.Count <= config.ExactItemLimit)
        {
            solvers = [new ExactSmallSolver(customConstraints), new ExtremePointSolver(customConstraints)];
        }
        else if (profile == SolverProfile.Quality)
        {
            solvers =
            [
                new HomogeneousBlockSolver(customConstraints), new ExtremePointSolver(customConstraints),
                new MaximalSpaceSolver(customConstraints), new LayerSolver(customConstraints),
            ];
            if (items.Count <= config.ExactItemLimit)
            {
                solvers.Insert(0, new ExactSmallSolver(customConstraints));
            }
        }
        else
        {
            solvers = [new ExtremePointSolver(customConstraints), new LayerSolver(customConstraints)];
        }
        return [.. customSolvers, .. solvers];
    }

    private List<(string Name, ImmutableArray<ItemInstance> Order)> Orders(ImmutableArray<ItemInstance> items,
        PackingConfig config)
    {
        var strategies = new List<(string, Func<ItemInstance, object[]>)>();
        if (config.Profile == SolverProfile.Quality)
        {
            strategies.Add(("small_edge", i => [i.Dimensions.MaxEdge, i.Dimensions.Volume, i.Id]));
            strategies.Add(("small_volume", i => [i.Dimensions.Volume, i.Dimensions.MaxEdge, i.Id]));
        }
        strategies.Add(("volume", i => [-i.Dimensions.Volume, -i.Dimensions.MaxEdge, -i.Weight.Ticks, i.Id]));
        strategies.Add(("base_area", i => [-i.Dimensions.BaseArea, -i.Dimensions.Volume, i.Id]));
        strategies.Add(("weight", i => [-i.Weight.Ticks, -i.Dimensions.Volume, i.Id]));
        strategies.Add(("constrained", i => [i.Item.AllowedRotations.Length, -i.Item.MinimumSupportRatio,
            -i.Dimensions.Volume, i.Id]));
        var limit = config.Profile == SolverProfile.Fast ? 1 : config.MultiStartOrders;
        var output = new List<(string, ImmutableArray<ItemInstance>)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Offer(string name, ImmutableArray<ItemInstance> order)
        {
            if (seen.Add(Signature(order)))
            {
                output.Add((name, order));
            }
            return output.Count >= limit;
        }
        foreach (var (name, key) in strategies)
        {
            if (Offer(name, [.. items.OrderBy(i => Ordering.Key(i, config, key(i)))]))
            {
                return output;
            }
        }
        foreach (var strategy in customOrders)
        {
            if (Offer(strategy.Name, [.. strategy.Order(items, config.Seed)]))
            {
                return output;
            }
        }
        var random = new DeterministicRandom(config.Seed);
        var attempts = 0;
        while (output.Count < limit && attempts < Math.Max(32, limit * 16))
        {
            attempts++;
            ImmutableArray<ItemInstance> order = [.. random.Shuffled(items)];
            if (seen.Add(Signature(order)))
            {
                output.Add(($"seeded_{output.Count}", order));
            }
        }
        return output;
    }

    private static string Signature(IEnumerable<ItemInstance> order) =>
        string.Join("\u0000", order.Select(item => item.Id));

    private List<Start> Starts(ImmutableArray<ItemInstance> items, PackingConfig config)
    {
        var orders = Orders(items, config);
        var starts = new List<Start>();
        foreach (var solver in Solvers(items, config))
        {
            var chosen = solver.OrderInsensitive ? orders.Take(1) : orders;
            starts.AddRange(chosen.Select(order => new Start(solver, order.Name, order.Order)));
        }
        if (config.ContainerPlanBeamWidth <= 1 || config.Profile != SolverProfile.Quality)
        {
            return starts;
        }
        var greedy = starts.Select(start => start.Solver.Name == "homogeneous_blocks"
            ? start
            : start with { OrderName = $"{start.OrderName}:greedy" }).ToList();
        var extreme = starts.FirstOrDefault(start => start.Solver.Name == "extreme_points");
        if (extreme is null)
        {
            return greedy;
        }
        var neighborhoods = items.Select(item => item.Item.Id).Distinct().Order(Units.CodePointComparer.Instance)
            .Select(itemType => extreme with { OrderName = $"neighborhood:{itemType}" });
        return [.. greedy, .. neighborhoods, extreme with { OrderName = "best:beam" }];
    }

    private PortfolioRun SolveSequential(ImmutableArray<ItemInstance> items, ImmutableArray<Container> containers,
        PackingConfig config, Deadline deadline, List<Start> starts, EffortBudget? effortBudget)
    {
        var results = new List<RawSolution>();
        var completed = new Dictionary<string, Start>(StringComparer.Ordinal);
        var records = starts.Select(start => new StartRecord($"{start.Solver.Name}:{start.OrderName}", false, false, false))
            .ToList();
        for (var position = 0; position < starts.Count; position++)
        {
            if (deadline.Expired)
            {
                break;
            }
            var start = starts[position];
            if (start.OrderName.StartsWith("neighborhood:", StringComparison.Ordinal)
                || start.OrderName.EndsWith(":beam", StringComparison.Ordinal))
            {
                if (Refined(start, results, completed, config) is not { } refined)
                {
                    continue;
                }
                start = refined;
            }
            var stats = new SearchStats();
            var budget = deadline.Slice(starts.Count - position);
            if (effortBudget is not null)
            {
                budget = budget.WithEffort(effortBudget, stats);
            }
            var startConfig = start.OrderName.EndsWith(":greedy", StringComparison.Ordinal)
                              || start.OrderName.Contains(":late_", StringComparison.Ordinal)
                ? config.WithSearchWidths(1, 1, 1)
                : config;
            var outcome = AcrossContainers(start.Solver, start.Order, containers, startConfig, stats, budget);
            var effortReached = budget.EffortExceeded;
            var wallReached = outcome.Reached && budget.RemainingNs <= 0;
            var unpacked = effortReached
                ? [.. outcome.Unpacked.Select(item => item.Reason == "time_limit"
                    ? new UnpackedItem(item.Instance, "effort_limit", item.Details)
                    : item)]
                : outcome.Unpacked;
            var startId = $"{start.Solver.Name}:{start.OrderName}";
            results.Add(new RawSolution(startId, outcome.Packed, unpacked, stats, wallReached, effortReached,
                outcome.Exhaustive));
            completed[startId] = start;
            records[position] = new StartRecord(startId, true, !outcome.Reached, outcome.Reached);
            // A single type's lattice is the densest possible packing once it holds everything,
            // but only when every container actually went through the lattice arithmetic.
            if (outcome.DominantLattice && unpacked.IsEmpty)
            {
                break;
            }
        }
        return FinishPortfolio(items, containers, deadline, results, records);
    }

    /// <summary>
    /// A refinement start re-runs the best eligible extreme-point answer so far: a neighborhood
    /// start moves one item type to the end; the beam start re-runs it at full width.
    /// </summary>
    private static Start? Refined(Start start, List<RawSolution> results, Dictionary<string, Start> completed,
        PackingConfig config)
    {
        var neighborhood = start.OrderName.StartsWith("neighborhood:", StringComparison.Ordinal);
        var scorer = ObjectiveScorers.Resolve(config.Objective, config);
        var best = results
            .Where(result => completed[result.SolverName].Solver is ExtremePointSolver
                             && (!neighborhood || result.SolverName.EndsWith(":greedy", StringComparison.Ordinal)))
            .Select(result => (Result: result, Key: SortKey.Of(Key(scorer.ScoreContainers(result.Containers, result.Unpacked)),
                result.SolverName)))
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Result)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }
        var source = completed[best.SolverName];
        if (!neighborhood)
        {
            return source with { OrderName = $"{source.OrderName}:beam" };
        }
        var itemType = start.OrderName["neighborhood:".Length..];
        ImmutableArray<ItemInstance> order =
            [.. source.Order.Where(item => item.Item.Id != itemType), .. source.Order.Where(item => item.Item.Id == itemType)];
        return new Start(source.Solver, $"{source.OrderName}:late_{itemType}", order);
    }

    private static SortKey Key(ScoreVector score) => SortKey.Of([.. score.Keys.Select(key => (object)key)]);

    private PortfolioRun FinishPortfolio(ImmutableArray<ItemInstance> items, ImmutableArray<Container> containers,
        Deadline deadline, List<RawSolution> results, List<StartRecord> records)
    {
        var globalDeadline = deadline.Expired;
        var finished = records.Select(record => record with { GlobalDeadlineReached = globalDeadline }).ToList();
        if (results.Count == 0)
        {
            const string fallbackId = "portfolio:fallback";
            results.Add(new RawSolution(fallbackId, [],
                [.. items.Select(item => UnpackedFor(item, containers, true))], new SearchStats(), true));
            finished.Add(new StartRecord(fallbackId, true, true, false, GlobalDeadlineReached: globalDeadline));
        }
        return new PortfolioRun([.. results], [.. finished]);
    }

    private UnpackedItem UnpackedFor(ItemInstance instance, IReadOnlyList<Container> containers, bool reached)
    {
        var (reason, details) = UnpackedReason(instance, containers, reached);
        return new UnpackedItem(instance, reason, details);
    }

    /// <summary>
    /// The strongest reason that can be proven for an unplaced item: geometry and payload
    /// first, then a registered constraint's own proof, then the search-dependent reasons.
    /// </summary>
    private (string Reason, ImmutableArray<string> Details) UnpackedReason(ItemInstance instance,
        IReadOnlyList<Container> containers, bool reached)
    {
        bool FitsWith(IEnumerable<Rotation> rotations) => containers.Any(container =>
            instance.Dimensions.UniqueRotations(rotations).Any(pair => pair.Dimensions.FitsInside(container.InnerDimensions)));
        var proof = customConstraints.OfType<IProvenRejection>()
            .Select(constraint => constraint.ProvesUnplaceable(instance, containers))
            .FirstOrDefault(found => found is not null);
        if (!FitsWith(Rotations.All))
        {
            return ("no_compatible_container_dimensions", []);
        }
        if (!FitsWith(instance.Item.AllowedRotations))
        {
            return ("rotation_restricted", []);
        }
        if (!containers.Any(container => container.MaxPayload is not { } payload || instance.Weight.Ticks <= payload.Ticks))
        {
            return ("payload_exceeded", []);
        }
        if (instance.Item.EligibleContainerTags.Count > 0
            && !containers.Any(container => instance.Item.EligibleContainerTags.Overlaps(container.Tags)))
        {
            return ("no_eligible_container", []);
        }
        if (proof is { } proven)
        {
            return (proven.Reason, [proven.Detail]);
        }
        if (reached)
        {
            return ("time_limit", []);
        }
        return string.IsNullOrEmpty(instance.Item.Group) ? ("no_feasible_placement", []) : ("group_cannot_fit_together", []);
    }

    /// <summary>
    /// Whether the item would fit in an already-packed container's final layout with the support
    /// rule alone set aside -- an observed fact about that layout, under its own small budget.
    /// </summary>
    private bool SupportIsTheBlocker(ItemInstance instance, IReadOnlyList<PackedContainer> packed, PackingConfig config)
    {
        var constraints = BeamPack.DefaultConstraints(config, customConstraints);
        var support = constraints.OfType<SupportConstraint>().ToList();
        if (support.Count == 0 || support.All(constraint => !constraint.AppliesTo(instance)))
        {
            return false;
        }
        var withoutSupport = constraints.Where(constraint => constraint is not SupportConstraint).ToList();
        var budget = new Deadline(2_000);
        foreach (var packedContainer in packed)
        {
            if (budget.Expired)
            {
                return false;
            }
            var state = new ContainerState(packedContainer.Container, packedContainer.Sequence);
            foreach (var placement in packedContainer.Placements)
            {
                state.Add(placement);
            }
            if (CandidateFinder.Find(state, instance, config, withoutSupport, new SearchStats(), budget, 1).Count == 0)
            {
                continue;
            }
            if (CandidateFinder.Find(state, instance, config, constraints, new SearchStats(), budget, 1).Count == 0)
            {
                return true;
            }
        }
        return false;
    }

    private sealed record Outcome(ImmutableArray<PackedContainer> Packed, ImmutableArray<UnpackedItem> Unpacked,
        bool Exhaustive, bool Reached, bool DominantLattice);

    private Outcome AcrossContainers(ISingleContainerSolver solver, ImmutableArray<ItemInstance> items,
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline) =>
        config.ContainerPlanBeamWidth > 1 && containerSelector is DefaultContainerSelector
            ? AcrossContainersBeam(solver, items, containers, config, stats, deadline)
            : AcrossContainersGreedy(solver, items, containers, config, stats, deadline);

    private static (PackedContainer Packed, HashSet<string> Ids) PackedState(ContainerState state)
    {
        if (state.LatticeSummary is { } summary)
        {
            return (new PackedContainer(state.Container, state.Sequence, [], summary, state.LatticeItems),
                state.LatticeItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
        }
        return (new PackedContainer(state.Container, state.Sequence, Loads.WithTopLoads(state.Placements)),
            state.Placements.Select(placement => placement.Instance.Id).ToHashSet(StringComparer.Ordinal));
    }

    private static long MaximumContainers(IReadOnlyList<ItemInstance> items, IReadOnlyList<Container> containers,
        PackingConfig config) =>
        config.MaxContainers ?? containers.Sum(container => (long)(container.Quantity ?? items.Count));

    private static List<Container> CostOrder(IReadOnlyList<Container> containers) =>
        [.. containers.OrderBy(c => SortKey.Of(c.CostMinor, c.InnerDimensions.Volume, c.Id))];

    private Outcome AcrossContainersGreedy(ISingleContainerSolver solver, ImmutableArray<ItemInstance> items,
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline)
    {
        var remaining = items.ToList();
        var packed = new List<PackedContainer>();
        var (reached, exhaustive, dominantLattice) = (false, true, true);
        var inventory = containers.ToDictionary(c => c.Id, c => c.Quantity, StringComparer.Ordinal);
        var sequences = containers.ToDictionary(c => c.Id, _ => 0, StringComparer.Ordinal);
        var maximum = MaximumContainers(items, containers, config);
        var ordered = CostOrder(containers);
        while (remaining.Count > 0 && packed.Count < maximum)
        {
            if (deadline.Expired)
            {
                reached = true;
                break;
            }
            ImmutableArray<ItemInstance> roundItems = [.. remaining];
            (SortKey Score, SingleContainerSolution Solution)? best = null;
            foreach (var container in ordered)
            {
                if (inventory[container.Id] is <= 0)
                {
                    continue;
                }
                SingleContainerSolution one;
                try
                {
                    one = solver.PackOne(container, sequences[container.Id] + 1, roundItems, config, stats, deadline);
                }
                catch (TimeLimitReachedException)
                {
                    reached = true;
                    break;
                }
                reached = reached || one.TimeLimitReached;
                if (one.State.PlacementCount == 0)
                {
                    if (reached)
                    {
                        break;
                    }
                    continue;
                }
                var score = RoundScore(container, one, roundItems.Length, config);
                if (best is null || score < best.Value.Score)
                {
                    best = (score, one);
                }
                if (reached)
                {
                    break;
                }
            }
            if (best is not { } winner)
            {
                break;
            }
            var chosen = winner.Solution.State.Container;
            sequences[chosen.Id]++;
            exhaustive = exhaustive && winner.Solution.Exhaustive;
            dominantLattice = dominantLattice && winner.Solution.DominantLattice;
            if (inventory[chosen.Id] is { } left)
            {
                inventory[chosen.Id] = left - 1;
            }
            var (committed, ids) = PackedState(winner.Solution.State);
            packed.Add(committed);
            remaining = [.. remaining.Where(item => !ids.Contains(item.Id))];
            if (reached)
            {
                break;
            }
        }
        var unpacked = Diagnose(remaining, containers, packed, config, reached);
        var proven = exhaustive && !reached && unpacked.IsEmpty && packed.Count == 1;
        return new Outcome([.. packed], unpacked, proven, reached, dominantLattice);
    }

    /// <summary>How one container's trial ranks in its round: weight-priced objectives rank by what the carrier bills.</summary>
    private SortKey RoundScore(Container container, SingleContainerSolution one, int roundCount, PackingConfig config)
    {
        if (config.Objective is not ("shipping_cost" or "lowest_landed_cost") || config.DimensionalWeightDivisor is null)
        {
            return containerSelector.Score(container, one);
        }
        var billed = Billing.BilledTicks(container, container.TareWeight.Ticks + one.State.PayloadTicks, config);
        var placed = one.State.PlacementCount;
        if (config.Objective == "shipping_cost")
        {
            return SortKey.Of(-placed, billed, container.Id);
        }
        var charge = container.RateTable?.ChargeMinorOrNull(Billing.Grams(billed));
        var containersNeeded = Units.IntMath.CeilDiv(roundCount, Math.Max(placed, 1));
        return SortKey.Of(charge ?? Billing.UnpriceableMinor, containersNeeded, -placed, container.Id);
    }

    private ImmutableArray<UnpackedItem> Diagnose(IReadOnlyList<ItemInstance> remaining, IReadOnlyList<Container> containers,
        IReadOnlyList<PackedContainer> packed, PackingConfig config, bool reached)
    {
        var unpacked = ImmutableArray.CreateBuilder<UnpackedItem>(remaining.Count);
        foreach (var instance in remaining)
        {
            var (reason, details) = UnpackedReason(instance, containers, reached);
            if (reason == "no_feasible_placement" && SupportIsTheBlocker(instance, packed, config))
            {
                reason = "insufficient_support";
            }
            unpacked.Add(new UnpackedItem(instance, reason, details));
        }
        return unpacked.MoveToImmutable();
    }

    private sealed record Plan(
        ImmutableArray<PackedContainer> Packed,
        ImmutableArray<ItemInstance> Remaining,
        ImmutableDictionary<string, int?> Inventory,
        ImmutableDictionary<string, int> Sequences,
        bool Exhaustive = true,
        bool DominantLattice = true);

    /// <summary>
    /// A bounded deterministic beam over partial multi-container plans. Plans with the same
    /// remaining instances and inventory have identical continuations, so only the best closed
    /// prefix per signature survives. Each layer is O(w * T * P) for w plans, T templates and P
    /// one single-container solve.
    /// </summary>
    private Outcome AcrossContainersBeam(ISingleContainerSolver solver, ImmutableArray<ItemInstance> items,
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline)
    {
        var ordered = CostOrder(containers);
        var maximum = MaximumContainers(items, containers, config);
        var initial = new Plan([], items,
            containers.ToImmutableDictionary(c => c.Id, c => c.Quantity, StringComparer.Ordinal),
            containers.ToImmutableDictionary(c => c.Id, _ => 0, StringComparer.Ordinal));
        List<Plan> beam = [initial];
        var incumbent = initial;
        long planNodes = 0;
        var reached = false;
        while (beam.Count > 0 && planNodes < config.ContainerPlanNodeLimit)
        {
            var expansions = new List<Plan>();
            foreach (var plan in beam)
            {
                if (plan.Remaining.IsEmpty || plan.Packed.Length >= maximum)
                {
                    if (ScorePlan(plan.Packed, plan.Remaining, config) < ScorePlan(incumbent.Packed, incumbent.Remaining, config))
                    {
                        incumbent = plan;
                    }
                    continue;
                }
                foreach (var container in ordered)
                {
                    if (planNodes >= config.ContainerPlanNodeLimit)
                    {
                        break;
                    }
                    if (deadline.Expired)
                    {
                        reached = true;
                        break;
                    }
                    if (plan.Inventory[container.Id] is <= 0)
                    {
                        continue;
                    }
                    planNodes++;
                    SingleContainerSolution one;
                    try
                    {
                        one = solver.PackOne(container, plan.Sequences[container.Id] + 1, plan.Remaining, config, stats,
                            deadline);
                    }
                    catch (TimeLimitReachedException)
                    {
                        reached = true;
                        break;
                    }
                    reached = reached || one.TimeLimitReached;
                    if (one.State.PlacementCount == 0)
                    {
                        continue;
                    }
                    var (packedContainer, ids) = PackedState(one.State);
                    var child = new Plan(
                        plan.Packed.Add(packedContainer),
                        [.. plan.Remaining.Where(item => !ids.Contains(item.Id))],
                        plan.Inventory[container.Id] is { } count ? plan.Inventory.SetItem(container.Id, count - 1) : plan.Inventory,
                        plan.Sequences.SetItem(container.Id, plan.Sequences[container.Id] + 1),
                        plan.Exhaustive && one.Exhaustive,
                        plan.DominantLattice && one.DominantLattice);
                    expansions.Add(child);
                    if (ScorePlan(child.Packed, child.Remaining, config) < ScorePlan(incumbent.Packed, incumbent.Remaining, config))
                    {
                        incumbent = child;
                    }
                    if (reached)
                    {
                        break;
                    }
                }
                if (reached)
                {
                    break;
                }
            }
            if (reached || expansions.Count == 0)
            {
                break;
            }
            beam = Dominant(expansions, config)
                .OrderBy(plan => PlanBoundKey(plan, containers, config))
                .Take((int)Math.Min(config.ContainerPlanBeamWidth, int.MaxValue))
                .ToList();
        }
        var unpacked = Diagnose(incumbent.Remaining, containers, incumbent.Packed, config, reached);
        var proven = incumbent.Exhaustive && !reached && unpacked.IsEmpty && incumbent.Packed.Length == 1;
        return new Outcome(incumbent.Packed, unpacked, proven, reached, incumbent.DominantLattice);
    }

    /// <summary>Keeps, per (remaining, inventory) signature, the plan with the best closed prefix, in first-seen order.</summary>
    private static List<Plan> Dominant(List<Plan> expansions, PackingConfig config)
    {
        var order = new List<string>();
        var dominant = new Dictionary<string, Plan>(StringComparer.Ordinal);
        foreach (var plan in expansions)
        {
            var signature = Signature(plan.Remaining) + "\u0001" + string.Join("\u0000",
                plan.Inventory.OrderBy(entry => entry.Key, Units.CodePointComparer.Instance)
                    .Select(entry => $"{entry.Key}\u0002{entry.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "~"}"));
            if (!dominant.TryGetValue(signature, out var previous))
            {
                order.Add(signature);
                dominant[signature] = plan;
            }
            else if (ScorePlan(plan.Packed, [], config) < ScorePlan(previous.Packed, [], config))
            {
                dominant[signature] = plan;
            }
        }
        return [.. order.Select(signature => dominant[signature])];
    }

    private static ScoreVector ScorePlan(IReadOnlyList<PackedContainer> packed, IReadOnlyList<ItemInstance> remaining,
        PackingConfig config) =>
        ObjectiveScorers.Resolve(config.Objective, config).ScoreContainers(packed,
            [.. remaining.Select(item => new UnpackedItem(item, "search_exhausted"))]);

    private static SortKey PlanBoundKey(Plan plan, IReadOnlyList<Container> containers, PackingConfig config)
    {
        var partial = ScorePlan(plan.Packed, [], config).Keys.ToArray();
        var countIndex = config.Objective == "default" ? 1 : 2;
        partial[countIndex] += AdditionalContainerLowerBound(plan, containers);
        return SortKey.Of([.. partial.Select(key => (object)key),
            SortKey.Of([.. plan.Remaining.Select(item => (object)item.Id)]),
            SortKey.Of([.. plan.Packed.Select(container => (object)container.Container.Id)])]);
    }

    /// <summary>
    /// An admissible bound on unopened containers: the largest available capacity granted to
    /// every future container. The volume half is disabled for nesting.
    /// </summary>
    private static long AdditionalContainerLowerBound(Plan plan, IReadOnlyList<Container> containers)
    {
        var available = containers.Where(c => plan.Inventory[c.Id] is null or > 0).ToList();
        if (plan.Remaining.IsEmpty || available.Count == 0)
        {
            return 0;
        }
        long lower = 0;
        if (!plan.Remaining.Any(item => item.Item.NestingHeight is not null))
        {
            var maximumVolume = available.Max(c => c.InnerDimensions.Volume);
            if (maximumVolume > 0)
            {
                var totalVolume = plan.Remaining.Aggregate(Int128.Zero, (sum, item) => sum + item.Dimensions.Volume);
                lower = Math.Max(lower, (long)((totalVolume + maximumVolume - 1) / maximumVolume));
            }
        }
        var payloads = available.Where(c => c.MaxPayload is not null).Select(c => c.MaxPayload!.Value.Ticks).ToList();
        if (payloads.Count == available.Count && payloads.Count > 0)
        {
            var maximumPayload = payloads.Max();
            if (maximumPayload > 0)
            {
                var totalWeight = plan.Remaining.Aggregate(Int128.Zero, (sum, item) => sum + item.Weight.Ticks);
                lower = Math.Max(lower, (long)((totalWeight + maximumPayload - 1) / maximumPayload));
            }
        }
        return lower;
    }
}
