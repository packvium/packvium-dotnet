using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Intelligence;

/// <summary>Base class for every scenario error.</summary>
public class ScenarioException(string message) : Exception(message);

/// <summary>
/// Baseline and treatment did not run the identical order corpus, in the same order. Comparing
/// them would not isolate the effect of the version pin, so it is refused.
/// </summary>
public sealed class MismatchedOrderCorpusException(string message) : ScenarioException(message);

/// <summary>
/// The exact, recorded configuration one scenario ran against: explicit catalog, tariff, policy
/// and solver versions, never "current", so a stored scenario replays the same however far the
/// registries have moved since. Per-rule policy pins are kept sorted by rule id.
/// </summary>
public sealed record ScenarioVersionPin
{
    /// <summary>Refuses an empty pin, a non-positive version, and a rule pinned twice.</summary>
    public ScenarioVersionPin(int? catalogVersion = null, int? tariffVersion = null, int? policyVersion = null,
        IEnumerable<(string RuleId, int Version)>? policyVersions = null, string? solverVersion = null)
    {
        var rules = (policyVersions ?? []).ToList();
        if (catalogVersion is null && tariffVersion is null && policyVersion is null && solverVersion is null && rules.Count == 0)
        {
            throw new ArgumentException("a version pin must set at least one of its fields");
        }
        if (new[] { catalogVersion, tariffVersion, policyVersion }.Any(version => version <= 0))
        {
            throw new ArgumentException("version numbers must be positive");
        }
        if (rules.Any(rule => rule.RuleId.Length == 0 || rule.Version <= 0))
        {
            throw new ArgumentException("policy version pins require a rule id and positive version");
        }
        if (rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            throw new ArgumentException("a version pin cannot name the same policy rule twice");
        }
        if (solverVersion is { Length: 0 })
        {
            throw new ArgumentException("solver_version must be non-empty when supplied");
        }
        CatalogVersion = catalogVersion;
        TariffVersion = tariffVersion;
        PolicyVersion = policyVersion;
        PolicyVersions = [.. rules.OrderBy(rule => rule.RuleId, CodePointComparer.Instance)];
        SolverVersion = solverVersion;
    }

    /// <summary>The catalog version the scenario ran against, or null when none was consulted.</summary>
    public int? CatalogVersion { get; }

    /// <summary>The tariff version, or null.</summary>
    public int? TariffVersion { get; }

    /// <summary>The global policy version, or null.</summary>
    public int? PolicyVersion { get; }

    /// <summary>Per-rule policy pins, sorted by rule id in code-point order.</summary>
    public ImmutableArray<(string RuleId, int Version)> PolicyVersions { get; }

    /// <summary>The solver build, or null.</summary>
    public string? SolverVersion { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(ScenarioVersionPin? other) =>
        other is not null && CatalogVersion == other.CatalogVersion && TariffVersion == other.TariffVersion
        && PolicyVersion == other.PolicyVersion && SolverVersion == other.SolverVersion
        && PolicyVersions.SequenceEqual(other.PolicyVersions);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(CatalogVersion, TariffVersion, PolicyVersion, SolverVersion, PolicyVersions.Length);
}

/// <summary>
/// One order's outcome under one version pin. The metrics are Pareto axes the caller's evaluator
/// supplies -- material cost, utilisation, runtime -- and are never computed here.
/// </summary>
public sealed record OrderRunResult
{
    /// <summary>
    /// Refuses a success with no metric, a failure with no reason, an empty metric name and a
    /// non-finite metric: a measured infinity is a broken evaluator, not an encoding.
    /// </summary>
    public OrderRunResult(string orderId, bool succeeded, IReadOnlyDictionary<string, double>? metrics = null,
        string? failureReason = null, object? artifact = null)
    {
        metrics ??= ImmutableSortedDictionary<string, double>.Empty;
        if (orderId.Length == 0)
        {
            throw new ArgumentException("order_id is required");
        }
        if (succeeded && metrics.Count == 0)
        {
            throw new ArgumentException("a succeeded run must report at least one metric");
        }
        if (!succeeded && string.IsNullOrEmpty(failureReason))
        {
            throw new ArgumentException("a failed run must record a failure_reason");
        }
        if (metrics.Keys.Any(name => name.Length == 0))
        {
            throw new ArgumentException("metric names must be non-empty");
        }
        if (metrics.Values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("metric values must be finite");
        }
        OrderId = orderId;
        Succeeded = succeeded;
        Metrics = MetricSet.Copy(metrics);
        FailureReason = failureReason;
        Artifact = artifact;
    }

    /// <summary>The order this run is about.</summary>
    public string OrderId { get; }

    /// <summary>Whether the evaluator produced a usable outcome.</summary>
    public bool Succeeded { get; }

    /// <summary>The metric axes, read-only, in code-point order of their names.</summary>
    public ImmutableSortedDictionary<string, double> Metrics { get; }

    /// <summary>Why the run failed; null for a success.</summary>
    public string? FailureReason { get; }

