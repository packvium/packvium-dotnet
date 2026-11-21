using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Packvium.Revisions;
using Packvium.Serialization;

namespace Packvium.Intelligence;

/// <summary>
/// Approved plan revisions as labeled evidence for historical evaluation (<c>revision_outcomes.py</c>).
/// A revision records what differed from an approved plan; this appends those records to an
/// <see cref="OutcomeLedger"/>, where <see cref="Holdout"/> already evaluates recommendations on
/// untouched history. It adds no second evaluator and no second store.
///
/// Every ledger event's id is the decision, the SHA-256 of the revision that recorded it and the
/// event's sequence, so evidence names one hashed, versioned document. Recording only calls
/// <see cref="OutcomeLedger.Record"/>, which never replaces an event, and never reads back or
/// changes the plan or the revision, so re-recording is idempotent. No solver, validator or
/// request reads the ledger. A revision takes no clock, so the integrator passes
/// <c>recordedAt</c> from its own system.
/// </summary>
public static class RevisionOutcomes
{
    private static readonly Dictionary<string, OutcomeEventType> Types = new(System.StringComparer.Ordinal)
    {
        ["item_missing"] = OutcomeEventType.ItemMissing,
        ["container_substituted"] = OutcomeEventType.ActualCarton,
        // A lock is an operator deciding where an item goes instead of the plan, which is what an
        // override is -- and why holdout reports it against the baseline, never the treatment.
        ["placement_locked"] = OutcomeEventType.OperatorOverride,
        ["placement_verified"] = OutcomeEventType.PlacementVerified,
    };

    /// <summary>The ledger events one revision's exceptions become; the root records none. O(E + n) for E events and a revision of size n.</summary>
    public static ImmutableArray<OutcomeEvent> Events(JsonValue revision, string decisionId, long recordedAt)
    {
        if (revision is not JsonObject document || document.Get("format") is not JsonString { Value: PlanRevision.Format })
        {
            throw new PlanRevisionException("invalid_revision", $"not a {PlanRevision.Format} document");
        }
        var digest = PlanRevision.Digest(document);
        return
        [
            .. ((JsonArray)document.Require("events")).Items.Cast<JsonObject>().Select(@event =>
            {
                var type = ((JsonString)@event.Require("type")).Value;
                return new OutcomeEvent($"{decisionId}@{digest}#{PythonText(@event.Require("sequence"))}", decisionId,
                    Types[type], Payload(type, @event), recordedAt);
            }),
        ];
    }

    /// <summary>Appends one revision's exceptions to <paramref name="ledger"/>, and returns what the ledger holds for them.</summary>
    public static ImmutableArray<OutcomeEvent> Record(OutcomeLedger ledger, JsonValue revision, string decisionId,
        long recordedAt) =>
        [.. Events(revision, decisionId, recordedAt).Select(ledger.Record)];

    private static Dictionary<string, JsonValue> Payload(string type, JsonObject @event) => type switch
    {
        "item_missing" => new() { ["item_type"] = @event.Require("item_type"), ["quantity"] = @event.Require("quantity") },
        "container_substituted" => new() { ["carton_id"] = ((JsonObject)@event.Require("replacement")).Require("id") },
        "placement_locked" => new() { ["reason_code"] = new JsonString("placement_locked") },
        _ => new()
        {
            ["item_type"] = ((JsonObject)@event.Require("placement")).Require("item_type"),
            ["container_type"] = ((JsonObject)@event.Require("placement")).Require("container_type"),
        },
    };

    /// <summary>A sequence as Python's f-string writes it: <c>1</c>, or <c>1.0</c> for a float.</summary>
    private static string PythonText(JsonValue sequence) => sequence switch
    {
        JsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        JsonFloat number => PythonFloat.Repr(number.Value),
        _ => JsonValues.Spelling(sequence),
    };
}
