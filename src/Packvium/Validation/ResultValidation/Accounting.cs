using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>The summary counts and the item accounting: every requested unit is packed or unpacked, exactly once.</summary>
internal static class Accounting
{
    /// <summary>The summary and <c>complete</c> agree with the containers and unpacked items. O(containers).</summary>
    public static void CheckStructure(JsonValue result, List<ResultIssue> issues)
    {
        var summary = Json.At(result, "summary");
        var containers = Json.At(result, "containers");
        var packed = Json.Items(containers, "result.containers").Sum(container => Json.Length(Json.At(container, "placements"), "placements"));
        var unpacked = Json.Length(Json.At(result, "unpacked_items"), "result.unpacked_items");
        var counts = new (string Field, int Actual)[]
        {
            ("container_count", Json.Length(containers, "result.containers")),
            ("packed_item_count", packed),
            ("unpacked_item_count", unpacked),
        };
        foreach (var (field, actual) in counts)
        {
            var reported = Json.At(summary, field);
            if (!Json.EqualsInt(reported, actual))
            {
                issues.Add(Issues.Of("summary_mismatch", $"{field} {PyFormat.Str(reported)} != {actual}"));
            }
        }
        var complete = Json.At(result, "complete");
        if (!Json.EqualsInt(complete, unpacked == 0 ? 1 : 0))
        {
            issues.Add(Issues.Of("complete_mismatch", $"complete={PyFormat.Str(complete)} with {unpacked} unpacked"));
        }
    }

    /// <summary>
    /// Participating <c>require_container_tag</c> rules, resolved from the request alone: a second
    /// implementation of the policy resolution, so a bug shared with an engine cannot hide behind
    /// agreement with it. The latest version of each rule id in force at <c>as_of</c> wins. O(rules).
    /// </summary>
    public static ImmutableArray<JsonValue> ContainerTagRules(JsonValue request)
    {
        if (Json.Get(request, "policy") is not JsonObject policy)
        {
            return [];
        }
        var declared = Json.Get(policy, "rules");
        if (declared is not JsonArray { Items.Length: > 0 } rules || Json.IntInstance(Json.Get(policy, "as_of")) is not { } asOf)
        {
            return [];
        }
        var shipment = Json.Get(policy, "shipment") is var held && Json.Truthy(held) ? held : Json.EmptyObject;
        var latest = new Ordered<JsonValue>();
        foreach (var rule in rules.Items)
        {
            if (rule is not JsonObject record || !record.Contains("require_container_tag") || !Applies(record, shipment))
            {
                continue;
            }
            if (Json.CompareInt(Json.Get(record, "effective_at", new JsonInteger(0)), asOf) > 0)
            {
                continue;
            }
            var id = Json.Get(record, "id");
            if (!latest.TryGet(id, out var current) || Json.Compare(Version(record), Version(current)) > 0)
            {
                latest.Set(id, record);
            }
        }
        return [.. latest.Values];
    }

    /// <summary>Every fact the rule's <c>applies_to</c> names equals the shipment's.</summary>
    private static bool Applies(JsonObject rule, JsonValue shipment)
    {
        var appliesTo = Json.Get(rule, "applies_to") is var declared && Json.Truthy(declared) ? declared : Json.EmptyObject;
        if (appliesTo is not JsonObject facts)
        {
            throw MalformedResultException.ShapeBreak("applies_to must be an object");
        }
        return facts.Members.All(fact => Json.PyEquals(Json.Get(shipment, fact.Key), fact.Value));
    }

    /// <summary><c>(effective_at, version)</c>, the order a newer rule version wins by.</summary>
    private static JsonArray Version(JsonValue rule) =>
        new([Json.Get(rule, "effective_at", new JsonInteger(0)), Json.Get(rule, "version", new JsonInteger(0))]);

    /// <summary>
    /// No item id twice, every unpacked item with a proven reason, and every item type accounted for
    /// exactly its requested quantity. O(placements + unpacked · containers · 6).
    /// </summary>
    public static void CheckAccounting(JsonValue result, RequestContext context, List<ResultIssue> issues)
    {
        var seen = new Ordered<bool>();
        var perType = new Ordered<int>();
        foreach (var container in Json.Items(Json.At(result, "containers"), "result.containers"))
        {
            foreach (var placement in Json.Items(Json.At(container, "placements"), "placements"))
            {
                var itemId = Json.At(placement, "item_id");
                if (seen.Has(itemId))
                {
                    issues.Add(Issues.Of("duplicate_item", $"{PyFormat.Str(itemId)}"));
                }
                seen.Set(itemId, true);
                Count(perType, Json.At(placement, "item_type"));
            }
        }
        var algorithm = Json.At(result, "algorithm");
        var limits = new SearchLimits(Json.Truthy(Json.At(algorithm, "time_limit_reached")), Json.Truthy(Json.At(algorithm, "effort_limit_reached")));
        foreach (var unpacked in Json.Items(Json.At(result, "unpacked_items"), "result.unpacked_items"))
        {
            var itemId = Json.At(unpacked, "item_id");
            if (seen.Has(itemId))
            {
                issues.Add(Issues.Of("duplicate_item", $"{PyFormat.Str(itemId)} is both packed and unpacked"));
            }
            seen.Set(itemId, true);
            var itemType = Json.At(unpacked, "item_type");
            Count(perType, itemType);
            if (!Json.Truthy(Json.Get(unpacked, "reason")))
            {
                issues.Add(Issues.Of("missing_reason", $"{PyFormat.Str(itemId)}"));
            }
            ReasonProof.Check(unpacked, context.Items.GetOrDefault(itemType), context, limits, issues);
        }
        foreach (var (itemType, count) in perType.Entries)
        {
            if (context.Items.GetOrDefault(itemType) is not { } spec)
            {
                issues.Add(Issues.Of("unknown_item", $"{PyFormat.Str(itemType)}"));
            }
            else if (!Json.EqualsInt(spec.Quantity, count))
            {
                issues.Add(Issues.Of("quantity_mismatch", $"{PyFormat.Str(itemType)}: {count} accounted for, {PyFormat.Str(spec.Quantity)} requested"));
            }
        }
        foreach (var (itemType, spec) in context.Items.Entries)
        {
            if (!perType.Has(itemType))
            {
                issues.Add(Issues.Of("quantity_mismatch", $"{PyFormat.Str(itemType)}: 0 accounted for, {PyFormat.Str(spec.Quantity)} requested"));
            }
        }
    }

    private static void Count(Ordered<int> perType, JsonValue itemType) =>
        perType.Set(itemType, (perType.TryGet(itemType, out var count) ? count : 0) + 1);
}

/// <summary>Whether the result says the search stopped at its time limit, or at its counted-work limit.</summary>
internal readonly record struct SearchLimits(bool TimedOut, bool EffortLimited);
