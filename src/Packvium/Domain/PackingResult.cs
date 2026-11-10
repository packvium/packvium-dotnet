using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

public enum PackingStatus
{
    Optimal,
    Feasible,
    BestFound,
    TimeLimit,
    Infeasible,
    InvalidResult,
}

public static class PackingStatuses
{
    public static string Value(this PackingStatus status) => status switch
    {
        PackingStatus.Optimal => "optimal",
        PackingStatus.Feasible => "feasible",
        PackingStatus.BestFound => "best_found",
        PackingStatus.TimeLimit => "time_limit",
        PackingStatus.Infeasible => "infeasible",
        _ => "invalid_result",
    };
}

/// <summary>
/// One open result axis. Attribute values are booleans, start records or lists of them; the
/// code is a string so a newer producer's value survives an older client.
/// </summary>
public sealed record ResultFact
{
    public ResultFact(string code, ImmutableArray<KeyValuePair<string, object>> attributes = default)
    {
        if (code.Length == 0)
        {
            throw new ArgumentException("result fact code must not be empty");
        }
        Code = code;
        Attributes = attributes.IsDefault ? [] : attributes;
    }

    public string Code { get; }

    /// <summary>In emission order.</summary>
    public ImmutableArray<KeyValuePair<string, object>> Attributes { get; }

    public object Attribute(string key) => Attributes.First(attribute => attribute.Key == key).Value;
}

/// <summary>One portfolio start's lifecycle.</summary>
public sealed record StartRecord(
    string Id,
    bool Started,
    bool Completed,
    bool Truncated,
    bool Selected = false,
    bool GlobalDeadlineReached = false);

public sealed record SolverMetrics(
    long CandidatePointsConsidered = 0,
    long OrientationsConsidered = 0,
    long FeasibleCandidates = 0,
    long CollisionChecks = 0,
    long SupportChecks = 0,
    long SpacePartitions = 0,
    long SearchNodesExpanded = 0);

/// <summary>How a particular implementation searched -- diagnostic, never part of the canonical comparison.</summary>
public sealed record AlgorithmReport(
    string Profile,
    string Solver,
    long DurationMs,
    long Seed,
    bool TimeLimitReached = false,
    bool EffortLimitReached = false,
    long CandidatesEvaluated = 0,
    long PlacementsAttempted = 0,
    SolverMetrics? Metrics = null)
{
    public SolverMetrics Metrics { get; init; } = Metrics ?? new SolverMetrics();
}

public static class ResultFacts
{
    public static ResultFact AggregateTermination(IReadOnlyList<StartRecord> starts, bool error = false)
    {
        if (starts.Count == 0)
        {
            throw new ArgumentException("termination aggregation requires at least one start record");
        }
        var selected = starts.Where(record => record.Selected).ToList();
        if (selected.Count != 1)
        {
            throw new ArgumentException("termination aggregation requires exactly one selected start");
        }
        var winningTruncated = selected[0].Truncated;
        var globalDeadline = starts.Any(record => record.GlobalDeadlineReached);
        var code = error ? "error" : winningTruncated || globalDeadline ? "time_limit" : "complete";
        return new ResultFact(code,
        [
            new("any_start_truncated", starts.Any(record => record.Truncated)),
            new("all_required_starts_completed", starts.All(record => record.Completed)),
            new("winning_start_truncated", winningTruncated),
            new("global_deadline_reached", globalDeadline),
            new("starts", starts.ToImmutableArray()),
        ]);
    }

    public static (ResultFact Feasibility, ResultFact Termination, ResultFact Optimality) Derive(PackingStatus status,
        bool complete, bool timeLimitReached)
    {
        var feasibility = status == PackingStatus.Infeasible ? "infeasible"
            : complete && status != PackingStatus.InvalidResult ? "feasible"
            : "unknown";
        var termination = status == PackingStatus.InvalidResult ? "error" : timeLimitReached ? "time_limit" : "complete";
        var optimality = status switch
        {
            PackingStatus.Optimal => "proven_optimal",
            PackingStatus.Infeasible => "proven_infeasible",
            _ when !complete && status != PackingStatus.InvalidResult => "best_found",
            _ => "not_proven",
        };
        return (new ResultFact(feasibility), new ResultFact(termination), new ResultFact(optimality));
    }
}

/// <summary>The immutable answer to one request: the selected packing plus its ranked alternatives.</summary>
public sealed class PackingResult
{
    public PackingResult(PackingStatus status, IEnumerable<PackedContainer> containers, IEnumerable<UnpackedItem> unpacked,
        AlgorithmReport algorithm, IEnumerable<long> score, IEnumerable<string>? warnings = null,
        IEnumerable<PackingResult>? alternatives = null, ResultFact? feasibility = null, ResultFact? termination = null,
        ResultFact? optimality = null, string objective = "default", IEnumerable<object>? catalogVersionsUsed = null)
    {
        Status = status;
        Containers = [.. containers];
        Unpacked = [.. unpacked];
        Algorithm = algorithm;
        Score = [.. score];
        Warnings = warnings?.ToImmutableArray() ?? [];
        Alternatives = alternatives?.ToImmutableArray() ?? [];
        Objective = objective;
        CatalogVersionsUsed = catalogVersionsUsed?.ToImmutableArray() ?? [];
        var derived = ResultFacts.Derive(status, Unpacked.IsEmpty, algorithm.TimeLimitReached);
        Feasibility = feasibility ?? derived.Feasibility;
        Termination = termination ?? ResultFacts.AggregateTermination(
        [
            new StartRecord(algorithm.Solver.Length > 0 ? algorithm.Solver : "unknown", true,
                !algorithm.TimeLimitReached, algorithm.TimeLimitReached, true, algorithm.TimeLimitReached),
        ], status == PackingStatus.InvalidResult);
        Optimality = optimality ?? derived.Optimality;
    }

    public PackingStatus Status { get; }

    public ImmutableArray<PackedContainer> Containers { get; }

    public ImmutableArray<UnpackedItem> Unpacked { get; }

    public AlgorithmReport Algorithm { get; }

    public ImmutableArray<long> Score { get; }

    public ImmutableArray<string> Warnings { get; }

    public ImmutableArray<PackingResult> Alternatives { get; }

    public ResultFact Feasibility { get; }

    public ResultFact Termination { get; }

    public ResultFact Optimality { get; }

    public string Objective { get; }

    /// <summary>The request's catalog references, carried back verbatim by the serialization layer.</summary>
    public ImmutableArray<object> CatalogVersionsUsed { get; }

    public bool Complete => Unpacked.IsEmpty;

    public long PackedItemCount => Containers.Sum(container => container.PlacementCount);

    public PackingResult WithCatalogVersionsUsed(IEnumerable<object> references) =>
        new(Status, Containers, Unpacked, Algorithm, Score, Warnings, Alternatives, Feasibility, Termination, Optimality,
            Objective, references);
}
