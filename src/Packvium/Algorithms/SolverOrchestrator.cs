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

    /// <summary>
    /// Searches for placements of <paramref name="items"/>. <paramref name="preloaded"/> are the
    /// containers a request's fixed placements name, each holding only its fixed items, in the
    /// order they open: every start fills them first and keeps them, whatever else it does.
    /// </summary>
    public PortfolioRun Solve(ImmutableArray<ItemInstance> items, ImmutableArray<Container> containers,
        PackingConfig config, Deadline deadline, ImmutableArray<PackedContainer> preloaded = default)
    {
        preloaded = preloaded.IsDefault ? [] : preloaded;
        var starts = Starts(items, config, containers);
        if (config.EffortBudget?.MaxRestarts is { } restarts)
        {
            starts = [.. starts.Take((int)Math.Min(restarts, int.MaxValue))];
        }
        var run = SolveSequential(items, containers, config, deadline, starts, config.EffortBudget, preloaded);
        return WithoutABlockStartThatOnlyTies(run, starts, config);
    }

    /// <summary>
    /// Drops the added block start's answer unless it strictly beats the profile's own. The start
    /// is there to improve an answer, not to replace an equal one: on a tie the ranking falls to
    /// the solver name, which would hand the result to a lattice that knows nothing of the
    /// orderings the profile's own starts honour. O(s) scores for s solutions.
    /// </summary>
    private static PortfolioRun WithoutABlockStartThatOnlyTies(PortfolioRun run, List<Start> starts, PackingConfig config)
    {
        if (starts.Count == 0 || starts[^1].Solver is not HomogeneousBlockSolver { NodeLimit: not null } blocks)
        {
            return run;
        }
        var scorer = ObjectiveScorers.Resolve(config.Objective, config);
        var added = $"{blocks.Name}:{starts[^1].OrderName}";
        var own = run.Solutions.Where(solution => solution.SolverName != added)
            .Select(solution => scorer.ScoreContainers(solution.Containers, solution.Unpacked)).ToList();
        if (own.Count == 0)
        {
            return run;
        }
        var bestOwn = own.Min()!;
        ImmutableArray<RawSolution> kept = [.. run.Solutions.Where(solution => solution.SolverName != added
            || scorer.ScoreContainers(solution.Containers, solution.Unpacked) < bestOwn)];
        return new PortfolioRun(kept, run.Starts);
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
        var smallFirst = new List<(string, Func<ItemInstance, object[]>)>();
        if (config.Profile == SolverProfile.Quality)
        {
            smallFirst.Add(("small_edge", i => [i.Dimensions.MaxEdge, i.Dimensions.Volume, i.Id]));
            smallFirst.Add(("small_volume", i => [i.Dimensions.Volume, i.Dimensions.MaxEdge, i.Id]));
        }
        List<(string, Func<ItemInstance, object[]>)> largeFirst =
        [
            ("volume", i => [-i.Dimensions.Volume, -i.Dimensions.MaxEdge, -i.Weight.Ticks, i.Id]),
            ("base_area", i => [-i.Dimensions.BaseArea, -i.Dimensions.Volume, i.Id]),
            ("weight", i => [-i.Weight.Ticks, -i.Dimensions.Volume, i.Id]),
            ("constrained", i => [i.Item.AllowedRotations.Length, -i.Item.MinimumSupportRatio, -i.Dimensions.Volume, i.Id]),
        ];
        // Small items first places the most pieces; large items first fills the most space. The
        // objective decides which family gets the early, fully funded starts.
        List<(string, Func<ItemInstance, object[]>)> strategies =
            config.RanksByVolume ? [.. largeFirst, .. smallFirst] : [.. smallFirst, .. largeFirst];
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

    /// <summary>Lattice nodes the block start of <c>fast</c> and <c>balanced</c> may enumerate per mode.</summary>
    private const long BlockStartNodeLimit = 100_000;

    /// <summary>
    /// Beam width and candidates per item for the <c>best:beam</c> refinement of a winning late-type
    /// ordering. Other refinements keep the profile's width.
    /// </summary>
    private const long LateTypeRefinementWidth = 4;

    /// <summary>
    /// The block start <c>fast</c> and <c>balanced</c> add to their own, or null. Only
    /// where the lattice can express the whole request, where the items do not all lead alike a
    /// lattice cannot honour the caller's say over which goes first, and one item type is the
    /// grid's case, exact there.
    /// </summary>
    private HomogeneousBlockSolver? BlockStart(ImmutableArray<ItemInstance> items, PackingConfig config,
        ImmutableArray<Container> containers)
    {
        if (config.Profile is not (SolverProfile.Fast or SolverProfile.Balanced) || !config.Solvers.IsEmpty)
        {
            return null;
        }
        if (customSolvers.Count > 0 || customConstraints.Count > 0
            || items.Select(item => item.Item.Id).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return null;
        }
        if (items.Select(item => SortKey.Of(Ordering.Lead(item, config))).Distinct().Count() > 1)
        {
            return null;
        }
        var modes = HomogeneousBlockSolver.Modes;
        if (config.Profile == SolverProfile.Fast)
        {
            // One enumeration, the one the objective ranks by: fast is latency-bound.
            modes = config.RanksByVolume ? ["volume"] : ["count"];
        }
        var solver = new HomogeneousBlockSolver(modes: modes, nodeLimit: BlockStartNodeLimit);
        return containers.IsEmpty || !containers.All(container => solver.Supports(container, items, config)) ? null : solver;
    }

    private List<Start> Starts(ImmutableArray<ItemInstance> items, PackingConfig config,
        ImmutableArray<Container> containers)
    {
        var orders = Orders(items, config);
        var starts = new List<Start>();
        foreach (var solver in Solvers(items, config))
        {
            var chosen = solver.OrderInsensitive ? orders.Take(1) : orders;
            starts.AddRange(chosen.Select(order => new Start(solver, order.Name, order.Order)));
        }
        // Last, so a restart budget drops it first and the profile's own starts keep theirs.
        if (orders.Count > 0 && BlockStart(items, config, containers) is { } blocks)
        {
            starts.Add(new Start(blocks, orders[0].Name, orders[0].Order));
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
        PackingConfig config, Deadline deadline, List<Start> starts, EffortBudget? effortBudget,
        ImmutableArray<PackedContainer> preloaded)
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
            var startConfig = StartConfig(start.OrderName, config);
            var outcome = AcrossContainers(start.Solver, start.Order, containers, startConfig, stats, budget, preloaded);
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
        return FinishPortfolio(items, containers, deadline, results, records, preloaded);
    }

    /// <summary>
    /// The configuration one portfolio start searches with: the greedy orderings and their
    /// late-type neighbourhoods keep one candidate; a <c>:beam</c> refinement of a winning
    /// neighbourhood still carries <c>:late_</c> in its name, yet searches wide, at most
    /// <see cref="LateTypeRefinementWidth"/>.
    /// </summary>
    private static PackingConfig StartConfig(string orderName, PackingConfig config)
    {
        var beam = orderName.EndsWith(":beam", StringComparison.Ordinal);
        var late = orderName.Contains(":late_", StringComparison.Ordinal);
        if (!beam && (orderName.EndsWith(":greedy", StringComparison.Ordinal) || late))
        {
            return config.WithSearchWidths(1, 1, 1);
        }
        if (beam && late)
        {
            return config.WithSearchWidths(Math.Min(config.MaxCandidatesPerItem, LateTypeRefinementWidth),
                Math.Min(config.ContainerPlanBeamWidth, LateTypeRefinementWidth), config.ContainerPlanNodeLimit);
        }
        return config;
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
        Deadline deadline, List<RawSolution> results, List<StartRecord> records, ImmutableArray<PackedContainer> preloaded)
    {
        var globalDeadline = deadline.Expired;
        var finished = records.Select(record => record with { GlobalDeadlineReached = globalDeadline }).ToList();
        if (results.Count == 0)
        {
            const string fallbackId = "portfolio:fallback";
            results.Add(new RawSolution(fallbackId, preloaded,
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
            instance.Dimensions.UniqueRotations(rotations).Any(pair => FitsAlone(container, pair.Dimensions)));
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
    /// Whether one box of these dimensions fits this container empty: the frame, and for a flat bag
    /// the girth rule too, so a bag too thin for an item proves it unplaceable.
    /// </summary>
    private static bool FitsAlone(Container container, Dimensions dimensions) =>
        container.IsFlatBag
            ? FlatBag.FitsAlone(container.InnerDimensions, dimensions)
            : dimensions.FitsInside(container.InnerDimensions);

    /// <summary>How many containers of each template a layout opened. O(P), once per layout.</summary>
    private static Dictionary<string, int> OpenedByType(IEnumerable<PackedContainer> packed)
    {
        var opened = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var packedContainer in packed)
        {
            opened[packedContainer.Container.Id] = opened.GetValueOrDefault(packedContainer.Container.Id) + 1;
        }
        return opened;
    }

    /// <summary>
    /// The container limit that left <paramref name="instance"/> out, for the
    /// <c>no_feasible_placement</c> fallback, or null when no limit explains it. Only
    /// containers that admit the item on every structural count are asked: when an axle refuses
    /// the item alone in every one of them, the axle is the reason; otherwise, when every one that
    /// would take it alone is out of stock, or <c>max_containers</c> is reached, the inventory is.
    /// O(C * R) for C templates and R rotations, reading only the request and the final layout.
    /// </summary>
    private static string? LimitReason(ItemInstance instance, IReadOnlyList<Container> containers,
        Dictionary<string, int> opened, PackingConfig config)
    {
        var item = instance.Item;
        var envelopes = instance.Dimensions.UniqueRotations(item.AllowedRotations)
            .Select(pair => pair.Dimensions.Expand(config.Clearance)).ToList();
        bool Compatible(Container container) =>
            envelopes.Any(envelope => FitsAlone(container, envelope))
            && (container.MaxPayload is not { } payload || instance.Weight.Ticks <= payload.Ticks)
            && (item.EligibleContainerTags.Count == 0 || item.EligibleContainerTags.Overlaps(container.Tags));
        bool AdmitsAlone(Container container) =>
            container.Axles is not { } axles
            || envelopes.Any(envelope => FitsAlone(container, envelope) && AxleLoad.AdmitsAlone(axles,
                container.TareWeight.Ticks, container.InnerDimensions.Length.Ticks, instance.Weight.Ticks,
                envelope.Length.Ticks));
        var candidates = containers.Where(Compatible).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        var admitting = candidates.Where(AdmitsAlone).ToList();
        if (admitting.Count == 0)
        {
            return "axle_overloaded";
        }
        if (config.MaxContainers is { } maximum && opened.Values.Sum() >= maximum)
        {
            return "container_inventory_exhausted";
        }
        return admitting.All(container => container.Quantity is { } quantity && opened.GetValueOrDefault(container.Id) >= quantity)
            ? "container_inventory_exhausted"
            : null;
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
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline,
        ImmutableArray<PackedContainer> preloaded)
    {
        var opened = OpenPreloaded(solver, preloaded, items, containers, config, stats, deadline);
        return config.ContainerPlanBeamWidth > 1 && containerSelector is DefaultContainerSelector
            ? AcrossContainersBeam(solver, items, containers, config, stats, deadline, opened)
            : AcrossContainersGreedy(solver, items, containers, config, stats, deadline, opened);
    }

    /// <summary>
    /// Fills every container holding fixed items, first, and keeps it whatever it gets; returns
    /// the plan every start continues from. A container search cannot fill -- the deadline has
    /// passed, or the solver ran out of time -- is kept with its fixed items alone: dropping it
    /// would drop items the request says are already loaded. One single-container solve per
    /// fixed container.
    /// </summary>
    private static (Plan Plan, bool Reached) OpenPreloaded(ISingleContainerSolver solver,
        ImmutableArray<PackedContainer> preloaded, ImmutableArray<ItemInstance> items,
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline)
    {
        var plan = new Plan([], items,
            containers.ToImmutableDictionary(c => c.Id, c => c.Quantity, StringComparer.Ordinal),
            containers.ToImmutableDictionary(c => c.Id, _ => 0, StringComparer.Ordinal));
        var reached = false;
        foreach (var fixedContainer in preloaded)
        {
            var seeded = fixedContainer.Container.WithPreloaded(fixedContainer.Placements);
            SingleContainerSolution? one = null;
            if (!reached && !deadline.Expired)
            {
                try
                {
                    one = solver.PackOne(seeded, fixedContainer.Sequence, plan.Remaining, config, stats, deadline);
                }
                catch (TimeLimitReachedException)
                {
                    one = null;
                }
            }
            if (one is null || one.TimeLimitReached)
            {
                reached = true;
            }
            var state = one?.State ?? new ContainerState(seeded, fixedContainer.Sequence);
            var (packedContainer, ids) = PackedState(state);
            plan = new Plan(
                plan.Packed.Add(packedContainer),
                [.. plan.Remaining.Where(item => !ids.Contains(item.Id))],
                plan.Inventory[seeded.Id] is { } count ? plan.Inventory.SetItem(seeded.Id, count - 1) : plan.Inventory,
                plan.Sequences.SetItem(seeded.Id, fixedContainer.Sequence),
                plan.Exhaustive && one is not null && one.Exhaustive,
                plan.DominantLattice && one is not null && one.DominantLattice);
        }
        return (plan, reached);
    }

    /// <summary>
    /// A result carries the request's container, not the seeded instance: anything that rebuilds
    /// a state from a packed container re-adds every placement itself, and a seeded container
    /// would add the fixed ones twice.
    /// </summary>
    private static (PackedContainer Packed, HashSet<string> Ids) PackedState(ContainerState state)
    {
        var container = state.Container.Preloaded.IsEmpty ? state.Container : state.Container.WithPreloaded([]);
        if (state.LatticeSummary is { } summary)
        {
            return (new PackedContainer(container, state.Sequence, [], summary, state.LatticeItems),
                state.LatticeItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal));
        }
        return (new PackedContainer(container, state.Sequence, Loads.WithTopLoads(state.Placements)),
            state.Placements.Select(placement => placement.Instance.Id).ToHashSet(StringComparer.Ordinal));
    }

    private static long MaximumContainers(IReadOnlyList<ItemInstance> items, IReadOnlyList<Container> containers,
        PackingConfig config) =>
        config.MaxContainers ?? containers.Sum(container => (long)(container.Quantity ?? items.Count));

    private static List<Container> CostOrder(IReadOnlyList<Container> containers) =>
        [.. containers.OrderBy(c => SortKey.Of(c.CostMinor, c.InnerDimensions.Volume, c.Id))];

    private Outcome AcrossContainersGreedy(ISingleContainerSolver solver, ImmutableArray<ItemInstance> items,
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline,
        (Plan Plan, bool Reached) opened)
    {
        var (start, reached) = opened;
        var remaining = start.Remaining.ToList();
        var packed = start.Packed.ToList();
        var (exhaustive, dominantLattice) = (start.Exhaustive, start.DominantLattice);
        var inventory = new Dictionary<string, int?>(start.Inventory, StringComparer.Ordinal);
        var sequences = new Dictionary<string, int>(start.Sequences, StringComparer.Ordinal);
        var maximum = MaximumContainers(items, containers, config);
        var ordered = CostOrder(containers);
        while (remaining.Count > 0 && packed.Count < maximum && !reached)
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
                    (one, _) = Trial(solver, container, sequences[container.Id] + 1, roundItems, config, stats, deadline);
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
                var score = RoundScore(one, roundItems.Length, config);
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

    /// <summary>
    /// How one container's trial ranks in its round, read from the container the trial built, so a
    /// made-to-measure trial prices the box that ships: weight-priced objectives rank by what the
    /// carrier bills, <c>maximum_volume</c> by the volume loaded unless a caller chose the selector.
    /// </summary>
    private SortKey RoundScore(SingleContainerSolution one, int roundCount, PackingConfig config)
    {
        var container = one.State.Container;
        if (config.Objective is not ("shipping_cost" or "lowest_landed_cost") || config.DimensionalWeightDivisor is null)
        {
            if (config.RanksByVolume && containerSelector is DefaultContainerSelector)
            {
                var used = one.State.UsedVolumeTicks;
                return SortKey.Of(-used, container.CostMinor, container.InnerDimensions.Volume - used, container.Id);
            }
            return containerSelector.Score(container, one);
        }
        var billed = Math.Max(container.TareWeight.Ticks + one.State.PayloadTicks, one.State.BilledDimensionalTicks(config));
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
        var opened = OpenedByType(packed);
        foreach (var instance in remaining)
        {
            var (reason, details) = UnpackedReason(instance, containers, reached);
            if (reason == "no_feasible_placement" && SupportIsTheBlocker(instance, packed, config))
            {
                reason = "insufficient_support";
            }
            if (reason == "no_feasible_placement")
            {
                reason = LimitReason(instance, containers, opened, config) ?? reason;
            }
            unpacked.Add(new UnpackedItem(instance, reason, details));
        }
        return unpacked.MoveToImmutable();
    }

    /// <summary>
    /// One container's trial for a round, and how many single-container solves it spent: a declared
    /// container is one solve; a made-to-measure one runs the rung chain and returns its trial
    /// already fabricated.
    /// </summary>
    private (SingleContainerSolution Solution, int Trials) Trial(ISingleContainerSolver solver, Container container,
        int sequence, ImmutableArray<ItemInstance> items, PackingConfig config, SearchStats stats, Deadline deadline) =>
        container.MadeToMeasure is null
            ? (solver.PackOne(container, sequence, items, config, stats, deadline), 1)
            : MadeToMeasureTrial(solver, container, sequence, items, config, stats, deadline);

    /// <summary>
    /// Tries each solving envelope in ascending order and keeps the first that places every item;
    /// failing that, the trial this round's own key prefers, the smaller rung on a tie. At most four
    /// solves with two trial states alive. A compact lattice cannot describe a box cut to the
    /// achieved extent, so the rung solves always report coordinates, and a rung choice is a
    /// heuristic, so the trial is never exhaustive.
    /// </summary>
    private (SingleContainerSolution Solution, int Trials) MadeToMeasureTrial(ISingleContainerSolver solver,
        Container container, int sequence, ImmutableArray<ItemInstance> items, PackingConfig config, SearchStats stats,
        Deadline deadline)
    {
        var spec = container.MadeToMeasure!;
        var edges = MadeToMeasure.InstanceEdges(items.Select(item => item.Dimensions), config.Clearance.Ticks);
        var rungs = spec.SolvingEnvelopes(Triple.Of(container.InnerDimensions), edges);
        var rungConfig = config.WithPlacementCoordinates();
        SingleContainerSolution? best = null;
        SortKey? bestKey = null;
        SingleContainerSolution? last = null;
        var stopped = false;
        var trials = 0;
        foreach (var rung in rungs)
        {
            SingleContainerSolution one;
            try
            {
                one = solver.PackOne(container.WithEnvelope(rung), sequence, items, rungConfig, stats, deadline);
            }
            catch (TimeLimitReachedException) when (best is not null)
            {
                stopped = true;
                break;
            }
            trials++;
            stopped = one.TimeLimitReached;
            last = one;
            if (one.State.PlacementCount > 0)
            {
                var trial = Fabricated(container, one);
                if (one.State.PlacementCount == items.Length)
                {
                    return (trial, trials);
                }
                var key = RoundScore(trial, items.Length, config);
                if (best is null || key < bestKey!)
                {
                    (best, bestKey) = (trial, key);
                }
            }
            if (stopped || deadline.Expired)
            {
                stopped = true;
                break;
            }
        }
        var chosen = best ?? last!;
        return (chosen with { TimeLimitReached = chosen.TimeLimitReached || stopped }, trials);
    }

    /// <summary>
    /// A rung trial as the box cut for it: the achieved extent of its placements, envelope far
    /// faces with clearance, rounded up onto the machine grid. O(p).
    /// </summary>
    private static SingleContainerSolution Fabricated(Container declared, SingleContainerSolution one)
    {
        var achieved = FlatBag.ExtentOf(one.State.Placements.Select(placement => placement.EnvelopeBox));
        return one with { State = one.State.CopyWith(declared.Fabricate(achieved)), Exhaustive = false, DominantLattice = false };
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
        ImmutableArray<Container> containers, PackingConfig config, SearchStats stats, Deadline deadline,
        (Plan Plan, bool Reached) opened)
    {
        var ordered = CostOrder(containers);
        var maximum = MaximumContainers(items, containers, config);
        var (initial, reached) = opened;
        List<Plan> beam = reached ? [] : [initial];
        var incumbent = initial;
        long planNodes = 0;
        while (beam.Count > 0 && planNodes < config.ContainerPlanNodeLimit)
        {
            var expansions = new List<Plan>();
            foreach (var plan in beam)
            {
                if (plan.Remaining.IsEmpty || plan.Packed.Length >= maximum)
                {
                    // Already weighed against the incumbent when it was created (below), and the
                    // incumbent only ever improves, so it cannot win now.
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
                    SingleContainerSolution one;
                    int trials;
                    try
                    {
                        (one, trials) = Trial(solver, container, plan.Sequences[container.Id] + 1, plan.Remaining, config,
                            stats, deadline);
                    }
                    catch (TimeLimitReachedException)
                    {
                        planNodes++;
                        reached = true;
                        break;
                    }
                    // Each rung is one single-container solve, so the node limit keeps bounding the
                    // work of a made-to-measure chain.
                    planNodes += trials;
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
                // A hull counts its own solid, as the beam and exact floors do.
                var totalVolume = plan.Remaining.Aggregate(Int128.Zero, (sum, item) => sum + Nesting.ItemOccupiedVolume(item));
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
