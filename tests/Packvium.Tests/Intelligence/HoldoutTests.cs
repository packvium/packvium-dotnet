using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Intelligence;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>
/// Replaying a recommendation against history it has not seen. A packing the injected validator
/// rejects is that arm's failure at any cost -- its metrics never reach the comparison -- and a
/// recommendation may not be scored on the evidence that produced it.
/// </summary>
public sealed class HoldoutTests
{
    private static readonly ScenarioVersionPin BaselinePin = new(catalogVersion: 1);

    private static readonly ScenarioVersionPin TreatmentPin = new(catalogVersion: 2);

    private static Recommendation Recommendation(params string[] supporting) =>
        new("rec-1", "cheaper cartons", supporting.Length == 0 ? ["trained-1"] : supporting,
            [new ExpectedDelta("cost", 10, 8)], 1.0, ["no rejected placement"], "republish version 1");

    /// <summary>Events are <c>(decision, recorded_at)</c> shipments, or damage when a type is given.</summary>
    private static OutcomeLedger LedgerWith(params (string Decision, long At, OutcomeEventType Type)[] events)
    {
        var ledger = new OutcomeLedger();
        for (var index = 0; index < events.Length; index++)
        {
            var (decision, at, type) = events[index];
            var payload = type == OutcomeEventType.ActualCarton
                ? new Dictionary<string, JsonValue>(StringComparer.Ordinal) { ["carton_id"] = new JsonString("box-a"), ["catalog_version"] = new JsonInteger(1) }
                : new Dictionary<string, JsonValue>(StringComparer.Ordinal) { ["reason_code"] = new JsonString("crushed_corner"), ["severity"] = new JsonString("minor") };
            ledger.Record(new OutcomeEvent($"e{index + 1}", decision, type, payload, at));
        }
        return ledger;
    }

    private static (string Decision, long At, OutcomeEventType Type) Ship(string decision, long at) =>
        (decision, at, OutcomeEventType.ActualCarton);

    private sealed record Request(string DecisionId);

    private sealed record Result(string Arm);

    private static HoldoutEvaluation Evaluate(
        Dictionary<string, ((double, double) Baseline, (double, double) Treatment)> table, OutcomeLedger ledger,
        string[] decisionIds, (string Decision, string Arm)[]? invalid = null, long splitAt = 500, Recommendation? recommendation = null)
    {
        var rejected = (invalid ?? []).ToHashSet();
        return Holdout.EvaluateOnHistory(recommendation ?? Recommendation(), ledger, decisionIds, BaselinePin, TreatmentPin,
            (decisionId, pin) =>
            {
                var arm = pin == BaselinePin ? "baseline" : "treatment";
                var ((baseCost, baseUse), (treatCost, treatUse)) = table[decisionId];
                var (cost, utilisation) = arm == "baseline" ? (baseCost, baseUse) : (treatCost, treatUse);
                var run = new OrderRunResult(decisionId, succeeded: true, metrics: ParetoTests.Metrics(cost, utilisation));
                return new OrderEvaluationArtifact(run, new Request(decisionId), new Result(arm));
            },
            (request, result) => rejected.Contains((((Request)request).DecisionId, ((Result)result).Arm))
                ? new ValidationVerdict(valid: false, ["unsupported_item"])
                : new ValidationVerdict(valid: true),
            ParetoTests.Directions, splitAt);
    }

    private static Dictionary<string, ((double, double), (double, double))> Table(params (string Decision, (double, double) Baseline, (double, double) Treatment)[] rows) =>
        rows.ToDictionary(row => row.Decision, row => (row.Baseline, row.Treatment), StringComparer.Ordinal);

    private static readonly Dictionary<string, ((double, double), (double, double))> Empty = new(StringComparer.Ordinal);

    // ---------------------------------------------------------- the split is by recorded time

    [Fact]
    public void DecisionsRecordedBeforeTheSplitAreTraining()
    {
        var result = Evaluate(Table(("trained-1", (10, 0.7), (8, 0.8)), ("held-1", (10, 0.7), (8, 0.8))),
            LedgerWith(Ship("trained-1", 100), Ship("held-1", 900)), ["trained-1", "held-1"]);
        Assert.Equal(["trained-1"], result.TrainingDecisionIds);
        Assert.Equal(["held-1"], result.HoldoutDecisionIds);
    }

