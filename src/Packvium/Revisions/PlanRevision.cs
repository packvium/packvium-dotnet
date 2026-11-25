using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Packvium.Artifacts;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Revisions;

/// <summary>
/// Plan revisions (<c>packvium-plan-revision/v1</c>): an
/// append-only chain of exceptions against approved plans. A revision records what differed
/// from an approved plan -- a missing item, a substituted carton, a lock, a verified placement --
/// and derives the request the next plan solves. It is a pure function of its parent, the
/// approved artifact and its events: it calls no solver, validator or clock, so every builder
/// emits the same bytes. A replan is then an ordinary solve of the derived request; locks and
/// verified placements become <c>fixed_placements</c>.
/// </summary>
public static class PlanRevision
{
    public const string Format = "packvium-plan-revision/v1";

    public static ImmutableArray<string> EventTypes { get; } =
        ["item_missing", "container_substituted", "placement_locked", "placement_verified"];

    private static readonly Dictionary<string, string[]> EventFields = new(StringComparer.Ordinal)
    {
        ["item_missing"] = ["item_type", "quantity"],
        ["container_substituted"] = ["container_type", "replacement"],
        ["placement_locked"] = ["placement"],
        ["placement_verified"] = ["placement"],
    };

    private static readonly string[] PlacementFields =
        ["item_type", "container_type", "container_instance", "position", "orientation"];

    /// <summary>The RFC 8785 canonical form, the bytes a digest and every engine are compared on.</summary>
    public static string CanonicalJson(JsonValue document)
    {
        try
        {
            return CanonicalJsonWriter.Write(document);
        }
        catch (CanonicalJsonException error)
        {
            throw new PlanRevisionException(error.Code, error.Message);
        }
    }

