using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>The result's own claims about the search: counters, counted-work limits and the three result facts.</summary>
internal static class Facts
{
    private static readonly ImmutableArray<string> Metrics =
    [
        "candidate_points_considered", "orientations_considered", "feasible_candidates", "collision_checks",
        "support_checks", "space_partitions", "search_nodes_expanded",
    ];

    private static readonly ImmutableArray<(string Budget, string Metric)> MetricLimits =
    [
        ("max_candidates_evaluated", "feasible_candidates"),
        ("max_placement_attempts", "orientations_considered"),
        ("max_search_nodes", "search_nodes_expanded"),
    ];

    public static JsonObject Algorithm(JsonValue result) =>
        Json.At(result, "algorithm") as JsonObject ?? throw MalformedResultException.ContractBreak("algorithm must be an object");

    /// <summary>Every counter is a non-negative integer, and the legacy counters mirror two of them. O(1).</summary>
    public static void CheckAlgorithmMetrics(JsonValue result, List<ResultIssue> issues)
    {
        var algorithm = Algorithm(result);
        if (Json.Get(algorithm, "metrics") is not JsonObject metrics
            || Metrics.Any(field => Json.StrictInt(Json.Get(metrics, field)) is not { } count || count < 0))
        {
            issues.Add(Issues.Of("solver_metrics_mismatch", $"{"algorithm.metrics must contain every non-negative integer counter"}"));
            return;
        }
        var candidates = Json.StrictInt(Json.Get(algorithm, "candidates_evaluated"));
        var attempts = Json.StrictInt(Json.Get(algorithm, "placements_attempted"));
        if (candidates is null || attempts is null
            || candidates != Json.StrictInt(Json.At(metrics, "feasible_candidates"))
            || attempts != Json.StrictInt(Json.At(metrics, "orientations_considered")))
        {
            issues.Add(Issues.Of("solver_metrics_mismatch", $"{"legacy candidate counters must mirror algorithm.metrics"}"));
        }
    }

    /// <summary>Counted-work limits are respected, and the stop flag is evidenced by one. O(starts).</summary>
    public static void CheckEffortBudget(JsonValue request, JsonValue result, List<ResultIssue> issues)
    {
        var algorithm = Json.At(result, "algorithm");
        if (Json.Get(algorithm, "effort_limit_reached") is not JsonBool { Value: var effortLimited })
        {
            throw MalformedResultException.ContractBreak("algorithm.effort_limit_reached must be a boolean");
        }
        var budget = Json.Get(Json.Get(request, "configuration", Json.EmptyObject), "effort_budget");
        if (budget is JsonNull)
        {
            if (effortLimited)
            {
                issues.Add(Issues.Of("effort_budget_mismatch", $"{"effort_limit_reached is true but the request has no effort_budget"}"));
            }
            return;
        }
        if (budget is not JsonObject)
        {
            throw MalformedResultException.ContractBreak("config.effort_budget must be an object");
        }
        if (Json.Get(algorithm, "metrics") is not JsonObject metrics)
        {
            return;
        }
        var reached = false;
        foreach (var (budgetName, metricName) in MetricLimits)
        {
            var limit = Json.Get(budget, budgetName);
            if (limit is JsonNull || Json.StrictInt(Json.Get(metrics, metricName)) is not { } value)
            {
                continue;
            }
            var order = -Json.CompareInt(limit, value);
            if (order > 0)
            {
                issues.Add(Issues.Of("effort_budget_exceeded", $"{metricName} is {value}, above {budgetName}={PyFormat.Str(limit)}"));
            }
            reached = reached || order >= 0;
        }
        var restartLimit = Json.Get(budget, "max_restarts");
        var starts = Json.Get(Json.Get(result, "termination", Json.EmptyObject), "starts", Json.EmptyList);
        if (Json.IntInstance(restartLimit) is { } restarts && starts is JsonArray startList)
        {
            var started = startList.Items.Count(start => start is JsonObject && Json.IsTrue(Json.Get(start, "started")));
            if (started > restarts)
            {
                issues.Add(Issues.Of("effort_budget_exceeded", $"started {started} solver runs, above max_restarts={PyFormat.Str(restartLimit)}"));
            }
            reached = reached || started >= restarts;
        }
        if (effortLimited && !reached)
        {
            issues.Add(Issues.Of("effort_budget_mismatch", $"{"effort_limit_reached is true but no configured counted-work boundary was reached"}"));
        }
    }

    /// <summary>The limit flags, the start records and the feasibility, termination and optimality codes agree. O(starts).</summary>
    public static void CheckResultFacts(JsonValue result, List<ResultIssue> issues)
    {
        var algorithm = Algorithm(result);
        var timedOut = Flag(algorithm, "time_limit_reached");
        var effortLimited = Flag(algorithm, "effort_limit_reached");
        if (timedOut && effortLimited)
        {
            issues.Add(Issues.Of("result_fact_mismatch", $"{"time_limit_reached and effort_limit_reached cannot both be true"}"));
        }
        if (Json.At(result, "termination") is not JsonObject termination)
        {
            throw MalformedResultException.ContractBreak("termination must be an object");
        }
        StartRecords.Check(termination, algorithm, new SearchLimits(timedOut, effortLimited), issues);

        var status = Json.At(result, "status");
        var complete = Json.Truthy(Json.At(result, "complete"));
        var invalid = Json.IsText(status, "invalid_result");
        var expected = new (string Name, string Code)[]
        {
            ("feasibility", Json.IsText(status, "infeasible") ? "infeasible" : complete && !invalid ? "feasible" : "unknown"),
            ("termination", invalid ? "error" : timedOut ? "time_limit" : effortLimited ? "effort_limit" : "complete"),
            ("optimality", Json.IsText(status, "optimal") ? "proven_optimal"
                : Json.IsText(status, "infeasible") ? "proven_infeasible"
                : !complete && !invalid ? "best_found" : "not_proven"),
        };
        foreach (var (name, code) in expected)
        {
            var actual = FactCode(result, name);
            if (actual != code)
            {
                issues.Add(Issues.Of("result_fact_mismatch", $"{name}.code is {PyFormat.StringRepr(actual)}, expected {PyFormat.StringRepr(code)}"));
            }
        }
    }

