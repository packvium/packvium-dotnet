using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Commerce;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Intelligence;

/// <summary>Base class for every outcome-ledger error.</summary>
public class OutcomeException(string message) : Exception(message);

/// <summary>
/// A payload field outside the closed vocabulary of its event type, refused at construction. This
/// is the ledger's guard against free text: a field it does not know -- a note that might carry a
/// name or an address -- can never be stored.
/// </summary>
public sealed class UnsupportedOutcomeFieldException(string message) : OutcomeException(message);

/// <summary>The same event id recorded again with different content.</summary>
public sealed class DuplicateEventMismatchException(string message) : OutcomeException(message);

/// <summary>No event is recorded under the given event id.</summary>
public sealed class OutcomeEventNotFoundException(string message) : OutcomeException(message);

/// <summary>What one outcome event records about a decision.</summary>
public enum OutcomeEventType
{
    /// <summary>Which ranked alternative was actually used.</summary>
    AlternativeChosen,
    /// <summary>The carton that really shipped.</summary>
    ActualCarton,
    /// <summary>Dimensions measured at the dock.</summary>
    MeasuredDimensions,
    /// <summary>Weight measured at the dock.</summary>
    MeasuredWeight,
    /// <summary>The shipment was repacked.</summary>
    Repack,
    /// <summary>The shipment arrived damaged.</summary>
    Damage,
    /// <summary>The shipment was returned.</summary>
    Return,
    /// <summary>An operator overrode the decision.</summary>
    OperatorOverride,
}

/// <summary>The wire spellings of <see cref="OutcomeEventType"/> and the payload fields each one accepts.</summary>
public static class OutcomeEventTypes
{
    /// <summary>
    /// The closed, named payload vocabulary per event type: ids, numeric measurements and
    /// enumerated reason codes, and no free-text field.
    /// </summary>
    public static IReadOnlyDictionary<OutcomeEventType, ImmutableSortedSet<string>> AllowedFields { get; } =
        new Dictionary<OutcomeEventType, ImmutableSortedSet<string>>
        {
            [OutcomeEventType.AlternativeChosen] = Fields("alternative_index", "objective_score"),
            [OutcomeEventType.ActualCarton] = Fields("carton_id", "catalog_version"),
            [OutcomeEventType.MeasuredDimensions] = Fields("length_mm", "width_mm", "height_mm"),
            [OutcomeEventType.MeasuredWeight] = Fields("weight_g"),
            [OutcomeEventType.Repack] = Fields("reason_code", "new_carton_id"),
            [OutcomeEventType.Damage] = Fields("reason_code", "severity"),
            [OutcomeEventType.Return] = Fields("reason_code"),
            [OutcomeEventType.OperatorOverride] = Fields("reason_code", "operator_id"),
        }.ToImmutableDictionary();

    /// <summary>The wire spelling, such as <c>actual_carton</c>.</summary>
    public static string Value(this OutcomeEventType type) => type switch
    {
        OutcomeEventType.AlternativeChosen => "alternative_chosen",
        OutcomeEventType.ActualCarton => "actual_carton",
        OutcomeEventType.MeasuredDimensions => "measured_dimensions",
        OutcomeEventType.MeasuredWeight => "measured_weight",
        OutcomeEventType.Repack => "repack",
        OutcomeEventType.Damage => "damage",
        OutcomeEventType.Return => "return",
        OutcomeEventType.OperatorOverride => "operator_override",
        _ => throw new UnsupportedOutcomeFieldException($"unsupported event type {(int)type}"),
    };

    internal static void ValidatePayload(OutcomeEventType type, IEnumerable<string> fields)
    {
        if (!AllowedFields.TryGetValue(type, out var allowed))
        {
            throw new UnsupportedOutcomeFieldException($"unsupported event type {(int)type}");
        }
        var unsupported = fields.Where(field => !allowed.Contains(field)).Order(CodePointComparer.Instance).ToList();
        if (unsupported.Count > 0)
        {
            throw new UnsupportedOutcomeFieldException(
                $"event type {PythonText.Repr(type.Value())} does not support field(s) {PythonText.ReprList(unsupported)}; "
                + $"allowed: {PythonText.ReprList(allowed)}");
        }
    }

    private static ImmutableSortedSet<string> Fields(params string[] names) =>
        ImmutableSortedSet.Create(CodePointComparer.Instance, names);
}

