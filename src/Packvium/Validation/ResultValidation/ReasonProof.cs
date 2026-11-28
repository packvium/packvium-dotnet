using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// The proof behind every unpacked reason: its level matches the reason, a limit reason names a
/// reached limit, a proven reason is actually proven from the request, and an observation names it.
/// O(containers · 6) per proven reason.
/// </summary>
internal static class ReasonProof
{
    private static readonly ImmutableHashSet<string> Proven =
        ["no_compatible_container_dimensions", "payload_exceeded", "no_eligible_container", "rotation_restricted", "policy_rule"];

    private static readonly ImmutableHashSet<string> Limited = ["time_limit", "effort_limit"];

    private static readonly ImmutableHashSet<string> Observed =
        ["no_feasible_placement", "search_exhausted", "exact_search_incomplete", "insufficient_support"];

    private static readonly ImmutableArray<JsonValue> EveryRotation = [.. Geometry.Orientations.Select(code => (JsonValue)new JsonString(code))];

    public static void Check(JsonValue unpacked, ItemSpec? spec, RequestContext context, SearchLimits limits, List<ResultIssue> issues)
    {
        var itemId = PyFormat.Str(Json.Get(unpacked, "item_id", new JsonString("?")));
        var reason = Json.Get(unpacked, "reason", new JsonString(string.Empty));
        var proof = Json.Get(unpacked, "proof");
        if (proof is not JsonObject)
        {
            issues.Add(Issues.Of("missing_reason_proof", $"{itemId}"));
            return;
        }
        var level = Json.Get(proof, "level");
        var expected = ExpectedLevel(reason);
        if (!Json.IsText(level, expected))
        {
            issues.Add(Issues.Of("reason_proof_mismatch",
                $"{itemId}: {PyFormat.Repr(reason)} has proof {PyFormat.Repr(level)}, expected {PyFormat.StringRepr(expected)}"));
        }
        if (Json.IsText(reason, "time_limit") && !limits.TimedOut)
        {
            issues.Add(Issues.Of("reason_limit_mismatch", $"{itemId}: time_limit reason without a reached time limit"));
        }
        if (Json.IsText(reason, "effort_limit") && !limits.EffortLimited)
        {
            issues.Add(Issues.Of("reason_limit_mismatch", $"{itemId}: effort_limit reason without a reached effort limit"));
        }
        if (spec is not null)
        {
            CheckProvenReason(unpacked, reason, spec, context, itemId, issues);
        }
        CheckObservations(proof, reason, itemId, issues);
    }

    /// <summary><c>reason in {...}</c> for each level's set; an unhashable reason is Python's <c>TypeError</c>.</summary>
    private static string ExpectedLevel(JsonValue reason)
    {
        Json.HashKey(reason);
        var text = reason is JsonString named ? named.Value : null;
        return text switch
        {
            not null when Proven.Contains(text) => "proven",
            not null when Limited.Contains(text) => "unknown_due_to_limit",
            not null when Observed.Contains(text) => "observed",
            _ => "inferred",
        };
    }

    private static void CheckProvenReason(JsonValue unpacked, JsonValue reason, ItemSpec spec, RequestContext context, string itemId,
        List<ResultIssue> issues)
    {
        void Invalid(string detail) => issues.Add(Issues.Of("invalid_proven_reason", $"{itemId}: {detail}"));

        if (Json.IsText(reason, "no_compatible_container_dimensions"))
        {
            if (FitsSome(spec, context, spec.AllowedRotations))
            {
                Invalid("at least one allowed orientation fits a container");
            }
            if (FitsSome(spec, context, EveryRotation))
            {
                Invalid("fits at least one container once every rotation is tried, not only its own allowed set -- should be rotation_restricted");
            }
        }
        if (Json.IsText(reason, "rotation_restricted"))
        {
            if (FitsSome(spec, context, spec.AllowedRotations))
            {
                Invalid("already fits a container within its own allowed rotations");
            }
            if (!FitsSome(spec, context, EveryRotation))
            {
                Invalid("does not fit any container even with every rotation tried -- should be no_compatible_container_dimensions");
            }
        }
        if (Json.IsText(reason, "payload_exceeded")
            && context.Containers.Values.Any(container => container.MaxPayload is not { } limit || spec.Weight <= limit))
        {
            Invalid("its weight is within at least one container payload limit");
        }
        if (Json.IsText(reason, "no_eligible_container")
            && context.Containers.Values.Any(container => spec.EligibleContainerTags.IsEmpty || spec.EligibleContainerTags.Overlaps(container.Tags)))
        {
            Invalid("at least one container satisfies its eligible tags");
        }
        if (Json.IsText(reason, "policy_rule"))
        {
            PolicyCitation.Check(unpacked, spec, context, itemId, issues);
        }
    }

