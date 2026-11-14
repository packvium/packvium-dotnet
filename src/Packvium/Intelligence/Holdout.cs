using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Intelligence;

/// <summary>Base class for every holdout error.</summary>
public class HoldoutException(string message) : Exception(message);

/// <summary>
/// The recommendation cites a decision the split holds out. Refused rather than scored: a
/// recommendation evaluated on its own evidence measures how well it remembers.
/// </summary>
public sealed class SupportingEvidenceInHoldoutException(string message) : HoldoutException(message);

/// <summary>The per-decision verdicts of a holdout evaluation.</summary>
public static class HoldoutVerdicts
{
    /// <summary>The treatment dominated the baseline, or packed what the baseline could not.</summary>
    public const string Improved = "improved";
    /// <summary>The baseline dominated, or the treatment failed or was rejected.</summary>
    public const string Regressed = "regressed";
    /// <summary>Both arms are on the frontier: a real trade-off, or no difference.</summary>
    public const string TradedOff = "traded_off";
    /// <summary>Neither arm produced a packing the validator accepts.</summary>
    public const string Unpackable = "unpackable";
}

/// <summary>
/// An independent validator's answer about one packing. It carries no score and no metric, and
/// the callback that produces it is never handed one, which keeps a learned number from reaching it.
/// </summary>
public sealed record ValidationVerdict
{
    /// <summary>Refuses a valid verdict with codes and a rejection without them.</summary>
    public ValidationVerdict(bool valid, IEnumerable<string>? codes = null)
    {
        var all = (codes ?? []).ToImmutableArray();
        if (valid && !all.IsEmpty)
        {
            throw new ArgumentException("a valid verdict cannot carry rejection codes");
        }
        if (!valid && all.IsEmpty)
        {
            throw new ArgumentException("a rejection must say which rules it failed");
        }
        Valid = valid;
        Codes = all;
    }

    /// <summary>Whether the packing passed every rule the validator checks.</summary>
    public bool Valid { get; }

    /// <summary>The rules the packing failed, as the validator reported them.</summary>
    public ImmutableArray<string> Codes { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(ValidationVerdict? other) => other is not null && Valid == other.Valid && Codes.SequenceEqual(other.Codes);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Valid, Codes.Length);
}

/// <summary>
/// One arm's run of one decision, plus the request and result an independent check reads. A run
/// summary alone cannot be validated, so the pair is required.
/// </summary>
public sealed record OrderEvaluationArtifact
{
    /// <summary>Refuses an artifact without its request or its result.</summary>
    public OrderEvaluationArtifact(OrderRunResult run, object? request, object? result)
    {
        if (request is null || result is null)
        {
            throw new ArgumentException(
                "a holdout artifact must carry the request/result pair the validator reads; "
                + "a run summary alone cannot be independently validated");
        }
        Run = run;
        Request = request;
        Result = result;
    }

    /// <summary>The arm's run summary: success and metrics.</summary>
    public OrderRunResult Run { get; }

    /// <summary>The request the arm packed, handed to the validator.</summary>
    public object Request { get; }

    /// <summary>The packing the arm produced, handed to the validator.</summary>
    public object Result { get; }
}

/// <summary><c>(decision_id, pin) -&gt; OrderEvaluationArtifact</c>: re-packs one historical decision under one pin.</summary>
public delegate OrderEvaluationArtifact HoldoutEvaluator(string decisionId, ScenarioVersionPin versionPin);

/// <summary>
/// <c>(request, result) -&gt; ValidationVerdict</c>. Separate from the evaluator on purpose: an
/// engine that graded its own homework would make the gate a comment.
/// </summary>
public delegate ValidationVerdict IndependentValidator(object request, object result);

/// <summary>
/// What the replay found for one held-out decision, retained rather than summarised: the verdict,
/// the validator codes behind it and the realised-risk events the baseline actually incurred.
/// </summary>
public sealed record DecisionOutcome
{
    /// <summary>Refuses an unknown verdict, and an improvement whose treatment the validator rejected.</summary>
    public DecisionOutcome(string decisionId, string verdict, bool baselineValid, bool treatmentValid,
        IEnumerable<string>? baselineCodes = null, IEnumerable<string>? treatmentCodes = null,
        IEnumerable<string>? baselineRealisedRisk = null)
    {
        if (decisionId.Length == 0)
        {
            throw new ArgumentException("decision_id is required");
        }
        if (verdict is not (HoldoutVerdicts.Improved or HoldoutVerdicts.Regressed or HoldoutVerdicts.TradedOff
            or HoldoutVerdicts.Unpackable))
        {
            throw new ArgumentException($"unknown verdict {PythonText.Repr(verdict)}");
        }
        if (verdict == HoldoutVerdicts.Improved && !treatmentValid)
        {
            throw new ArgumentException("a packing the validator rejected can never be recorded as an improvement");
        }
        DecisionId = decisionId;
        Verdict = verdict;
        BaselineValid = baselineValid;
        TreatmentValid = treatmentValid;
        BaselineCodes = (baselineCodes ?? []).ToImmutableArray();
        TreatmentCodes = (treatmentCodes ?? []).ToImmutableArray();
        BaselineRealisedRisk = (baselineRealisedRisk ?? []).ToImmutableArray();
    }