    private static bool Flag(JsonObject algorithm, string name) =>
        Json.Get(algorithm, name) is JsonBool flag ? flag.Value : throw MalformedResultException.ContractBreak($"algorithm.{name} must be a boolean");

    private static string FactCode(JsonValue result, string name)
    {
        if (Json.At(result, name) is not JsonObject fact)
        {
            throw MalformedResultException.ContractBreak($"{name} must be an object");
        }
        return Json.Get(fact, "code") is JsonString { Value.Length: > 0 } code
            ? code.Value
            : throw MalformedResultException.ContractBreak($"{name}.code must be a non-empty string");
    }
}

/// <summary>
/// <c>termination.starts</c>: each start's lifecycle flags are consistent, exactly one is selected,
/// and the aggregate flags, the winning start and the limit flags follow from them. O(starts).
/// </summary>
internal static class StartRecords
{
    private static readonly ImmutableArray<string> LifecycleFlags = ["started", "completed", "truncated", "selected", "global_deadline_reached"];

    public static void Check(JsonObject termination, JsonObject algorithm, SearchLimits limits, List<ResultIssue> issues)
    {
        if (Json.Get(termination, "starts") is not JsonArray { Items.Length: > 0 } startList)
        {
            issues.Add(Issues.Of("termination_aggregate_mismatch", $"{"termination.starts must contain at least one start"}"));
            return;
        }
        var starts = startList.Items;
        CheckLifecycles(starts, issues);
        var selected = starts.Where(start => IsTrue(start, "selected")).ToList();
        if (selected.Count != 1)
        {
            issues.Add(Issues.Of("termination_aggregate_mismatch", $"expected one selected start, found {selected.Count}"));
            return;
        }
        if (!starts.All(start => start is JsonObject))
        {
            return;
        }
        var winner = selected[0];
        var aggregate = new (string Name, bool Expected)[]
        {
            ("any_start_truncated", starts.Any(start => IsTrue(start, "truncated"))),
            ("all_required_starts_completed", starts.All(start => IsTrue(start, "completed"))),
            ("winning_start_truncated", IsTrue(winner, "truncated")),
            ("global_deadline_reached", starts.Any(start => IsTrue(start, "global_deadline_reached"))),
        };
        foreach (var (name, expected) in aggregate)
        {
            var reported = Json.Get(termination, name);
            if (reported is not JsonBool flag || flag.Value != expected)
            {
                issues.Add(Issues.Of("termination_aggregate_mismatch", $"{name} is {PyFormat.Repr(reported)}, expected {PyFormat.Bool(expected)}"));
            }
        }
        CheckWinner(algorithm, winner, issues);
        var deadline = aggregate[3].Expected;
        if (deadline && !limits.TimedOut)
        {
            issues.Add(Issues.Of("legacy_time_limit_mismatch", $"{"global_deadline_reached requires time_limit_reached"}"));
        }
        if ((deadline || aggregate[2].Expected) != (limits.TimedOut || limits.EffortLimited))
        {
            issues.Add(Issues.Of("termination_limit_mismatch", $"{"selected-start truncation must identify exactly one reached limit"}"));
        }
    }

    private static void CheckLifecycles(ImmutableArray<JsonValue> starts, List<ResultIssue> issues)
    {
        var malformed = false;
        foreach (var start in starts)
        {
            if (start is not JsonObject || Json.Get(start, "id") is not JsonString id)
            {
                malformed = true;
                continue;
            }
            var flags = LifecycleFlags.Select(name => Json.Get(start, name)).ToList();
            if (!flags.All(flag => flag is JsonBool))
            {
                malformed = true;
                continue;
            }
            var (started, completed, truncated) = (Json.IsTrue(flags[0]), Json.IsTrue(flags[1]), Json.IsTrue(flags[2]));
            if ((completed && (!started || truncated)) || (truncated && (!started || completed)) || (!started && (completed || truncated)))
            {
                issues.Add(Issues.Of("start_record_mismatch", $"{id.Value}: inconsistent lifecycle flags"));
            }
        }
        if (malformed)
        {
            issues.Add(Issues.Of("start_record_mismatch", $"{"a start record is missing its id or boolean lifecycle fields"}"));
        }
    }

    private static void CheckWinner(JsonObject algorithm, JsonValue winner, List<ResultIssue> issues)
    {
        if (Json.Get(algorithm, "solver") is not JsonString solver)
        {
            return;
        }
        var selectedId = Json.Get(winner, "id", new JsonString(string.Empty));
        if (Json.IsText(selectedId, solver.Value)
            || Json.Text(selectedId, "a start id").StartsWith($"{solver.Value}#", System.StringComparison.Ordinal))
        {
            return;
        }
        issues.Add(Issues.Of("winning_start_mismatch",
            $"selected start {PyFormat.Repr(selectedId)} does not identify solver {PyFormat.StringRepr(solver.Value)}"));
    }

    private static bool IsTrue(JsonValue start, string name) => start is JsonObject && Json.IsTrue(Json.Get(start, name));
}
