using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Intelligence;
using Packvium.Revisions;
using Packvium.Serialization;
using Packvium.Tests.Packing;
using Packvium.Tests.Revisions;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>Approved revisions feed historical evaluation without touching a plan (<c>test_revision_outcomes.py</c>).</summary>
public sealed class RevisionOutcomesTests
{
    private static readonly ScenarioVersionPin BaselinePin = new(catalogVersion: 1);

    private static readonly ScenarioVersionPin TreatmentPin = new(catalogVersion: 2);

    [Fact]
    public void EachExceptionBecomesOneLedgerEventNamedByItsRevision()
    {
        var (revisions, _) = RevisionTests.Chain();
        var events = RevisionOutcomes.Events(revisions[1], "order-1", 500);
        var digest = PlanRevision.Digest(revisions[1]);
        Assert.Equal([$"order-1@{digest}#1", $"order-1@{digest}#2"], events.Select(outcome => outcome.EventId));
        Assert.Equal([OutcomeEventType.OperatorOverride, OutcomeEventType.ItemMissing], events.Select(outcome => outcome.EventType));
        Assert.Equal(new Dictionary<string, JsonValue> { ["item_type"] = new JsonString("cube"), ["quantity"] = new JsonInteger(1) },
            events[1].Payload);
    }

    [Fact]
    public void TheRootRevisionRecordsNothing() =>
        Assert.Empty(RevisionOutcomes.Events(RevisionTests.Chain().Revisions[0], "order-1", 500));

    [Fact]
    public void ADocumentThatIsNotARevisionIsRefused() =>
        Assert.Throws<PlanRevisionException>(() => RevisionOutcomes.Events(RevisionTests.J("""{"events": []}"""), "order-1", 500));

    [Fact]
    public void RecordingARevisionTwiceChangesNothing()
    {
        var (revisions, _) = RevisionTests.Chain();
        var ledger = new OutcomeLedger();
        var first = RevisionOutcomes.Record(ledger, revisions[2], "order-1", 500);
        var again = RevisionOutcomes.Record(ledger, revisions[2], "order-1", 500);
        Assert.Equal(first, again);
        Assert.Single(ledger.EventsForDecision("order-1"));
    }

    [Fact]
    public void RecordingNeverChangesTheRevisionOrItsPlan()
    {
        var (revisions, _) = RevisionTests.Chain();
        var before = revisions.Select(revision => CanonicalJsonWriter.Write(revision)).ToList();
        RevisionOutcomes.Record(new OutcomeLedger(), revisions[1], "order-1", 500);
        Assert.Equal(before, revisions.Select(revision => CanonicalJsonWriter.Write(revision)));
    }

    [Fact]
    public void ALockedPlacementIsReportedAgainstTheBaselineInHoldout()
    {
        var (revisions, _) = RevisionTests.Chain();
        var ledger = new OutcomeLedger();
        RevisionOutcomes.Record(ledger, revisions[1], "trained-1", 100);
        RevisionOutcomes.Record(ledger, revisions[1], "held-1", 900);
        RevisionOutcomes.Record(ledger, revisions[2], "held-1", 950);
        var request = revisions[^1].Require("request");
        var recommendation = new Recommendation("rec-1", "cheaper cartons", ["trained-1"], [new ExpectedDelta("cost", 10, 8)],
            1.0, ["no rejected placement"], "republish");
        var evaluation = Holdout.EvaluateOnHistory(recommendation, ledger, ["trained-1", "held-1"], BaselinePin, TreatmentPin,
            (decisionId, pin) => new OrderEvaluationArtifact(
                new OrderRunResult(decisionId, succeeded: true,
                    metrics: new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = pin == BaselinePin ? 10 : 8 }),
                request, JsonApi.PackFromJson(request)),
            (_, result) => new ValidationVerdict(valid: ((JsonObject)result).Get("warnings") is not JsonArray { Items.IsEmpty: false }),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["cost"] = false }, 800);
        Assert.Equal(["trained-1"], evaluation.TrainingDecisionIds);
        var decision = Assert.Single(evaluation.Decisions);
        Assert.Equal("improved", decision.Verdict);
        Assert.Equal(["operator_override"], decision.BaselineRealisedRisk);
    }

    [Fact]
    public void TheLedgerCannotChangeAPacking()
    {
        var request = RevisionTests.Chain().Revisions[^1].Require("request");
        var before = JsonApi.PackFromJson(request);
        RevisionOutcomes.Record(new OutcomeLedger(), RevisionTests.Chain().Revisions[1], "order-1", 500);
        Assert.Equal(FixedPlacementTests.WithoutDurations(before), FixedPlacementTests.WithoutDurations(JsonApi.PackFromJson(request)));
    }
}
