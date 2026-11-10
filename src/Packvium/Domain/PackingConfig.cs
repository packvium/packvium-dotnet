using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Packvium.Units;

namespace Packvium.Domain;

public enum SolverProfile
{
    Fast,
    Balanced,
    Quality,
    ExactSmall,
}

public static class SolverProfiles
{
    public static string Value(this SolverProfile profile) => profile switch
    {
        SolverProfile.Fast => "fast",
        SolverProfile.Balanced => "balanced",
        SolverProfile.Quality => "quality",
        _ => "exact_small",
    };

    public static SolverProfile Parse(string value) => value switch
    {
        "fast" => SolverProfile.Fast,
        "balanced" => SolverProfile.Balanced,
        "quality" => SolverProfile.Quality,
        "exact_small" => SolverProfile.ExactSmall,
        _ => throw new ArgumentException($"'{value}' is not a valid SolverProfile"),
    };
}

/// <summary>
/// Counted-work limits, checked alongside (never instead of) the wall clock: a count is a
/// reproducible measure of work, a clock is not.
/// </summary>
public sealed record EffortBudget(
    long? MaxCandidatesEvaluated = null,
    long? MaxPlacementAttempts = null,
    long? MaxSearchNodes = null,
    long? MaxRestarts = null);

/// <summary>Immutable solve configuration, validated at construction.</summary>
public sealed record PackingConfig
{
    public PackingConfig(
        SolverProfile profile = SolverProfile.Balanced,
        long timeLimitMs = 1000,
        long topK = 3,
        long seed = 42,
        long? maxContainers = null,
        Length clearance = default,
        double minimumSupportRatio = 0.0,
        long exactItemLimit = 7,
        long multiStartOrders = 8,
        bool validateResult = true,
        long maxCandidatesPerItem = 1,
        long maxCandidatePoints = 4096,
        IEnumerable<string>? solvers = null,
        string objective = "default",
        EffortBudget? effortBudget = null,
        long? dimensionalWeightDivisor = null,
        string dimensionalWeightLengthUnit = "in",
        string dimensionalWeightWeightUnit = "lb",
        bool requirePlacementCoordinates = true,
        long parallelStarts = 1,
        long containerPlanBeamWidth = 1,
        long containerPlanNodeLimit = 1,
        IEnumerable<string>? accessDirections = null)
    {
        Profile = profile;
        TimeLimitMs = timeLimitMs;
        TopK = topK;
        Seed = seed;
        MaxContainers = maxContainers;
        Clearance = clearance;
        MinimumSupportRatio = minimumSupportRatio;
        ExactItemLimit = exactItemLimit;
        MultiStartOrders = multiStartOrders;
        ValidateResult = validateResult;
        MaxCandidatesPerItem = maxCandidatesPerItem;
        MaxCandidatePoints = maxCandidatePoints;
        Solvers = solvers?.ToImmutableArray() ?? [];
        Objective = objective;
        EffortBudget = effortBudget;
        DimensionalWeightDivisor = dimensionalWeightDivisor;
        DimensionalWeightLengthUnit = dimensionalWeightLengthUnit;
        DimensionalWeightWeightUnit = dimensionalWeightWeightUnit;
        RequirePlacementCoordinates = requirePlacementCoordinates;
        ParallelStarts = parallelStarts;
        ContainerPlanBeamWidth = containerPlanBeamWidth;
        ContainerPlanNodeLimit = containerPlanNodeLimit;
        AccessDirections = accessDirections?.ToImmutableArray() ?? [];
        Validate();
    }

    public SolverProfile Profile { get; }

    public long TimeLimitMs { get; }

    public long TopK { get; }

    public long Seed { get; }

    public long? MaxContainers { get; }

    public Length Clearance { get; }

    public double MinimumSupportRatio { get; }

    public long ExactItemLimit { get; }

    public long MultiStartOrders { get; }

    public bool ValidateResult { get; }

    public long MaxCandidatesPerItem { get; }

    public long MaxCandidatePoints { get; }

    public ImmutableArray<string> Solvers { get; }

    public string Objective { get; }

    public EffortBudget? EffortBudget { get; }

    /// <summary>A carrier's published divisor; required only by the <c>shipping_cost</c> objective.</summary>
    public long? DimensionalWeightDivisor { get; }

    public string DimensionalWeightLengthUnit { get; }

    public string DimensionalWeightWeightUnit { get; }

    /// <summary>False lets the lattice fast path skip materialising one placement per instance.</summary>
    public bool RequirePlacementCoordinates { get; }

    public long ParallelStarts { get; }

    /// <summary>Width of the deterministic beam over partial container plans.</summary>
    public long ContainerPlanBeamWidth { get; }

    /// <summary>Hard counted-work ceiling for container-plan nodes, independent of wall time.</summary>
    public long ContainerPlanNodeLimit { get; }

    /// <summary>Walls an item may be unloaded through; empty disables the stop-accessibility check.</summary>
    public ImmutableArray<string> AccessDirections { get; }

    /// <summary>This configuration with a narrower search: the greedy and late-item starts run at width one.</summary>
    public PackingConfig WithSearchWidths(long maxCandidatesPerItem, long containerPlanBeamWidth,
        long containerPlanNodeLimit) =>
        new(Profile, TimeLimitMs, TopK, Seed, MaxContainers, Clearance, MinimumSupportRatio, ExactItemLimit,
            MultiStartOrders, ValidateResult, maxCandidatesPerItem, MaxCandidatePoints, Solvers, Objective, EffortBudget,
            DimensionalWeightDivisor, DimensionalWeightLengthUnit, DimensionalWeightWeightUnit,
            RequirePlacementCoordinates, ParallelStarts, containerPlanBeamWidth, containerPlanNodeLimit,
            AccessDirections);

    public static PackingConfig Fast(long timeLimitMs = 200) =>
        new(SolverProfile.Fast, timeLimitMs, topK: 1, multiStartOrders: 1);

    public static PackingConfig Balanced(long timeLimitMs = 1000, long topK = 3) =>
        new(SolverProfile.Balanced, timeLimitMs, topK);

    public static PackingConfig Quality(long timeLimitMs = 5000, long topK = 5) =>
        new(SolverProfile.Quality, timeLimitMs, topK, multiStartOrders: 24, maxCandidatesPerItem: 16,
            containerPlanBeamWidth: 16, containerPlanNodeLimit: 100_000);

    public static PackingConfig ExactSmall(long timeLimitMs = 10000) => new(SolverProfile.ExactSmall, timeLimitMs);

    private void Validate()
    {
        if (TimeLimitMs <= 0 || TopK <= 0 || ExactItemLimit <= 0 || MultiStartOrders <= 0 || MaxCandidatesPerItem <= 0
            || ParallelStarts <= 0 || ContainerPlanBeamWidth <= 0 || ContainerPlanNodeLimit <= 0)
        {
            throw new ArgumentException("positive configuration values required");
        }
        if (MaxCandidatePoints < 16)
        {
            throw new ArgumentException("max_candidate_points must be at least 16");
        }
        if (!(MinimumSupportRatio >= 0 && MinimumSupportRatio <= 1))
        {
            throw new ArgumentException("minimum_support_ratio must be between 0 and 1");
        }
        if (DimensionalWeightDivisor is <= 0)
        {
            throw new ArgumentException("dimensional_weight_divisor must be positive");
        }
    }
}
