using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Commerce;
using Packvium.Units;

namespace Packvium.Intelligence;

/// <summary>Base class for every recommendation error.</summary>
public class RecommendationException(string message) : Exception(message);

/// <summary>
/// One metric's change from baseline to treatment, averaged over the orders both scenarios
/// succeeded on. Signed on purpose: whether a negative delta is good depends on the axis.
/// </summary>
public sealed record ExpectedDelta
{
    /// <summary>Refuses an empty metric name.</summary>
    public ExpectedDelta(string metric, double baselineMean, double treatmentMean)
    {
        if (metric.Length == 0)
        {
            throw new ArgumentException("metric is required");
        }
        Metric = metric;
        BaselineMean = baselineMean;
        TreatmentMean = treatmentMean;
    }

    /// <summary>The metric axis.</summary>
    public string Metric { get; }

    /// <summary>The baseline's mean over the paired cohort.</summary>
    public double BaselineMean { get; }

    /// <summary>The treatment's mean over the paired cohort.</summary>
    public double TreatmentMean { get; }

    /// <summary>Treatment minus baseline.</summary>
    public double Delta => TreatmentMean - BaselineMean;
}

/// <summary>
/// One reviewable proposal: the orders that support it, what changed per metric, how confident
/// the evidence is, what bounds it and how to undo it. It is never itself a mutation -- the only
/// path to a registry is an explicit approval call.
/// </summary>
public sealed record Recommendation
{
    /// <summary>
    /// Refuses a proposal without evidence, deltas, a confidence in [0, 1], a non-empty constraint
    /// or a rollback plan: a proposal that cannot say how to undo itself cannot exist.
    /// </summary>
    public Recommendation(string recommendationId, string proposal, IEnumerable<string> supportingOrderIds,
        IEnumerable<ExpectedDelta> expectedDeltas, double confidence, IEnumerable<string> constraints, string rollbackPlan)
    {
        var supporting = supportingOrderIds.ToImmutableArray();
        var deltas = expectedDeltas.ToImmutableArray();
        var bounds = constraints.ToImmutableArray();
        if (recommendationId.Length == 0)
        {
            throw new ArgumentException("recommendation_id is required");
        }
        if (proposal.Length == 0)
        {
            throw new ArgumentException("proposal is required");
        }
        if (supporting.IsEmpty)
        {
            throw new ArgumentException("a recommendation must cite at least one supporting order");
        }
        if (deltas.IsEmpty)
        {
            throw new ArgumentException("a recommendation must report at least one expected delta");
        }
        if (!(confidence >= 0.0 && confidence <= 1.0))
        {
            throw new ArgumentException("confidence must be between 0 and 1");
        }
        if (bounds.IsEmpty || bounds.Any(bound => bound.Length == 0))
        {
            throw new ArgumentException("a recommendation must state at least one non-empty constraint");
        }
        if (rollbackPlan.Length == 0)
        {
            throw new ArgumentException("a recommendation must state its rollback plan");
        }
        RecommendationId = recommendationId;
        Proposal = proposal;
        SupportingOrderIds = supporting;
        ExpectedDeltas = deltas;
        Confidence = confidence;
        Constraints = bounds;
        RollbackPlan = rollbackPlan;
    }

    /// <summary>The recommendation's id, cited by its approval.</summary>
    public string RecommendationId { get; }

    /// <summary>What is proposed, in words a reviewer reads.</summary>
    public string Proposal { get; }

    /// <summary>The orders both arms answered, in corpus order: the paired cohort.</summary>
    public ImmutableArray<string> SupportingOrderIds { get; }

    /// <summary>One delta per metric both arms reported, sorted by metric name in code-point order.</summary>
    public ImmutableArray<ExpectedDelta> ExpectedDeltas { get; }

    /// <summary>The paired cohort's share of every order attempted.</summary>
    public double Confidence { get; }

    /// <summary>What bounds the proposal, such as the rules it must not break.</summary>
    public ImmutableArray<string> Constraints { get; }

    /// <summary>How to undo the change once it is approved.</summary>
    public string RollbackPlan { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(Recommendation? other) =>
        other is not null && RecommendationId == other.RecommendationId && Proposal == other.Proposal
        && Confidence.Equals(other.Confidence) && RollbackPlan == other.RollbackPlan
        && SupportingOrderIds.SequenceEqual(other.SupportingOrderIds) && ExpectedDeltas.SequenceEqual(other.ExpectedDeltas)
        && Constraints.SequenceEqual(other.Constraints);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(RecommendationId, Proposal, Confidence);
}

/// <summary>The immutable record of one approval: which recommendation, when, and the registry version publishing it produced.</summary>
public sealed record ApprovalRecord
{
    /// <summary>Refuses an empty id, a negative time and a non-positive version.</summary>
    public ApprovalRecord(string recommendationId, long approvedAt, int publishedVersion)
    {
        if (recommendationId.Length == 0)
        {
            throw new ArgumentException("recommendation_id is required");
        }
        if (approvedAt < 0)
        {
            throw new ArgumentException("approved_at cannot be negative");
        }
        if (publishedVersion <= 0)
        {
            throw new ArgumentException("published_version must be positive");
        }
        RecommendationId = recommendationId;
        ApprovedAt = approvedAt;
        PublishedVersion = publishedVersion;
    }