/// <summary>
/// One immutable fact about one decision. A correction is never an edit: it is a new event whose
/// <see cref="Supersedes"/> names the event it corrects.
/// </summary>
public sealed record OutcomeEvent
{
    /// <summary>Refuses an empty id or decision, a negative time, a self-correction and any payload field outside the event type's vocabulary.</summary>
    public OutcomeEvent(string eventId, string decisionId, OutcomeEventType eventType,
        IReadOnlyDictionary<string, JsonValue> payload, long recordedAt, string? supersedes = null)
    {
        if (eventId.Length == 0)
        {
            throw new ArgumentException("event_id is required");
        }
        if (decisionId.Length == 0)
        {
            throw new ArgumentException("decision_id is required");
        }
        if (recordedAt < 0)
        {
            throw new ArgumentException("recorded_at cannot be negative");
        }
        if (string.Equals(supersedes, eventId, StringComparison.Ordinal))
        {
            throw new ArgumentException("an event cannot supersede itself");
        }
        OutcomeEventTypes.ValidatePayload(eventType, payload.Keys);
        EventId = eventId;
        DecisionId = decisionId;
        EventType = eventType;
        Payload = payload.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, CodePointComparer.Instance);
        RecordedAt = recordedAt;
        Supersedes = supersedes;
    }

    /// <summary>This event's own id; a redelivery repeats it.</summary>
    public string EventId { get; }

    /// <summary>The decision this event is about, typically the job's idempotency key.</summary>
    public string DecisionId { get; }

    /// <summary>What kind of fact this is; it fixes which payload fields are allowed.</summary>
    public OutcomeEventType EventType { get; }

    /// <summary>The structured facts, keyed by field name in code-point order.</summary>
    public ImmutableSortedDictionary<string, JsonValue> Payload { get; }

    /// <summary>When the fact was recorded; a holdout split is made of these.</summary>
    public long RecordedAt { get; }

    /// <summary>The id of the earlier event this one corrects, or null.</summary>
    public string? Supersedes { get; }

    /// <summary>Equal when every field is, with payload values compared as Python compares them (<c>1 == 1.0</c>).</summary>
    /// <summary>Equal when every field is, with arrays and maps compared by content.</summary>
    public bool Equals(OutcomeEvent? other) =>
        other is not null && EventId == other.EventId && DecisionId == other.DecisionId && EventType == other.EventType
        && RecordedAt == other.RecordedAt && Supersedes == other.Supersedes && Payload.Count == other.Payload.Count
        && Payload.All(pair => other.Payload.TryGetValue(pair.Key, out var value) && PythonValues.AreEqual(pair.Value, value));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EventId, DecisionId, EventType, RecordedAt, Supersedes);
}

/// <summary>
/// Append-only store of outcome events, indexed by decision (<c>packvium.outcomes</c>). It never
/// reads, re-runs or overrides a packing decision's validation: it only appends structured facts
/// about decisions some already-validated system produced. In memory: it defines the contract, a
/// deployment backs it with durable storage.
/// </summary>
public sealed class OutcomeLedger
{
    private readonly Dictionary<string, OutcomeEvent> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> byDecision = new(StringComparer.Ordinal);

    /// <summary>
    /// Appends <paramref name="outcomeEvent"/>, or returns the one already recorded under its id when
    /// the content is identical -- a duplicate delivery is idempotent. The same id with different
    /// content, or a correction of an unknown event, is refused.
    /// </summary>
    public OutcomeEvent Record(OutcomeEvent outcomeEvent)
    {
        if (byId.TryGetValue(outcomeEvent.EventId, out var existing))
        {
            if (existing != outcomeEvent)
            {
                throw new DuplicateEventMismatchException(
                    $"event id {PythonText.Repr(outcomeEvent.EventId)} was already recorded with different content");
            }
            return existing;
        }
        if (outcomeEvent.Supersedes is { } superseded && !byId.ContainsKey(superseded))
        {
            throw new OutcomeEventNotFoundException(
                $"event {PythonText.Repr(outcomeEvent.EventId)} supersedes unknown event {PythonText.Repr(superseded)}");
        }
        byId[outcomeEvent.EventId] = outcomeEvent;
        if (!byDecision.TryGetValue(outcomeEvent.DecisionId, out var history))
        {
            byDecision[outcomeEvent.DecisionId] = history = [];
        }
        history.Add(outcomeEvent.EventId);
        return outcomeEvent;
    }

    /// <summary>The complete, unfolded history of one decision in recorded order, corrected events included.</summary>
    public ImmutableArray<OutcomeEvent> EventsForDecision(string decisionId) =>
        byDecision.TryGetValue(decisionId, out var history) ? [.. history.Select(eventId => byId[eventId])] : [];

    /// <summary>
    /// What was believed about one decision at <paramref name="at"/>: events recorded strictly
    /// before it, with only the corrections also recorded by then. <see cref="CurrentView"/> cannot
    /// answer this -- it folds every correction ever recorded and would pull later knowledge back
    /// into a past belief.
    /// </summary>
    public ImmutableArray<OutcomeEvent> ViewAsOf(string decisionId, long at)
    {
        if (at < 0)
        {
            throw new ArgumentException("as-of time cannot be negative");
        }
        return Unsuperseded([.. EventsForDecision(decisionId).Where(outcomeEvent => outcomeEvent.RecordedAt < at)]);
    }

    /// <summary>The current, corrected understanding of one decision: every event except those a correction supersedes.</summary>
    public ImmutableArray<OutcomeEvent> CurrentView(string decisionId) => Unsuperseded(EventsForDecision(decisionId));

    private static ImmutableArray<OutcomeEvent> Unsuperseded(ImmutableArray<OutcomeEvent> known)
    {
        var superseded = known.Select(outcomeEvent => outcomeEvent.Supersedes).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. known.Where(outcomeEvent => !superseded.Contains(outcomeEvent.EventId))];
    }
}
