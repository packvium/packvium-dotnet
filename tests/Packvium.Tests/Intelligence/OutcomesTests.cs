using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Intelligence;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>
/// The append-only outcome ledger. The distinction worth reading twice is <c>ViewAsOf</c> against
/// <c>CurrentView</c>: both fold the same immutable events, and only one of them is safe to
/// reconstruct a past belief with.
/// </summary>
public sealed class OutcomesTests
{
    internal static OutcomeEvent Shipped(string eventId, string decisionId, long at, string carton = "box-a", string? supersedes = null) =>
        new(eventId, decisionId, OutcomeEventType.ActualCarton,
            new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["carton_id"] = new JsonString(carton),
                ["catalog_version"] = new JsonInteger(1),
            },
            at, supersedes);

    private static string[] Ids(IEnumerable<OutcomeEvent> events) => [.. events.Select(outcomeEvent => outcomeEvent.EventId)];

    // ----------------------------------------------- recording is append-only and idempotent

    [Fact]
    public void ARecordedEventComesBackForItsDecision()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100));
        Assert.Equal(["e1"], Ids(ledger.EventsForDecision("d1")));
    }

    [Fact]
    public void AnUnknownDecisionHasAnEmptyHistoryRatherThanThrowing() =>
        Assert.Empty(new OutcomeLedger().EventsForDecision("never-shipped"));

    /// <summary>Duplicate delivery is a fact of every queue: repeating an event must neither double it nor fail.</summary>
    [Fact]
    public void RecordingTheIdenticalEventTwiceIsANoOp()
    {
        var ledger = new OutcomeLedger();
        var first = ledger.Record(Shipped("e1", "d1", 100));
        var again = ledger.Record(Shipped("e1", "d1", 100));
        Assert.Same(first, again);
        Assert.Single(ledger.EventsForDecision("d1"));
    }

    /// <summary>A duplicate delivery repeats an event; it does not carry a different one wearing the same id.</summary>
    [Fact]
    public void ReusingAnEventIdForDifferentContentIsRefused()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100, carton: "box-a"));
        var refusal = Assert.Throws<DuplicateEventMismatchException>(() => ledger.Record(Shipped("e1", "d1", 100, carton: "box-b")));
        Assert.Equal("event id 'e1' was already recorded with different content", refusal.Message);
    }

    [Fact]
    public void EventsComeBackInRecordedOrder()
    {
        var ledger = new OutcomeLedger();
        long[] times = [300, 100, 200];
        for (var index = 0; index < times.Length; index++)
        {
            ledger.Record(Shipped($"e{index + 1}", "d1", times[index]));
        }
        Assert.Equal(["e1", "e2", "e3"], Ids(ledger.EventsForDecision("d1")));
    }

    [Fact]
    public void DecisionsAreKeptApart()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100));
        ledger.Record(Shipped("e2", "d2", 100));
        Assert.Equal(["e1"], Ids(ledger.EventsForDecision("d1")));
        Assert.Equal(["e2"], Ids(ledger.EventsForDecision("d2")));
    }

    // ----------------------------------------- a correction supersedes rather than overwrites

    private static OutcomeLedger Corrected()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100, carton: "box-a"));
        ledger.Record(Shipped("e2", "d1", 200, carton: "box-b", supersedes: "e1"));
        return ledger;
    }

    [Fact]
    public void TheCorrectionReplacesTheOriginalInTheCurrentView() => Assert.Equal(["e2"], Ids(Corrected().CurrentView("d1")));

    /// <summary>"Append-only" is the whole claim: a correction that erased what it corrected would make the ledger mutable.</summary>
    [Fact]
    public void TheOriginalIsStillInTheUnfoldedHistory() => Assert.Equal(["e1", "e2"], Ids(Corrected().EventsForDecision("d1")));

    [Fact]
    public void SupersedingAnEventTheLedgerHasNeverSeenIsRefused()
    {
        var refusal = Assert.Throws<OutcomeEventNotFoundException>(() =>
            new OutcomeLedger().Record(Shipped("e2", "d1", 200, supersedes: "ghost")));
        Assert.Equal("event 'e2' supersedes unknown event 'ghost'", refusal.Message);
    }

    [Fact]
    public void AnEventCannotSupersedeItself()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Shipped("e1", "d1", 100, supersedes: "e1"));
        Assert.Contains("cannot supersede itself", refusal.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------- time travel is narrower than the current view

    private static OutcomeLedger Ledger()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100, carton: "box-a"));
        ledger.Record(Shipped("e2", "d1", 300, carton: "box-b", supersedes: "e1"));
        return ledger;
    }

    [Fact]
    public void BeforeAnythingWasRecordedTheViewIsEmpty() => Assert.Empty(Ledger().ViewAsOf("d1", 50));

    [Fact]
    public void BetweenTheEventAndItsCorrectionTheOriginalStillStands()
    {
        var view = Ledger().ViewAsOf("d1", 200);
        Assert.Equal(["e1"], Ids(view));
        Assert.Equal(new JsonString("box-a"), view[0].Payload["carton_id"]);
    }

    [Fact]
    public void AfterTheCorrectionTheCorrectionApplies() => Assert.Equal(["e2"], Ids(Ledger().ViewAsOf("d1", 400)));

    /// <summary>An event recorded at the very instant the question is asked is not yet part of what was believed.</summary>
    [Fact]
    public void TheBoundaryIsStrictlyBefore() => Assert.Empty(Ledger().ViewAsOf("d1", 100));

    [Fact]
    public void TheCurrentViewDisagreesWithThePastOneOnPurpose()
    {
        var ledger = Ledger();
        Assert.Equal(["e1"], Ids(ledger.ViewAsOf("d1", 200)));
        Assert.Equal(["e2"], Ids(ledger.CurrentView("d1")));
    }

    [Fact]
    public void ANegativeInstantIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Ledger().ViewAsOf("d1", -1));
        Assert.Contains("as-of time cannot be negative", refusal.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------- several event kinds coexist on one decision

    [Fact]
    public void AShipmentAndItsDamageBothStand()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100));
        ledger.Record(new OutcomeEvent("e2", "d1", OutcomeEventType.Damage,
            new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["reason_code"] = new JsonString("crushed_corner"),
                ["severity"] = new JsonString("minor"),
            }, 200));
        Assert.Equal(["actual_carton", "damage"], ledger.CurrentView("d1").Select(outcomeEvent => outcomeEvent.EventType.Value()).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------- an event must name its decision

    /// <summary>The decision id is the only index the ledger has; an event without one could never be read back.</summary>
    [Fact]
    public void AnEventWithoutADecisionIdIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => Shipped("e1", "", 100));
        Assert.Contains("decision_id is required", refusal.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the closed payload vocabulary

    /// <summary>The ledger's guard against free text: a field it does not know is refused, never stored.</summary>
    [Fact]
    public void AFieldOutsideTheVocabularyIsRefusedWithBothListsNamed()
    {
        var refusal = Assert.Throws<UnsupportedOutcomeFieldException>(() => new OutcomeEvent("e1", "d1", OutcomeEventType.Damage,
            new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["note"] = new JsonString("call Ms Smith"),
                ["carton_id"] = new JsonString("box-a"),
            }, 100));
        Assert.Equal("event type 'damage' does not support field(s) ['carton_id', 'note']; allowed: ['reason_code', 'severity']",
            refusal.Message);
    }

    /// <summary>Python's equality: <c>1 == 1.0</c>, so a redelivery that spells a number differently is still the same event.</summary>
    [Fact]
    public void PayloadsCompareAsPythonComparesThem()
    {
        var ledger = new OutcomeLedger();
        ledger.Record(Shipped("e1", "d1", 100));
        var respelled = new OutcomeEvent("e1", "d1", OutcomeEventType.ActualCarton,
            new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["catalog_version"] = new JsonFloat(1.0),
                ["carton_id"] = new JsonString("box-a"),
            }, 100);
        Assert.Single(ledger.EventsForDecision("d1"));
        Assert.NotNull(ledger.Record(respelled));
        Assert.Single(ledger.EventsForDecision("d1"));
    }
}
