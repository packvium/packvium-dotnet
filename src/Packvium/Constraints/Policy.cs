using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>A rule set this engine cannot honour exactly as written: refused, never skipped.</summary>
public sealed class PolicyException(string message) : ArgumentException(message);

/// <summary>Declared shipment facts. A fact nobody declared is not a wildcard: a rule naming it never participates.</summary>
public sealed record ShipmentContext(string? Facility = null, string? Customer = null, string? Carrier = null,
    string? Service = null)
{
    public static readonly ImmutableArray<string> Facts = ["facility", "customer", "carrier", "service"];

    public string? Fact(string name) => name switch
    {
        "facility" => Facility,
        "customer" => Customer,
        "carrier" => Carrier,
        _ => Service,
    };

    /// <summary>Whether every fact this selector names equals the shipment's own.</summary>
    public bool SatisfiedBy(ShipmentContext shipment) =>
        Facts.All(name => Fact(name) is null || Fact(name) == shipment.Fact(name));
}

/// <summary>The three rule forms; each compiles onto a predicate the pipeline already has.</summary>
public abstract record PolicyForm;

/// <summary>No container may hold both tags.</summary>
public sealed record SeparateTags(string Tag, string FromTag) : PolicyForm;

/// <summary>An item carrying the tag may only enter a container carrying the container tag.</summary>
public sealed record RequireContainerTag(string ItemTag, string ContainerTag) : PolicyForm;

/// <summary>At most <paramref name="MaxCount"/> items carrying the tag may share one container.</summary>
public sealed record LimitTagPerContainer(string Tag, long MaxCount) : PolicyForm;

public sealed record PolicyRule(string Id, long Version, long EffectiveAt, long Priority, ShipmentContext AppliesTo,
    PolicyForm Form)
{
    public string Citation => $"{Id}@{Version}";
}

/// <summary>
/// The rules participating in one request, resolved and in citation order: per id the latest
/// effective version wins, then highest priority first, ties to the smallest id. Resolution is
/// O(r log r).
/// </summary>
public sealed class PolicyRuleSet(ImmutableArray<PolicyRule> rules)
{
    public ImmutableArray<PolicyRule> Rules { get; } = rules;

    public ImmutableArray<IPlacementConstraint> Constraints() => Rules.IsEmpty ? [] : [new PolicyConstraint(Rules)];

    public static PolicyRuleSet Resolve(IEnumerable<PolicyRule> rules, long asOf, ShipmentContext shipment)
    {
        var latest = new Dictionary<string, PolicyRule>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(rule => rule.EffectiveAt <= asOf && rule.AppliesTo.SatisfiedBy(shipment)))
        {
            if (!latest.TryGetValue(rule.Id, out var current)
                || (rule.EffectiveAt, rule.Version).CompareTo((current.EffectiveAt, current.Version)) > 0)
            {
                latest[rule.Id] = rule;
            }
        }
        return new PolicyRuleSet([.. latest.Values.OrderBy(rule => -rule.Priority)
            .ThenBy(rule => rule.Id, CodePointComparer.Instance)]);
    }
}

/// <summary>
/// Rejects a candidate any participating rule forbids, citing the first in citation order.
/// O(m + r) per candidate: one pass over the placed tags, one over the rules.
/// </summary>
public sealed class PolicyConstraint(ImmutableArray<PolicyRule> rules) : IPlacementConstraint, IProvenRejection
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        var itemTags = context.Item.Item.Tags;
        Dictionary<string, int>? present = null;
        foreach (var rule in rules)
        {
            if (rule.Form is RequireContainerTag require)
            {
                if (itemTags.Contains(require.ItemTag) && !context.Container.Tags.Contains(require.ContainerTag))
                {
                    return Reject(rule, $"requires a container tagged {PythonText.Repr(require.ContainerTag)}");
                }
                continue;
            }
            present ??= TagCounts(context.Placements);
            if (rule.Form is SeparateTags separate)
            {
                if (itemTags.Contains(separate.Tag) && present.GetValueOrDefault(separate.FromTag) > 0)
                {
                    return Reject(rule,
                        $"{PythonText.Repr(separate.Tag)} may not share a container with {PythonText.Repr(separate.FromTag)}");
                }
                if (itemTags.Contains(separate.FromTag) && present.GetValueOrDefault(separate.Tag) > 0)
                {
                    return Reject(rule,
                        $"{PythonText.Repr(separate.FromTag)} may not share a container with {PythonText.Repr(separate.Tag)}");
                }
            }
            else if (rule.Form is LimitTagPerContainer limit && itemTags.Contains(limit.Tag)
                     && present.GetValueOrDefault(limit.Tag) >= limit.MaxCount)
            {
                return Reject(rule, $"at most {limit.MaxCount} item(s) tagged {PythonText.Repr(limit.Tag)} per container");
            }
        }
        return ConstraintResult.Allow();
    }

    /// <summary>
    /// Only <c>require_container_tag</c> is a statement about the request alone; segregation and
    /// caps depend on what else was packed. O(r * c) per unpacked item.
    /// </summary>
    public (string Reason, string Detail)? ProvesUnplaceable(ItemInstance item, IReadOnlyList<Container> containers)
    {
        foreach (var rule in rules)
        {
            if (rule.Form is RequireContainerTag require && item.Item.Tags.Contains(require.ItemTag)
                && !containers.Any(container => container.Tags.Contains(require.ContainerTag)))
            {
                return ("policy_rule", $"{rule.Citation}: requires a container tagged {PythonText.Repr(require.ContainerTag)}, "
                                       + "which none of the containers offered carries");
            }
        }
        return null;
    }

    private static ConstraintResult Reject(PolicyRule rule, string why) =>
        ConstraintResult.Reject("policy_rule", $"{rule.Citation}: {why}");

    private static Dictionary<string, int> TagCounts(IEnumerable<Placement> placements)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tag in placements.SelectMany(placement => placement.Instance.Item.Tags))
        {
            counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }
        return counts;
    }
}
