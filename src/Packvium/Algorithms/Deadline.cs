using System;
using System.Diagnostics;
using Packvium.Domain;

namespace Packvium.Algorithms;

/// <summary>Raised inside a search when its time or counted-work budget runs out.</summary>
public sealed class TimeLimitReachedException() : Exception("packing time limit reached");

/// <summary>Per-start search counters, reported as <c>algorithm.metrics</c>.</summary>
public sealed class SearchStats
{
    public long CandidatesEvaluated { get; set; }

    public long PlacementsAttempted { get; set; }

    public long CandidatePointsConsidered { get; set; }

    public long CollisionChecks { get; set; }

    public long SupportChecks { get; set; }

    public long SpacePartitions { get; set; }

    public long SearchNodesExpanded { get; set; }

    /// <summary>Times the exact hull test overruled an axis-aligned collision; internal, never serialised.</summary>
    public long HullRefinements { get; set; }

    public bool Exceeds(EffortBudget budget) =>
        (budget.MaxCandidatesEvaluated is { } candidates && CandidatesEvaluated >= candidates)
        || (budget.MaxPlacementAttempts is { } attempts && PlacementsAttempted >= attempts)
        || (budget.MaxSearchNodes is { } nodes && SearchNodesExpanded >= nodes);
}

/// <summary>
/// A monotonic budget that can hand bounded sub-budgets to individual starts. An attached
/// effort budget is read against a live <see cref="SearchStats"/>, so <see cref="Expired"/>
/// trips on whichever bound -- time or counted work -- is hit first. Only the counted bound is
/// reproducible; the clock is a latency safety cutoff.
/// </summary>
public sealed class Deadline
{
    /// <summary>A start never gets less than this, so a long start list cannot starve a solver.</summary>
    public const long MinimumSliceNs = 1_000_000;

    private readonly Func<long> clock;
    private readonly EffortBudget? effortBudget;
    private readonly SearchStats? stats;

    public Deadline(long limitMs, long? limitNs = null, Func<long>? clock = null, EffortBudget? effortBudget = null,
        SearchStats? stats = null)
    {
        this.clock = clock ?? MonotonicNs;
        Started = this.clock();
        LimitNs = limitNs ?? checked(limitMs * 1_000_000);
        this.effortBudget = effortBudget;
        this.stats = stats;
    }

    public long Started { get; }

    public long LimitNs { get; }

    public long ElapsedMs => (clock() - Started) / 1_000_000;

    public long RemainingNs => LimitNs - (clock() - Started);

    public bool EffortExceeded => effortBudget is not null && stats is not null && stats.Exceeds(effortBudget);

    public bool Expired => EffortExceeded || RemainingNs <= 0;

    public Deadline Slice(long parts)
    {
        var remaining = RemainingNs;
        var limit = parts <= 1 ? remaining : Math.Max(Units.IntMath.FloorDiv(remaining, parts), Math.Min(remaining, MinimumSliceNs));
        return new Deadline(0, limit, clock, effortBudget, stats);
    }

    /// <summary>Bind an effort budget to one start's own stats, keeping the same time bound.</summary>
    public Deadline WithEffort(EffortBudget? budget, SearchStats startStats) =>
        new(0, RemainingNs, clock, budget, startStats);

    public void Check()
    {
        if (Expired)
        {
            throw new TimeLimitReachedException();
        }
    }

    private static long MonotonicNs() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
}