    /// <summary>
    /// <c>any(fits_empty(container, rotated envelope) for container ... for rotation ...)</c>,
    /// stopping at the first fit, so an unknown rotation is only an error when the search reaches it.
    /// </summary>
    private static bool FitsSome(ItemSpec spec, RequestContext context, ImmutableArray<JsonValue> rotations)
    {
        var margin = 2 * context.Clearance;
        foreach (var container in context.Containers.Values)
        {
            foreach (var rotation in rotations)
            {
                var size = Geometry.Permute(spec.Dimensions, Geometry.Axes(rotation)).Select(side => side + margin).ToImmutableArray();
                if (Extents.FitsEmpty(container, size))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void CheckObservations(JsonValue proof, JsonValue reason, string itemId, List<ResultIssue> issues)
    {
        if (Json.Get(proof, "observations") is not JsonArray { Items.Length: > 0 } observations)
        {
            issues.Add(Issues.Of("missing_reason_observations", $"{itemId}"));
            return;
        }
        var observedReason = false;
        foreach (var observation in observations.Items)
        {
            if (observation is not JsonObject)
            {
                issues.Add(Issues.Of("malformed_reason_observation", $"{itemId}"));
                continue;
            }
            var code = Json.Get(observation, "code");
            if (code is not JsonString { Value.Length: > 0 } || Json.IntInstance(Json.Get(observation, "count")) is not { } count || count < 1
                || Json.Get(observation, "details") is not JsonArray)
            {
                issues.Add(Issues.Of("malformed_reason_observation", $"{itemId}"));
                continue;
            }
            observedReason |= Json.PyEquals(code, reason);
        }
        if (!observedReason)
        {
            issues.Add(Issues.Of("reason_observation_mismatch", $"{itemId}: {PyFormat.Str(reason)}"));
        }
    }
}

/// <summary>
/// A <c>policy_rule</c> reason makes two claims, and both are checked: that some participating
/// rule rules this item out of every container, and that the rule the result names is that rule.
/// A citation naming an innocent rule sends a caller to change the wrong policy.
/// </summary>
internal static class PolicyCitation
{
    public static void Check(JsonValue unpacked, ItemSpec spec, RequestContext context, string itemId, List<ResultIssue> issues)
    {
        var blocking = context.ContainerTagRules.Where(rule => Blocks(rule, spec, context)).ToList();
        if (blocking.Count == 0)
        {
            issues.Add(Issues.Of("invalid_proven_reason", $"{itemId}: no participating policy rule rules it out of every container"));
            return;
        }
        var details = Json.Get(unpacked, "details");
        var cited = Json.Truthy(details)
            ? string.Join(' ', Json.Items(details, "details").Select(detail => Json.Text(detail, "details")))
            : string.Empty;
        foreach (var rule in blocking)
        {
            if (cited.Contains($"{PyFormat.Str(Json.At(rule, "id"))}@{PyFormat.Str(Json.At(rule, "version"))}", System.StringComparison.Ordinal))
            {
                return;
            }
        }
        issues.Add(Issues.Of("invalid_proven_reason", $"{itemId}: cites {PyFormat.StringRepr(cited)}, which names no rule that actually blocks it"));
    }

    /// <summary>The rule names one of the item's tags and no container carries the tag it requires.</summary>
    private static bool Blocks(JsonValue rule, ItemSpec spec, RequestContext context)
    {
        if (!spec.Tags.Contains(Json.Get(Json.At(rule, "require_container_tag"), "item_tag")))
        {
            return false;
        }
        return !context.Containers.Values.Any(container =>
            container.Tags.Contains(Json.Get(Json.At(rule, "require_container_tag"), "container_tag")));
    }
}