    /// <summary>The held-out decision.</summary>
    public string DecisionId { get; }

    /// <summary>One of <see cref="HoldoutVerdicts"/>.</summary>
    public string Verdict { get; }

    /// <summary>Whether the validator accepted the baseline's packing.</summary>
    public bool BaselineValid { get; }

    /// <summary>Whether the validator accepted the treatment's packing.</summary>
    public bool TreatmentValid { get; }

    /// <summary>The validator's codes for the baseline; empty when it was accepted.</summary>
    public ImmutableArray<string> BaselineCodes { get; }

    /// <summary>The validator's codes for the treatment; empty when it was accepted.</summary>
    public ImmutableArray<string> TreatmentCodes { get; }

    /// <summary>The baseline's recorded damage, returns, repacks and overrides, as event-type names in code-point order. Negative evidence only.</summary>
    public ImmutableArray<string> BaselineRealisedRisk { get; }

    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(DecisionOutcome? other) =>
        other is not null && DecisionId == other.DecisionId && Verdict == other.Verdict
        && BaselineValid == other.BaselineValid && TreatmentValid == other.TreatmentValid
        && BaselineCodes.SequenceEqual(other.BaselineCodes) && TreatmentCodes.SequenceEqual(other.TreatmentCodes)
        && BaselineRealisedRisk.SequenceEqual(other.BaselineRealisedRisk);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DecisionId, Verdict, BaselineValid, TreatmentValid);
}

/// <summary>
/// The complete record of one holdout evaluation -- the split, both pins, both sides of the split
/// and every per-decision verdict -- so the run can be argued with rather than merely believed.
/// The aggregate is a count of decisions, never a mean delta a few large orders could carry.
/// </summary>
/// <param name="RecommendationId">The recommendation scored.</param>
/// <param name="SplitAt">The instant that divides training from holdout.</param>
/// <param name="BaselinePin">The versions the baseline arm ran against.</param>
/// <param name="TreatmentPin">The versions the treatment arm ran against.</param>
/// <param name="TrainingDecisionIds">Decisions first recorded before the split, in the order given.</param>
/// <param name="HoldoutDecisionIds">Decisions first recorded at or after it, in the order given.</param>
/// <param name="Decisions">One outcome per held-out decision.</param>
public sealed record HoldoutEvaluation(
    string RecommendationId,
    long SplitAt,
    ScenarioVersionPin BaselinePin,
    ScenarioVersionPin TreatmentPin,
    ImmutableArray<string> TrainingDecisionIds,
    ImmutableArray<string> HoldoutDecisionIds,
    ImmutableArray<DecisionOutcome> Decisions)
{
    /// <summary>Held-out decisions the treatment improved.</summary>
    public int Improved => Count(HoldoutVerdicts.Improved);

    /// <summary>Held-out decisions the treatment made worse, rejected packings included.</summary>
    public int Regressed => Count(HoldoutVerdicts.Regressed);

    /// <summary>Held-out decisions where neither arm dominated.</summary>
    public int TradedOff => Count(HoldoutVerdicts.TradedOff);

    /// <summary>Held-out decisions neither arm packed validly.</summary>
    public int Unpackable => Count(HoldoutVerdicts.Unpackable);

    private int Count(string verdict) => Decisions.Count(decision => decision.Verdict == verdict);
}

/// <summary>
/// Historical replay and holdout evaluation for a recommendation (<c>packvium.holdout</c>): would
/// it have improved past decisions, scored on a slice it was not derived from.
///
/// The split is by time; a recommendation that cites a held-out decision is refused. Only what is
/// deterministically recomputable is compared: damage, returns, repacks and overrides happened
/// under the carton that shipped and are reported against the baseline, never credited to the
/// treatment. The validator runs before any metric is compared, and an arm it rejects has failed
/// for that decision whatever it cost.
/// </summary>
public static class Holdout
{
    /// <summary>
    /// Ledger facts about what a human or the world did to the carton that actually shipped. Under
    /// a different carton they may not have happened at all.
    /// </summary>
    public static ImmutableHashSet<OutcomeEventType> NotCounterfactual { get; } =
    [
        OutcomeEventType.Damage, OutcomeEventType.Return, OutcomeEventType.Repack, OutcomeEventType.OperatorOverride,
    ];