    [Fact]
    public void OnlyHeldOutDecisionsAreScored()
    {
        var result = Evaluate(Table(("trained-1", (10, 0.7), (8, 0.8)), ("held-1", (10, 0.7), (8, 0.8))),
            LedgerWith(Ship("trained-1", 100), Ship("held-1", 900)), ["trained-1", "held-1"]);
        Assert.Equal(["held-1"], result.Decisions.Select(decision => decision.DecisionId));
    }

    /// <summary>A later correction must not drag a decision across the split it was recorded on the other side of.</summary>
    [Fact]
    public void TheSplitUsesTheEarliestEventForADecision()
    {
        var ledger = LedgerWith(Ship("held-1", 100), ("held-1", 900, OutcomeEventType.Damage));
        var result = Evaluate(Table(("held-1", (10, 0.7), (8, 0.8))), ledger, ["held-1"], recommendation: Recommendation("other"));
        Assert.Equal(["held-1"], result.TrainingDecisionIds);
        Assert.Empty(result.Decisions);
    }

    /// <summary>It has no timestamp, so it cannot be placed on either side; defaulting it would be this module guessing.</summary>
    [Fact]
    public void ADecisionWithNoRecordedEventsIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Evaluate(Empty, new OutcomeLedger(), ["never-shipped"]));
        Assert.Equal("decision 'never-shipped' has no recorded events, so it cannot be split by time", refusal.Message);
    }

    [Fact]
    public void ANegativeSplitIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Evaluate(Empty, LedgerWith(Ship("held-1", 900)), ["held-1"], splitAt: -1));
        Assert.Contains("split_at cannot be negative", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty holdout would report zero regressions and read as a clean bill.</summary>
    [Fact]
    public void AnEmptyDecisionListIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Evaluate(Empty, new OutcomeLedger(), []));
        Assert.Contains("at least one decision id", refusal.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------ a proposal cannot be scored on its own evidence

    [Fact]
    public void ACitedDecisionInTheHoldoutIsRefusedByName()
    {
        var refusal = Assert.Throws<SupportingEvidenceInHoldoutException>(() =>
            Evaluate(Table(("trained-1", (10, 0.7), (8, 0.8))), LedgerWith(Ship("trained-1", 900)), ["trained-1"]));
        Assert.Equal("recommendation 'rec-1' cites held-out decision(s) ['trained-1']; it may not be scored on its own evidence",
            refusal.Message);
    }

    [Fact]
    public void TheSameDecisionOnTheTrainingSideIsFine()
    {
        var result = Evaluate(Table(("trained-1", (10, 0.7), (8, 0.8))), LedgerWith(Ship("trained-1", 100)), ["trained-1"]);
        Assert.Equal(["trained-1"], result.TrainingDecisionIds);
    }

    [Fact]
    public void TheRefusalIsAHoldoutError() =>
        Assert.True(typeof(SupportingEvidenceInHoldoutException).IsSubclassOf(typeof(HoldoutException)));

    // ------------------------------------------------------------- the verdict per decision

    private static DecisionOutcome Verdict((double, double) baseline, (double, double) treatment, params string[] invalidArms) =>
        Evaluate(Table(("held-1", baseline, treatment)), LedgerWith(Ship("held-1", 900)), ["held-1"],
            invalid: [.. invalidArms.Select(arm => ("held-1", arm))]).Decisions[0];

    [Fact]
    public void BetterOnEveryAxisIsAnImprovement() => Assert.Equal(HoldoutVerdicts.Improved, Verdict((10, 0.7), (8, 0.9)).Verdict);

    [Fact]
    public void WorseOnEveryAxisIsARegression() => Assert.Equal(HoldoutVerdicts.Regressed, Verdict((8, 0.9), (10, 0.7)).Verdict);

    [Fact]
    public void CheaperButSparserIsATradeOffRatherThanAWin() => Assert.Equal(HoldoutVerdicts.TradedOff, Verdict((10, 0.9), (8, 0.7)).Verdict);

    /// <summary>Neither dominates, so neither is named; that is the report saying "no difference".</summary>
    [Fact]
    public void IdenticalMetricsAreATradeOffRatherThanAWin() => Assert.Equal(HoldoutVerdicts.TradedOff, Verdict((10, 0.8), (10, 0.8)).Verdict);

    /// <summary>The treatment is cheaper and denser, and the validator refused it: its metrics are discarded, not discounted.</summary>
    [Fact]
    public void ARejectedTreatmentIsARegressionHoweverCheapItWas()
    {
        var outcome = Verdict((16, 0.6), (10, 0.9), "treatment");
        Assert.Equal(HoldoutVerdicts.Regressed, outcome.Verdict);
        Assert.False(outcome.TreatmentValid);
        Assert.Equal(["unsupported_item"], outcome.TreatmentCodes);
    }

    [Fact]
    public void ARejectedBaselineLeavesAValidTreatmentAnImprovement()
    {
        var outcome = Verdict((8, 0.9), (10, 0.7), "baseline");
        Assert.Equal(HoldoutVerdicts.Improved, outcome.Verdict);
        Assert.False(outcome.BaselineValid);
    }

    [Fact]
    public void BothArmsRejectedIsUnpackableRatherThanATie() =>
        Assert.Equal(HoldoutVerdicts.Unpackable, Verdict((10, 0.7), (8, 0.9), "baseline", "treatment").Verdict);

    [Fact]
    public void AnImprovementTheValidatorRejectedCannotBeRecorded()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new DecisionOutcome("d1", HoldoutVerdicts.Improved, true, false));
        Assert.Contains("can never be recorded as an improvement", refusal.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------ realised risk is reported against the arm that shipped

    /// <summary>Damage happened under the carton that actually shipped; crediting the treatment would score a counterfactual nobody ran.</summary>
    [Fact]
    public void RecordedDamageIsAttachedToTheBaseline()
    {
        var ledger = LedgerWith(Ship("held-1", 900), ("held-1", 950, OutcomeEventType.Damage));
        var result = Evaluate(Table(("held-1", (10, 0.7), (8, 0.9))), ledger, ["held-1"]);
        Assert.Equal(["damage"], result.Decisions[0].BaselineRealisedRisk);
    }

    [Fact]
    public void ACleanHistoryCarriesNoRealisedRisk()
    {
        var result = Evaluate(Table(("held-1", (10, 0.7), (8, 0.9))), LedgerWith(Ship("held-1", 900)), ["held-1"]);
        Assert.Empty(result.Decisions[0].BaselineRealisedRisk);
    }

    // --------------------------------------------------------------------------- the tally

    [Fact]
    public void EachVerdictIsCountedOnce()
    {
        var result = Evaluate(Table(("up", (10, 0.7), (8, 0.9)), ("down", (8, 0.9), (10, 0.7)), ("sideways", (10, 0.9), (8, 0.7))),
            LedgerWith(Ship("up", 900), Ship("down", 910), Ship("sideways", 920)), ["up", "down", "sideways"]);
        Assert.Equal((1, 1, 1, 0), (result.Improved, result.Regressed, result.TradedOff, result.Unpackable));
    }

    [Fact]
    public void TheEvaluationRecordsBothPinsItCompared()
    {
        var result = Evaluate(Table(("held-1", (10, 0.7), (8, 0.9))), LedgerWith(Ship("held-1", 900)), ["held-1"]);
        Assert.Equal(BaselinePin, result.BaselinePin);
        Assert.Equal(TreatmentPin, result.TreatmentPin);
        Assert.Equal(500, result.SplitAt);
    }

    // ------------------------------------------------ the artifact carries what the validator reads

    private static OrderRunResult Run() =>
        new("d1", succeeded: true, metrics: new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 1.0 });

    [Fact]
    public void ACompleteArtifactIsAccepted() => Assert.Equal("d1", new OrderEvaluationArtifact(Run(), new Request("d1"), new Result("b")).Run.OrderId);

    /// <summary>A run summary alone cannot be independently validated: the validator is handed the pair so no score can influence it.</summary>
    [Theory]
    [InlineData("request")]
    [InlineData("result")]
    public void AnArtifactWithoutThePairIsRefused(string missing)
    {
        var refusal = Assert.Throws<ArgumentException>(() => new OrderEvaluationArtifact(Run(),
            missing == "request" ? null : new Request("d1"), missing == "result" ? null : new Result("b")));
        Assert.Contains("request/result pair", refusal.Message, StringComparison.Ordinal);
    }
}