    /// <summary>The approved recommendation.</summary>
    public string RecommendationId { get; }

    /// <summary>When it was approved.</summary>
    public long ApprovedAt { get; }

    /// <summary>The registry version publishing it produced: the handle a rollback uses.</summary>
    public int PublishedVersion { get; }
}

/// <summary>
/// Carton-catalog and rule-change recommendations (<c>packvium.recommendations</c>). A proposal is
/// built from a paired baseline/treatment comparison and is withheld -- <c>null</c>, not an
/// exception and not a low number -- when the evidence is too thin. Publishing is a separate,
/// explicit act through <see cref="Approve"/>, <see cref="ApproveCatalog"/> or
/// <see cref="ApprovePolicy"/>; <see cref="Propose"/> is handed no registry.
/// </summary>
public static class Recommendations
{
    /// <summary>
    /// A recommendation from a baseline/treatment pair, or null when the paired cohort is smaller
    /// than <paramref name="minimumCohortSize"/>, its share of the corpus is below
    /// <paramref name="minimumConfidence"/>, or the arms share no metric. Both floors are required:
    /// what counts as enough evidence is a product decision. O(o * k) for o orders and k common metrics.
    /// </summary>
    public static Recommendation? Propose(string recommendationId, string proposal, ScenarioResult baseline,
        ScenarioResult treatment, IEnumerable<string> constraints, string rollbackPlan, int minimumCohortSize,
        double minimumConfidence)
    {
        Scenarios.RequireTheSameCorpus(baseline, treatment);
        if (minimumCohortSize <= 0)
        {
            throw new ArgumentException("minimum_cohort_size must be positive");
        }
        if (!(minimumConfidence >= 0.0 && minimumConfidence <= 1.0))
        {
            throw new ArgumentException("minimum_confidence must be between 0 and 1");
        }
        // A delta is a paired comparison. Averaging each arm's own survivors would let a difference
        // in which orders survived pass for a difference in performance.
        var paired = new SortedDictionary<string, (List<double> Baseline, List<double> Treatment)>(CodePointComparer.Instance);
        var supporting = new List<string>();
        foreach (var (baselineRun, treatmentRun) in baseline.Runs.Zip(treatment.Runs))
        {
            if (!baselineRun.Succeeded || !treatmentRun.Succeeded)
            {
                continue;
            }
            supporting.Add(baselineRun.OrderId);
            foreach (var (metric, value) in baselineRun.Metrics)
            {
                if (!treatmentRun.Metrics.TryGetValue(metric, out var treated))
                {
                    continue;
                }
                if (!paired.TryGetValue(metric, out var values))
                {
                    paired[metric] = values = ([], []);
                }
                values.Baseline.Add(value);
                values.Treatment.Add(treated);
            }
        }
        // Evidence size is the paired cohort, not the orders either arm attempted.
        var confidence = (double)supporting.Count / baseline.OrderIds.Length;
        if (supporting.Count < minimumCohortSize || confidence < minimumConfidence || paired.Count == 0)
        {
            return null;
        }
        var deltas = paired.Select(pair => new ExpectedDelta(pair.Key, Mean(pair.Value.Baseline), Mean(pair.Value.Treatment)));
        return new Recommendation(recommendationId, proposal, supporting, deltas, confidence, constraints, rollbackPlan);
    }

    /// <summary>
    /// The only path from a recommendation to a registry change: calls <paramref name="publish"/>
    /// exactly once and records the version it returns.
    /// </summary>
    public static ApprovalRecord Approve(Recommendation recommendation, long at, Func<int> publish) =>
        new(recommendation.RecommendationId, at, publish());

    /// <summary>Publishes an approved catalog proposal as a new version of the append-only registry.</summary>
    public static ApprovalRecord ApproveCatalog(Recommendation recommendation, CatalogRegistry registry,
        CatalogSnapshot snapshot, long approvedAt, long effectiveAt)
    {
        var version = registry.Publish(snapshot, effectiveAt, approvedAt,
            $"approved recommendation {recommendation.RecommendationId}");
        return new ApprovalRecord(recommendation.RecommendationId, approvedAt, version.Number);
    }

    /// <summary>Publishes an approved policy proposal as a new version of the rule in the append-only registry.</summary>
    public static ApprovalRecord ApprovePolicy(Recommendation recommendation, PolicyRegistry registry, string ruleId,
        string scope, string action, IEnumerable<PolicyPredicate> predicates, long priority, long approvedAt,
        long effectiveAt)
    {
        var rule = registry.Publish(ruleId, scope, action, [.. predicates], priority, effectiveAt,
            $"approved recommendation {recommendation.RecommendationId}");
        return new ApprovalRecord(recommendation.RecommendationId, approvedAt, rule.Version);
    }

    /// <summary>Left-to-right sum over the count, as <c>sum(values) / len(values)</c> adds them.</summary>
    private static double Mean(List<double> values)
    {
        var total = 0.0;
        foreach (var value in values)
        {
            total += value;
        }
        return total / values.Count;
    }
}