    /// <summary>
    /// Scores <paramref name="recommendation"/> against the decisions <paramref name="splitAt"/>
    /// holds out: a decision is held out when its earliest recorded event is at or after the split.
    /// <paramref name="splitAt"/> has no default, because how much history is enough is a claim
    /// about a business this library cannot see. Throws
    /// <see cref="SupportingEvidenceInHoldoutException"/> when the recommendation cites a held-out
    /// decision, and <see cref="ArgumentException"/> when a decision has no recorded events.
    /// </summary>
    public static HoldoutEvaluation EvaluateOnHistory(Recommendation recommendation, OutcomeLedger ledger,
        IEnumerable<string> decisionIds, ScenarioVersionPin baselinePin, ScenarioVersionPin treatmentPin,
        HoldoutEvaluator evaluator, IndependentValidator validator, IReadOnlyDictionary<string, bool> higherIsBetter,
        long splitAt)
    {
        var decisions = decisionIds.ToList();
        if (splitAt < 0)
        {
            throw new ArgumentException("split_at cannot be negative");
        }
        if (decisions.Count == 0)
        {
            throw new ArgumentException("at least one decision id is required");
        }
        var training = new List<string>();
        var holdout = new List<string>();
        foreach (var decisionId in decisions)
        {
            var events = ledger.EventsForDecision(decisionId);
            if (events.IsEmpty)
            {
                throw new ArgumentException(
                    $"decision {PythonText.Repr(decisionId)} has no recorded events, so it cannot be split by time");
            }
            (events.Min(outcomeEvent => outcomeEvent.RecordedAt) >= splitAt ? holdout : training).Add(decisionId);
        }
        var cited = recommendation.SupportingOrderIds.Intersect(holdout, StringComparer.Ordinal)
            .Order(CodePointComparer.Instance).ToList();
        if (cited.Count > 0)
        {
            throw new SupportingEvidenceInHoldoutException(
                $"recommendation {PythonText.Repr(recommendation.RecommendationId)} cites held-out "
                + $"decision(s) {PythonText.ReprList(cited)}; it may not be scored on its own evidence");
        }
        var outcomes = holdout.Select(decisionId =>
        {
            var (baselineRun, baselineVerdict) = Arm(decisionId, baselinePin, evaluator, validator);
            var (treatmentRun, treatmentVerdict) = Arm(decisionId, treatmentPin, evaluator, validator);
            return new DecisionOutcome(decisionId,
                VerdictFor(decisionId, baselineRun, treatmentRun, baselinePin, treatmentPin, higherIsBetter),
                baselineVerdict.Valid, treatmentVerdict.Valid, baselineVerdict.Codes, treatmentVerdict.Codes,
                RealisedRisk(ledger, decisionId));
        }).ToImmutableArray();
        return new HoldoutEvaluation(recommendation.RecommendationId, splitAt, baselinePin, treatmentPin,
            [.. training], [.. holdout], outcomes);
    }

    /// <summary>The baseline's realised bad outcomes, as recorded. Negative evidence only.</summary>
    private static IEnumerable<string> RealisedRisk(OutcomeLedger ledger, string decisionId) =>
        ledger.CurrentView(decisionId).Where(outcomeEvent => NotCounterfactual.Contains(outcomeEvent.EventType))
            .Select(outcomeEvent => outcomeEvent.EventType.Value()).Distinct(StringComparer.Ordinal)
            .Order(CodePointComparer.Instance);

    /// <summary>Runs one arm and validates it; the verdict is produced before any metric is read.</summary>
    private static (OrderRunResult Run, ValidationVerdict Verdict) Arm(string decisionId, ScenarioVersionPin pin,
        HoldoutEvaluator evaluator, IndependentValidator validator)
    {
        var artifact = evaluator(decisionId, pin);
        var verdict = validator(artifact.Request, artifact.Result);
        if (verdict.Valid)
        {
            return (artifact.Run, verdict);
        }
        // Discarded rather than down-weighted: there is no cost at which a rejected placement competes.
        return (new OrderRunResult(decisionId, succeeded: false,
            failureReason: "independent validation rejected the placement: " + string.Join(", ", verdict.Codes)), verdict);
    }

    private static string VerdictFor(string decisionId, OrderRunResult baselineRun, OrderRunResult treatmentRun,
        ScenarioVersionPin baselinePin, ScenarioVersionPin treatmentPin, IReadOnlyDictionary<string, bool> higherIsBetter)
    {
        if (!baselineRun.Succeeded && !treatmentRun.Succeeded)
        {
            return HoldoutVerdicts.Unpackable;
        }
        if (!treatmentRun.Succeeded)
        {
            return HoldoutVerdicts.Regressed;
        }
        if (!baselineRun.Succeeded)
        {
            // The treatment packed what the baseline could not, and its packing passed the validator.
            return HoldoutVerdicts.Improved;
        }
        // Both arms are valid: the same per-decision Pareto report the forward path uses.
        var report = Scenarios.CompareScenarios(
            new ScenarioResult("baseline", baselinePin, [decisionId], [baselineRun]),
            new ScenarioResult("treatment", treatmentPin, [decisionId], [treatmentRun]),
            higherIsBetter)[0];
        return report.Winner switch
        {
            "treatment" => HoldoutVerdicts.Improved,
            "baseline" => HoldoutVerdicts.Regressed,
            _ => HoldoutVerdicts.TradedOff,
        };
    }
}
