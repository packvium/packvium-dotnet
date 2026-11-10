using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary><c>PolicyRuleSet.from_dict</c>: every malformed rule is a refusal with the reference's own message.</summary>
public static class PolicyReader
{
    private static readonly ImmutableArray<(string Name, ImmutableArray<string> Keys)> Forms =
    [
        ("separate_tags", ["tag", "from_tag"]),
        ("require_container_tag", ["item_tag", "container_tag"]),
        ("limit_tag_per_container", ["tag", "max"]),
    ];

    public static PolicyRuleSet Read(JsonValue? raw)
    {
        if (raw is null or JsonNull)
        {
            return new PolicyRuleSet([]);
        }
        if (raw is not JsonObject policy)
        {
            throw new PolicyException("policy must be an object");
        }
        var unknown = policy.Keys.Except(["as_of", "shipment", "rules"]).Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw new PolicyException($"policy has unknown keys: {string.Join(", ", unknown)}");
        }
        var declared = policy.Get("rules") is { } rules && PythonCoercion.Bool(rules) ? rules : new JsonArray([]);
        if (declared is not JsonArray list)
        {
            throw new PolicyException("policy.rules must be an array");
        }
        if (list.Items.IsEmpty)
        {
            return new PolicyRuleSet([]);
        }
        if (!policy.Contains("as_of"))
        {
            throw new PolicyException("policy.as_of is required whenever policy.rules is non-empty");
        }
        var asOf = Integer(policy.Get("as_of"), "policy.as_of", 0);
        var shipment = Shipment(policy.Get("shipment"), "policy.shipment");
        var parsed = list.Items.Select(Rule).ToList();
        return PolicyRuleSet.Resolve(parsed, asOf, shipment);
    }

    private static PolicyRule Rule(JsonValue raw, int index)
    {
        var where = $"policy.rules[{index}]";
        if (raw is not JsonObject rule)
        {
            throw new PolicyException($"{where} must be an object");
        }
        var named = Forms.Where(form => rule.Contains(form.Name)).ToList();
        if (named.Count != 1)
        {
            throw new PolicyException(
                $"{where} must name exactly one rule form ({string.Join(", ", Forms.Select(form => form.Name).Order(CodePointComparer.Instance))}), "
                + $"not {named.Count}");
        }
        if (rule.Get("id") is not JsonString { Value.Length: > 0 } identifier)
        {
            throw new PolicyException($"{where}.id must be a non-empty string");
        }
        var version = Integer(rule.Get("version"), $"{where}.version", 1);
        return new PolicyRule(identifier.Value, version,
            Integer(rule.Get("effective_at"), $"{where}.effective_at", 0),
            Integer(rule.Get("priority"), $"{where}.priority", 0),
            Shipment(rule.Get("applies_to"), $"{where}.applies_to"),
            Form(named[0].Name, named[0].Keys, rule.Require(named[0].Name), $"{where}.{named[0].Name}"));
    }

    private static PolicyForm Form(string name, ImmutableArray<string> keys, JsonValue raw, string where)
    {
        if (raw is not JsonObject form)
        {
            throw new PolicyException($"{where} must be an object");
        }
        var unknown = form.Keys.Except(keys).Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw new PolicyException($"{where} has unknown keys: {string.Join(", ", unknown)}");
        }
        var missing = keys.Where(key => !form.Contains(key)).ToList();
        if (missing.Count > 0)
        {
            throw new PolicyException($"{where} is missing {string.Join(", ", missing)}");
        }
        var values = new List<object>();
        foreach (var key in keys)
        {
            var value = form.Require(key);
            values.Add(key == "max"
                ? Integer(value, $"{where}.max", 0)
                : value is JsonString { Value.Length: > 0 } text
                    ? text.Value
                    : throw new PolicyException($"{where}.{key} must be a non-empty string"));
        }
        return name switch
        {
            "separate_tags" => new SeparateTags((string)values[0], (string)values[1]),
            "require_container_tag" => new RequireContainerTag((string)values[0], (string)values[1]),
            _ => new LimitTagPerContainer((string)values[0], (long)values[1]),
        };
    }

    private static ShipmentContext Shipment(JsonValue? raw, string where)
    {
        if (raw is null or JsonNull)
        {
            return new ShipmentContext();
        }
        if (raw is not JsonObject facts)
        {
            throw new PolicyException($"{where} must be an object");
        }
        var unknown = facts.Keys.Except(ShipmentContext.Facts).Order(CodePointComparer.Instance).ToList();
        if (unknown.Count > 0)
        {
            throw new PolicyException($"{where} names unknown shipment facts: {string.Join(", ", unknown)}");
        }
        foreach (var (name, value) in facts.Members)
        {
            if (value is not JsonString { Value.Length: > 0 })
            {
                throw new PolicyException($"{where}.{name} must be a non-empty string");
            }
        }
        string? Fact(string name) => (facts.Get(name) as JsonString)?.Value;
        return new ShipmentContext(Fact("facility"), Fact("customer"), Fact("carrier"), Fact("service"));
    }

    /// <summary>A JSON integer, never a boolean, at or above the minimum.</summary>
    private static long Integer(JsonValue? value, string where, long minimum)
    {
        if (value is not JsonInteger integer || integer.Value < minimum || integer.Value > long.MaxValue)
        {
            throw new PolicyException($"{where} must be an integer >= {minimum}");
        }
        return (long)integer.Value;
    }
}