    /// <summary>The raw artifact the evaluator chose to retain, kept in full.</summary>
    public object? Artifact { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(OrderRunResult? other) =>
        other is not null && OrderId == other.OrderId && Succeeded == other.Succeeded
        && FailureReason == other.FailureReason && Equals(Artifact, other.Artifact)
        && MetricSet.Same(Metrics, other.Metrics);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(OrderId, Succeeded, FailureReason, Metrics.Count);
}

/// <summary>
/// <c>(order_id, version_pin) -&gt; OrderRunResult</c>. Supplied by the caller, which keeps pricing,
/// policy evaluation and packing out of this orchestration layer.
/// </summary>
public delegate OrderRunResult OrderEvaluator(string orderId, ScenarioVersionPin versionPin);

/// <summary>
/// The complete, immutable record of one scenario run: the pin, the order corpus itself, and every
/// order's raw outcome, one per order in the same order.
/// </summary>
public sealed record ScenarioResult
{
    /// <summary>Refuses an empty id or corpus, and runs that do not match the corpus one to one.</summary>
    public ScenarioResult(string scenarioId, ScenarioVersionPin versionPin, IEnumerable<string> orderIds,
        IEnumerable<OrderRunResult> runs)
    {
        var orders = orderIds.ToImmutableArray();
        var all = runs.ToImmutableArray();
        if (scenarioId.Length == 0)
        {
            throw new ArgumentException("scenario_id is required");
        }
        if (orders.IsEmpty)
        {
            throw new ArgumentException("order_ids cannot be empty");
        }
        if (!all.Select(run => run.OrderId).SequenceEqual(orders, StringComparer.Ordinal))
        {
            throw new ArgumentException("runs must correspond 1:1 to order_ids, in the same order");
        }
        ScenarioId = scenarioId;
        VersionPin = versionPin;
        OrderIds = orders;
        Runs = all;
    }

    /// <summary>The scenario's id.</summary>
    public string ScenarioId { get; }

    /// <summary>The versions the scenario ran against.</summary>
    public ScenarioVersionPin VersionPin { get; }

    /// <summary>The order corpus itself, in run order.</summary>
    public ImmutableArray<string> OrderIds { get; }

    /// <summary>One run per order, in the same order.</summary>
    public ImmutableArray<OrderRunResult> Runs { get; }

    /// <summary>Runs that produced a usable outcome.</summary>
    public int SucceededCount => Runs.Count(run => run.Succeeded);

    /// <summary>Runs that did not.</summary>
    public int FailedCount => Runs.Count(run => !run.Succeeded);

    /// <summary>The fraction of orders that produced a usable outcome, always computed rather than left to each caller.</summary>
    public double Confidence => (double)SucceededCount / Runs.Length;

    /// <summary>The run recorded for one order; an unknown order is refused by name.</summary>
    public OrderRunResult RawArtifact(string orderId) =>
        Runs.FirstOrDefault(run => string.Equals(run.OrderId, orderId, StringComparison.Ordinal))
        ?? throw new ScenarioException($"no run recorded for order {PythonText.Repr(orderId)}");

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(ScenarioResult? other) =>
        other is not null && ScenarioId == other.ScenarioId && VersionPin == other.VersionPin
        && OrderIds.SequenceEqual(other.OrderIds) && Runs.SequenceEqual(other.Runs);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ScenarioId, VersionPin, OrderIds.Length);
}

/// <summary>
/// Scenario simulation and what-if comparison (<c>packvium.simulation</c>). A scenario runs a
/// fixed order corpus against one version pin through an injected evaluator and mutates nothing;
/// two scenarios compare order by order as a Pareto report, never as one blended number.
/// </summary>
public static class Scenarios
{
    /// <summary>
    /// Runs every order through <paramref name="evaluator"/> under <paramref name="versionPin"/>, in
    /// order. With a deterministic evaluator the same arguments give an equal result: the
    /// reproducibility is this function's own determinism.
    /// </summary>
    public static ScenarioResult RunScenario(string scenarioId, ScenarioVersionPin versionPin, IEnumerable<string> orderIds,
        OrderEvaluator evaluator)
    {
        var orders = orderIds.ToImmutableArray();
        var runs = orders.Select(orderId => evaluator(orderId, versionPin)).ToImmutableArray();
        return new ScenarioResult(scenarioId, versionPin, orders, runs);
    }

    /// <summary>
    /// Compares two scenarios order by order: each order is its own profile and
    /// <c>baseline</c> and <c>treatment</c> are its two engines. An arm that failed an order has no
    /// metrics and is left out of that order's report; an order neither arm packed has none.
    /// O(o) around the report itself.
    /// </summary>
    public static ImmutableArray<ProfileReport> CompareScenarios(ScenarioResult baseline, ScenarioResult treatment,
        IReadOnlyDictionary<string, bool> higherIsBetter)
    {
        RequireTheSameCorpus(baseline, treatment);
        var candidates = new List<CandidateResult>();
        // Both results already match their corpus one to one and the corpora are equal, so the
        // runs pair positionally in one pass.
        foreach (var (baseRun, treatRun) in baseline.Runs.Zip(treatment.Runs))
        {
            if (baseRun.Succeeded)
            {
                candidates.Add(new CandidateResult(baseRun.OrderId, "baseline", baseRun.Metrics));
            }
            if (treatRun.Succeeded)
            {
                candidates.Add(new CandidateResult(baseRun.OrderId, "treatment", treatRun.Metrics));
            }
        }
        return Pareto.GenerateReport(candidates, higherIsBetter);
    }

    internal static void RequireTheSameCorpus(ScenarioResult baseline, ScenarioResult treatment)
    {
        if (!baseline.OrderIds.SequenceEqual(treatment.OrderIds, StringComparer.Ordinal))
        {
            throw new MismatchedOrderCorpusException(
                "baseline and treatment must have run the identical order corpus, in the same order");
        }
    }
}