    /// <summary><c>sha256:</c> and the lowercase hex SHA-256 of a document's canonical UTF-8 bytes.</summary>
    public static string Digest(JsonValue document) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(document)))).ToLowerInvariant();

    /// <summary>Revision 0: the request as first approved, with nothing recorded against it.</summary>
    public static JsonObject Root(JsonValue request)
    {
        if (request is not JsonObject requestObject)
        {
            throw new PlanRevisionException("invalid_revision", "a request is a JSON object");
        }
        var document = Document(0, JsonNull.Instance, JsonNull.Instance, [], requestObject);
        CanonicalJson(document);
        return document;
    }

    /// <summary>
    /// The next revision: <paramref name="events"/>, observed against
    /// <paramref name="approvedArtifact"/>, applied to <paramref name="parent"/>. Hashes the parent
    /// and the artifact in their serialized size; placement events replay through one canonical
    /// key per fixed placement, while missing-item and carton events scan their request lists.
    /// </summary>
    public static JsonObject Derive(JsonValue parent, JsonValue approvedArtifact, JsonValue events)
    {
        var parentDocument = RequireRevision(parent);
        RequireArtifact(approvedArtifact, parentDocument.Require("request"));
        if (events is not JsonArray { Items.IsEmpty: false } list)
        {
            throw new PlanRevisionException("invalid_event", "a revision records at least one event");
        }
        var first = LastSequence(parentDocument) + 1;
        ImmutableArray<JsonValue> recorded = [.. list.Items.Select((raw, offset) => (JsonValue)Event(raw, first + offset))];
        var request = ApplyEvents(parentDocument.Require("request"), new JsonArray(recorded));
        var provenance = (JsonObject)((JsonObject)approvedArtifact).Require("provenance");
        var approved = Object(("artifact", new JsonString(Digest(approvedArtifact))), ("replay", provenance.Require("replay")));
        var number = JsonValues.WholeNumber(parentDocument.Require("revision"))!.Value + 1;
        var document = Document(number, new JsonString(Digest(parentDocument)), approved, recorded, request);
        CanonicalJson(document);
        return document;
    }

    /// <summary>
    /// The request these events derive from <paramref name="request"/>. Refuses a contradiction,
    /// never physics: whether the new fixed set can hold is the engine's admission check when it
    /// is solved.
    /// </summary>
    public static JsonObject ApplyEvents(JsonValue request, JsonValue events)
    {
        if (request is not JsonObject derived)
        {
            throw new PlanRevisionException("invalid_revision", "a request is a JSON object");
        }
        if (events is not JsonArray list)
        {
            throw new PlanRevisionException("invalid_event", "events are a JSON array");
        }
        var placements = new PlacementIndex();
        foreach (var raw in list.Items)
        {
            var @event = RequireShape(raw);
            if (JsonValues.WholeNumber(@event.Get("sequence")) is null)
            {
                throw new PlanRevisionException("invalid_event",
                    $"event sequence {Spell(@event.Get("sequence"))} is not an integer");
            }
            derived = ((JsonString)@event.Require("type")).Value switch
            {
                "item_missing" => ApplyItemMissing(derived, @event),
                "container_substituted" => ApplyContainerSubstituted(derived, @event),
                _ => placements.Apply(derived, @event),
            };
        }
        return derived;
    }

    /// <summary>
    /// Every way the chain fails to be what its root and events derive, without stopping at the
    /// first. <paramref name="artifacts"/>, when not JSON null, holds at position k the artifact
    /// revision k names as approved.
    /// </summary>
    public static List<RevisionIssue> VerifyChain(JsonValue revisions, JsonValue? artifacts = null)
    {
        if (revisions is not JsonArray chain)
        {
            throw new PlanRevisionException("invalid_revision", "a chain is a JSON array");
        }
        var artifactList = artifacts is null or JsonNull
            ? null
            : artifacts as JsonArray ?? throw new PlanRevisionException("invalid_artifact", "artifacts is a JSON array");
        var issues = new List<RevisionIssue>();
        long lastSequence = 0;
        for (var position = 0; position < chain.Items.Length; position++)
        {
            JsonObject revision;
            try
            {
                revision = RequireRevision(chain.Items[position]);
            }
            catch (PlanRevisionException error)
            {
                issues.Add(new RevisionIssue("invalid_revision", position, error.Message));
                return issues;
            }
            var number = JsonValues.WholeNumber(revision.Require("revision"))!.Value;
            if (number != position)
            {
                issues.Add(new RevisionIssue("revision_number", position, $"revision {number} at position {position}"));
            }
            var parent = position > 0 ? chain.Items[position - 1] : null;
            JsonValue expectedParent = parent is null ? JsonNull.Instance : new JsonString(Digest(parent));
            var recordedParent = revision.Get("parent") ?? JsonNull.Instance;
            if (!SameParent(recordedParent, expectedParent))
            {
                issues.Add(new RevisionIssue("parent_mismatch", position,
                    $"parent {Plain(recordedParent)}, expected {Plain(expectedParent)}"));
            }
            lastSequence = CheckSequences(revision, position, lastSequence, issues);
            if (parent is not null)
            {
                CheckRequest(revision, (JsonObject)parent, position, issues);
            }
            var artifact = artifactList is null || position >= artifactList.Items.Length ? null : artifactList.Items[position];
            if (artifact is not (null or JsonNull) && parent is not null)
            {
                CheckArtifact(revision, (JsonObject)parent, artifact, position, issues);
            }
        }
        return issues;
    }

    // ---------------------------------------------------------------------------- building

    private static JsonObject Document(long number, JsonValue parent, JsonValue approved,
        ImmutableArray<JsonValue> events, JsonObject request) => Object(
        ("format", new JsonString(Format)),
        ("suite_version", new JsonString(OperationalArtifact.SuiteVersion)),
        ("revision", new JsonInteger(number)),
        ("parent", parent),
        ("approved", approved),
        ("events", new JsonArray(events)),
        ("request", request));

    private static JsonObject RequireRevision(JsonValue document)
    {
        if (document is not JsonObject revision || revision.Get("format") is not JsonString { Value: Format })
        {
            throw new PlanRevisionException("invalid_revision", $"not a {Format} document");
        }
        if (JsonValues.WholeNumber(revision.Get("revision")) is not >= 0)
        {
            throw new PlanRevisionException("invalid_revision", "revision is a non-negative integer");
        }
        if (revision.Get("request") is not JsonObject)
        {
            throw new PlanRevisionException("invalid_revision", "a revision carries its request");
        }
        if (revision.Get("events") is not JsonArray events)
        {
            throw new PlanRevisionException("invalid_revision", "a revision carries its events");
        }
        if (!events.Items.All(@event => @event is JsonObject recorded && JsonValues.WholeNumber(recorded.Get("sequence")) is not null))
        {
            throw new PlanRevisionException("invalid_revision", "a revision's events each carry an integer sequence");
        }
        return revision;
    }

    private static void RequireArtifact(JsonValue artifact, JsonValue request)
    {
        if (artifact is not JsonObject document || document.Get("format") is not JsonString { Value: OperationalArtifact.Format })
        {
            throw new PlanRevisionException("invalid_artifact", $"not a {OperationalArtifact.Format} document");
        }
        if (document.Get("provenance") is not JsonObject provenance || provenance.Get("replay") is not JsonObject)
        {
            throw new PlanRevisionException("invalid_artifact", "the artifact carries no provenance.replay");
        }
        if (!Same(provenance.Get("request"), request))
        {
            throw new PlanRevisionException("invalid_artifact",
                "the artifact was built from a different request than the parent's");
        }
    }

    private static long LastSequence(JsonObject revision)
    {
        var events = ((JsonArray)revision.Require("events")).Items;
        if (!events.IsEmpty)
        {
            return Sequence((JsonObject)events[^1]);
        }
        if (JsonValues.WholeNumber(revision.Require("revision")) != 0)
        {
            throw new PlanRevisionException("invalid_revision", "only the root revision records no events");
        }
        return 0;
    }

    private static JsonObject Event(JsonValue raw, long sequence)
    {
        var @event = RequireShape(raw);
        if (JsonValues.WholeNumber(@event.Get("sequence")) != sequence)
        {
            throw new PlanRevisionException("invalid_event",
                $"event sequence {Spell(@event.Get("sequence"))} does not continue the chain at {sequence}");
        }
        return @event;
    }

    private static JsonObject RequireShape(JsonValue raw)
    {
        if (raw is not JsonObject @event)
        {
            throw new PlanRevisionException("invalid_event", "an event is a JSON object");
        }
        var kindValue = @event.Get("type");
        if (kindValue is not JsonString { Value: var kind } || !EventFields.TryGetValue(kind, out var fields))
        {
            throw new PlanRevisionException("invalid_event", $"unknown event type {Spell(kindValue)}");
        }
        var unknown = @event.Keys.Where(key => key != "sequence" && key != "type" && !fields.Contains(key, StringComparer.Ordinal))
            .Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw new PlanRevisionException("invalid_event", $"{kind} does not carry {FixedPlacementShapes.Names(unknown)}");
        }
        var missing = fields.Where(field => !@event.Contains(field)).ToList();
        if (missing.Count > 0)
        {
            throw new PlanRevisionException("invalid_event", $"{kind} needs {FixedPlacementShapes.Names(missing)}");
        }
        switch (kind)
        {
            case "item_missing":
                ValidateItemMissing(@event);
                break;
            case "container_substituted":
                ValidateContainerSubstituted(@event);
                break;
            default:
                ValidatePlacement(@event);
                break;
        }
        return @event;
    }

    private static void ValidateItemMissing(JsonObject @event)
    {
        RequireName(@event.Require("item_type"), "item_type");
        if (JsonValues.WholeNumber(@event.Require("quantity")) is not >= 1)
        {
            throw new PlanRevisionException("invalid_event", "item_missing.quantity is a positive integer");
        }
    }

    private static void ValidateContainerSubstituted(JsonObject @event)
    {
        RequireName(@event.Require("container_type"), "container_type");
        if (@event.Require("replacement") is not JsonObject replacement)
        {
            throw new PlanRevisionException("invalid_event", "a replacement is a container object");
        }
        RequireName(replacement.Get("id"), "replacement.id");
    }

    private static void ValidatePlacement(JsonObject @event)
    {
        if (@event.Require("placement") is not JsonObject placement)
        {
            throw new PlanRevisionException("invalid_event", "a placement is a fixed-placement object");
        }
        var unknown = placement.Keys.Where(key => !PlacementFields.Contains(key, StringComparer.Ordinal))
            .Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw new PlanRevisionException("invalid_event", $"a placement does not carry {FixedPlacementShapes.Names(unknown)}");
        }
        RequireName(placement.Get("item_type"), "placement.item_type");
        RequireName(placement.Get("container_type"), "placement.container_type");
        if (placement.Get("orientation") is not JsonString { Value: var orientation }
            || !FixedPlacementShapes.Orientations.Contains(orientation))
        {
            throw new PlanRevisionException("invalid_event", "placement.orientation is one of the six codes");
        }
        if (JsonValues.WholeNumber(placement.Get("container_instance") ?? new JsonInteger(1)) is not >= 1)
        {
            throw new PlanRevisionException("invalid_event", "placement.container_instance counts from 1");
        }
        FixedPlacementShapes.RequirePointShape(placement.Get("position") ?? new JsonObject([]), "placement.position",
            string.Empty, (message, _) => new PlanRevisionException("invalid_event", message));
    }

    private static void RequireName(JsonValue? value, string field)
    {
        if (value is not JsonString { Value.Length: > 0 })
        {
            throw new PlanRevisionException("invalid_event", $"{field} is a non-empty string");
        }
    }

    // ---------------------------------------------------------------------------- applying

    private static JsonObject ApplyItemMissing(JsonObject request, JsonObject @event)
    {
        var items = Entries(request, "items");
        var itemType = ((JsonString)@event.Require("item_type")).Value;
        var index = IndexOf(items, itemType, "item");
        var quantity = JsonValues.WholeNumber(items[index].Get("quantity") ?? new JsonInteger(1))
                       ?? throw new PlanRevisionException("invalid_revision", $"request.items[{index}].quantity is an integer");
        var remaining = quantity - JsonValues.WholeNumber(@event.Require("quantity"))!.Value;
        var fixedCount = FixedEntries(request).Count(entry => entry.Get("item_type") is JsonString { Value: var type } && type == itemType);
        if (remaining < fixedCount)
        {
            throw new PlanRevisionException("event_conflict",
                $"event {Sequence(@event)}: {fixedCount} {itemType} are fixed, {Math.Max(remaining, 0)} would remain");
        }
        if (remaining > 0)
        {
            items[index] = items[index].With("quantity", new JsonInteger(remaining));
        }
        else if (items.Count == 1)
        {
            throw new PlanRevisionException("event_conflict", $"event {Sequence(@event)}: no item would remain to pack");
        }
        else
        {
            items.RemoveAt(index);
        }
        return request.With("items", List(items));
    }

    private static JsonObject ApplyContainerSubstituted(JsonObject request, JsonObject @event)
    {
        var containers = Entries(request, "containers");
        var containerType = ((JsonString)@event.Require("container_type")).Value;
        var index = IndexOf(containers, containerType, "container");
        if (FixedEntries(request).Any(entry => entry.Get("container_type") is JsonString { Value: var type } && type == containerType))
        {
            throw new PlanRevisionException("event_conflict",
                $"event {Sequence(@event)}: fixed placements are in {containerType}");
        }
        var replacement = (JsonObject)@event.Require("replacement");
        var replacementId = ((JsonString)replacement.Require("id")).Value;
        if (containers.Where((_, position) => position != index)
            .Any(container => container.Get("id") is JsonString { Value: var id } && id == replacementId))
        {
            throw new PlanRevisionException("event_conflict",
                $"event {Sequence(@event)}: another container is already {replacementId}");
        }
        containers[index] = replacement;
        return request.With("containers", List(containers));
    }

    /// <summary>
    /// Placement events of one replay, deduplicated by one canonical key per fixed placement; the
    /// request's first-seen fixed-placement order stays untouched. O(1) per event after one
    /// O(f) pass over the f placements the request already fixes.
    /// </summary>
    private sealed class PlacementIndex
    {
        private List<JsonObject>? fixedEntries;
        private HashSet<string>? keys;
        private bool indexable = true;

        public JsonObject Apply(JsonObject request, JsonObject @event)
        {
            if (fixedEntries is null)
            {
                fixedEntries = FixedEntries(request);
                try
                {
                    keys = [.. fixedEntries.Select(entry => CanonicalJson(WithDefaultInstance(entry)))];
                }
                catch (PlanRevisionException)
                {
                    indexable = false;
                }
            }
            var raw = (JsonObject)@event.Require("placement");
            var placement = WithDefaultInstance(raw);
            if (indexable)
            {
                string? key = null;
                try
                {
                    key = CanonicalJson(placement);
                }
                catch (PlanRevisionException)
                {
                    indexable = false;
                }
                if (key is not null)
                {
                    if (keys!.Add(key))
                    {
                        fixedEntries.Add(raw);
                    }
                    return Written(request);
                }
            }
            // An uncanonicalizable value may occur in a direct ApplyEvents call: keep the
            // reference's first-match order rather than making the index an earlier error.
            if (!fixedEntries.Any(entry => Same(WithDefaultInstance(entry), placement)))
            {
                fixedEntries.Add(raw);
            }
            return Written(request);
        }

        private JsonObject Written(JsonObject request) => request.With("fixed_placements", List(fixedEntries!));

        /// <summary><c>{"container_instance": 1, **entry}</c>: the entry's own instance wins, null included.</summary>
        private static JsonObject WithDefaultInstance(JsonObject entry) =>
            entry.Contains("container_instance") ? entry : entry.With("container_instance", new JsonInteger(1));
    }

    private static List<JsonObject> Entries(JsonObject request, string key)
    {
        if (request.Get(key) is not JsonArray entries || !entries.Items.All(entry => entry is JsonObject))
        {
            throw new PlanRevisionException("invalid_revision", $"request.{key} is a list of objects");
        }
        return [.. entries.Items.Cast<JsonObject>()];
    }

    /// <summary>
    /// The request's fixed placements; absent or null is none, anything else not a list of
    /// objects is a request no event can safely edit.
    /// </summary>
    private static List<JsonObject> FixedEntries(JsonObject request) =>
        request.Get("fixed_placements") is null or JsonNull ? [] : Entries(request, "fixed_placements");

    private static int IndexOf(List<JsonObject> entries, string identifier, string kind)
    {
        var index = entries.FindIndex(entry => entry.Get("id") is JsonString { Value: var id } && id == identifier);
        return index >= 0
            ? index
            : throw new PlanRevisionException("event_conflict", $"the request has no {kind} {CanonicalText.Spelling(identifier)}");
    }

    private static long Sequence(JsonObject @event) => JsonValues.WholeNumber(@event.Require("sequence"))!.Value;

    // ------------------------------------------------------------------------------ audit

    private static long CheckSequences(JsonObject revision, int position, long last, List<RevisionIssue> issues)
    {
        var events = ((JsonArray)revision.Require("events")).Items;
        if (position == 0 && !events.IsEmpty)
        {
            issues.Add(new RevisionIssue("sequence_gap", 0, "the root revision records no events"));
        }
        if (position > 0 && events.IsEmpty)
        {
            issues.Add(new RevisionIssue("sequence_gap", position, "a revision records at least one event"));
        }
        foreach (var @event in events)
        {
            var sequence = Sequence((JsonObject)@event);
            if (sequence != last + 1)
            {
                issues.Add(new RevisionIssue("sequence_gap", position, $"event {sequence} where {last + 1} was next"));
            }
            last = sequence;
        }
        return last;
    }

    private static void CheckRequest(JsonObject revision, JsonObject parent, int position, List<RevisionIssue> issues)
    {
        JsonObject derived;
        try
        {
            derived = ApplyEvents(parent.Require("request"), revision.Require("events"));
        }
        catch (PlanRevisionException error)
        {
            issues.Add(new RevisionIssue("request_mismatch", position, $"the events do not apply: {error.Message}"));
            return;
        }
        if (!Same(derived, revision.Require("request")))
        {
            issues.Add(new RevisionIssue("request_mismatch", position,
                "the request is not what the parent's request and these events derive"));
        }
    }

    private static void CheckArtifact(JsonObject revision, JsonObject parent, JsonValue artifact, int position,
        List<RevisionIssue> issues)
    {
        var approved = revision.Get("approved") as JsonObject;
        var digest = DigestOrNull(artifact);
        if (digest is null || approved is null || approved.Get("artifact") is not JsonString { Value: var recorded } || recorded != digest)
        {
            issues.Add(new RevisionIssue("artifact_mismatch", position,
                "the artifact's digest is not the one this revision approved"));
            return;
        }
        if ((artifact as JsonObject)?.Get("provenance") is not JsonObject provenance
            || !Same(provenance.Get("request"), parent.Require("request")))
        {
            issues.Add(new RevisionIssue("artifact_mismatch", position,
                "the artifact was built from a different request than the parent's"));
        }
        else if (!Same(provenance.Get("replay"), approved.Get("replay")))
        {
            issues.Add(new RevisionIssue("artifact_mismatch", position,
                "approved.replay is not the artifact's provenance.replay"));
        }
    }

    /// <summary>Equality as the canonical form sees it, so <c>1.0</c> and <c>1</c> are one value; absent is null.</summary>
    private static bool Same(JsonValue? left, JsonValue? right) =>
        CanonicalJson(left ?? JsonNull.Instance) == CanonicalJson(right ?? JsonNull.Instance);

    /// <summary>Python's <c>!=</c> between a recorded parent and the expected digest or null.</summary>
    private static bool SameParent(JsonValue recorded, JsonValue expected) => (recorded, expected) switch
    {
        (JsonNull, JsonNull) => true,
        (JsonString left, JsonString right) => left.Value == right.Value,
        _ => false,
    };

    /// <summary>An audited artifact's digest; one with no canonical form matches no approval.</summary>
    private static string? DigestOrNull(JsonValue document)
    {
        try
        {
            return Digest(document);
        }
        catch (PlanRevisionException)
        {
            return null;
        }
    }

    /// <summary>A digest as itself, and anything a tampered chain puts in its place as JSON.</summary>
    private static string Plain(JsonValue value) => value is JsonString text ? text.Value : JsonValues.Spelling(value);

    private static string Spell(JsonValue? value) => JsonValues.Spelling(value ?? JsonNull.Instance);

    private static JsonArray List(IEnumerable<JsonObject> entries) => new([.. entries.Select(entry => (JsonValue)entry)]);

    private static JsonObject Object(params (string Key, JsonValue Value)[] members) =>
        new(members.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)));
}
