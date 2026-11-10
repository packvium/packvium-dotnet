using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

/// <summary>A reason code with no registered message: caught at the boundary, never shipped mute.</summary>
public sealed class UnknownReasonException(string reason)
    : KeyNotFoundException($"no explanation registered for reason code '{reason}'")
{
    public string Reason { get; } = reason;
}

/// <summary>A locale-neutral explanation: a stable message key, ordered arguments, and the English fallback.</summary>
public sealed record Explanation(string MessageKey, ImmutableArray<(string Name, string Value)> Arguments,
    string DefaultMessage)
{
    public string? Argument(string name) => Arguments.FirstOrDefault(argument => argument.Name == name).Value;
}

/// <summary>
/// Deterministic, localisation-ready sentences for why an item was not packed, built only from
/// the structured reason and proof level -- never by parsing free text. The English lives in
/// one table, so another locale swaps the table rather than the code.
/// </summary>
public static class Explanations
{
    public static FrozenDictionary<string, string> ReasonMessages { get; } = new Dictionary<string, string>
    {
        ["no_compatible_container_dimensions"] = "does not fit inside any offered container in any rotation",
        ["rotation_restricted"] =
            "would fit in some container with more rotations allowed, but not with the rotations this item permits",
        ["payload_exceeded"] = "exceeds the maximum payload of every offered container",
        ["policy_rule"] =
            "is forbidden from every offered container by a policy rule the request declared -- the rule and version are in the details",
        ["no_eligible_container"] = "shares no eligible container tag with any offered container",
        ["time_limit"] = "was not reached before the configured time limit expired",
        ["effort_limit"] = "was not reached before the configured effort budget was exhausted",
        ["group_cannot_fit_together"] = "belongs to a group that could not all be placed together",
        ["insufficient_support"] =
            "would fit geometrically, but only by resting on support the minimum support ratio forbids",
        ["no_feasible_placement"] =
            "found no feasible placement in the containers offered, for a reason the search could not further isolate",
        ["search_exhausted"] = "was not placed before the configured search strategies were exhausted",
        ["exact_search_incomplete"] = "was not placed because the exact search ended before proving a final answer",
        ["container_inventory_exhausted"] =
            "requires another compatible container, but the declared inventory is exhausted",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>What kind of evidence backs an explanation, from the proof level.</summary>
    public static FrozenDictionary<string, string> LevelPrefixes { get; } = new Dictionary<string, string>
    {
        ["proven"] = "Proven",
        ["unknown_due_to_limit"] = "Unknown (limit reached)",
        ["observed"] = "Observed",
        ["inferred"] = "Inferred",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string ExplainReason(string reason) =>
        ReasonMessages.TryGetValue(reason, out var message) ? message : throw new UnknownReasonException(reason);

    /// <summary>One sentence naming the item, its evidence level and why; details are appended verbatim.</summary>
    public static string ExplainUnpackedItem(UnpackedItem item)
    {
        var sentence = ForUnpackedItem(item).DefaultMessage;
        var prefix = LevelPrefixes.GetValueOrDefault(item.Proof.Level, "");
        var lead = prefix.Length > 0 ? $"{prefix}: " : "";
        var detail = item.Details.IsEmpty ? "" : $" ({string.Join("; ", item.Details)})";
        return $"{item.Instance.Id}: {lead}{sentence}{detail}";
    }

    public static Explanation ForUnpackedItem(UnpackedItem item) => new(
        $"packvium.unpacked.{item.Reason}",
        [("item_id", item.Instance.Id), ("evidence_level", item.Proof.Level), ("details", string.Join("; ", item.Details))],
        ExplainReason(item.Reason));

    public static ImmutableArray<string> ExplainUnpackedItems(IEnumerable<UnpackedItem> items) =>
        [.. items.Select(ExplainUnpackedItem)];
}
