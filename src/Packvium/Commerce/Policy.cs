using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Commerce;

/// <summary>A predicate this engine does not recognise: refused at registration, never ignored at evaluation.</summary>
public sealed class UnsupportedPredicateException(string message) : ArgumentException(message);

public sealed class PolicyRuleNotFoundException(string message, string ruleId) : Exception(message)
{
    public string RuleId { get; } = ruleId;
}

public sealed class PolicyVersionNotFoundException(string message, string ruleId, BigInteger version) : Exception(message)
{
    public string RuleId { get; } = ruleId;

    public BigInteger Version { get; } = version;
}

/// <summary>The closed vocabularies, spelled as on the wire.</summary>
public static class PolicyVocabulary
{
    public static readonly ImmutableArray<string> Scopes =
        ["facility", "customer", "carrier", "material", "hazmat", "temperature", "service"];

    public static readonly ImmutableArray<string> Operators = ["equals", "not_equals", "in", "not_in", "exists", "absent"];

    public static readonly ImmutableArray<string> Actions = ["allow", "reject"];
}

/// <summary>
/// One condition over the caller's context. Values are raw JSON and compare with Python's
/// semantics -- <c>true == 1</c>, <c>1 == 1.0</c>, lists element-wise, and <c>in</c> over a
/// string is a substring test -- because that is the reference contract.
/// </summary>
public sealed record PolicyPredicate
{
    public PolicyPredicate(string scope, string field, string @operator, JsonValue? value)
    {
        if (!PolicyVocabulary.Scopes.Contains(scope))
        {
            throw new UnsupportedPredicateException($"unsupported policy scope {PythonText.Repr(scope)}");
        }
        if (!PolicyVocabulary.Operators.Contains(@operator))
        {
            throw new UnsupportedPredicateException($"unsupported policy operator {PythonText.Repr(@operator)}");
        }
        if (field.Length == 0)
        {
            throw new ArgumentException("field is required");
        }
        if (@operator is not ("exists" or "absent") && value is null or JsonNull)
        {
            throw new ArgumentException($"operator {PythonText.Repr(@operator)} requires a value");
        }
        Scope = scope;
        Field = field;
        Operator = @operator;
        Value = value is JsonNull ? null : value;
    }

    public string Scope { get; }

    public string Field { get; }

    public string Operator { get; }

    public JsonValue? Value { get; }

    public bool Matches(JsonObject context)
    {
        if (Operator == "exists")
        {
            return context.Contains(Field);
        }
        if (Operator == "absent")
        {
            return !context.Contains(Field);
        }
        if (!context.Contains(Field))
        {
            return false;
        }
        var actual = context.Require(Field);
        return Operator switch
        {
            "equals" => PythonValues.AreEqual(actual, Value!),
            "not_equals" => !PythonValues.AreEqual(actual, Value!),
            "in" => PythonValues.Contains(Value!, actual),
            _ => !PythonValues.Contains(Value!, actual),
        };
    }
}

/// <summary>Python's <c>==</c> and <c>in</c> over JSON values.</summary>
public static class PythonValues
{
    public static bool AreEqual(JsonValue left, JsonValue right) => (left, right) switch
    {
        (JsonNull, JsonNull) => true,
        (JsonString a, JsonString b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),
        (JsonArray a, JsonArray b) => a.Items.Length == b.Items.Length
                                      && a.Items.Zip(b.Items).All(pair => AreEqual(pair.First, pair.Second)),
        (JsonObject a, JsonObject b) => a.Keys.Length == b.Keys.Length
                                        && a.Keys.All(key => b.Contains(key) && AreEqual(a.Require(key), b.Require(key))),
        _ when Number(left) is { } a && Number(right) is { } b => a.CompareTo(b) == 0,
        _ => false,
    };

    /// <summary><c>needle in haystack</c>; the operand types Python refuses raise its <c>TypeError</c>.</summary>
    public static bool Contains(JsonValue haystack, JsonValue needle) => haystack switch
    {
        JsonString text => needle is JsonString part
            ? text.Value.Contains(part.Value, StringComparison.Ordinal)
            : throw new InvalidOperationException(
                $"TypeError: 'in <string>' requires string as left operand, not {PythonCoercion.TypeName(needle)}"),
        JsonArray list => list.Items.Any(item => AreEqual(item, needle)),
        JsonObject map => needle switch
        {
            JsonArray or JsonObject => throw new InvalidOperationException(
                $"TypeError: unhashable type: '{PythonCoercion.TypeName(needle)}'"),
            JsonString key => map.Contains(key.Value),
            _ => false,
        },
        _ => throw new InvalidOperationException(
            $"TypeError: argument of type '{PythonCoercion.TypeName(haystack)}' is not iterable"),
    };

    /// <summary>A JSON number or boolean as an exact comparable value; booleans are 0 and 1.</summary>
    private static ExactNumber? Number(JsonValue value) => value switch
    {
        JsonBool flag => new ExactNumber(flag.Value ? 1 : 0, null),
        JsonInteger integer => new ExactNumber(integer.Value, null),
        JsonFloat number => new ExactNumber(BigInteger.Zero, number.Value),
        _ => null,
    };

    private readonly record struct ExactNumber(BigInteger Integer, double? Float)
    {
        public int CompareTo(ExactNumber other) => (Float, other.Float) switch
        {
            (null, null) => Integer.CompareTo(other.Integer),
            ({ } a, { } b) => a == b ? 0 : a < b ? -1 : 1,
            ({ } a, null) => CompareFloat(a, other.Integer),
            (null, { } b) => -CompareFloat(b, Integer),
        };

        /// <summary>Exact, as Python compares a float with an int; NaN equals nothing.</summary>
        private static int CompareFloat(double value, BigInteger integer)
        {
            if (double.IsNaN(value))
            {
                return 2;
            }
            if (double.IsInfinity(value))
            {
                return value > 0 ? 1 : -1;
            }
            var floor = new BigInteger(Math.Floor(value));
            var order = floor.CompareTo(integer);
            return order != 0 ? order : value > Math.Floor(value) ? 1 : 0;
        }
    }
}

/// <summary>One immutable, numbered version of a rule id's history; all predicates must match.</summary>
public sealed record PolicyRule
{
    public PolicyRule(string ruleId, int version, string scope, string action, ImmutableArray<PolicyPredicate> predicates,
        BigInteger priority, BigInteger effectiveAt, string reason)
    {
        if (ruleId.Length == 0)
        {
            throw new ArgumentException("rule_id is required");
        }
        if (version <= 0)
        {
            throw new ArgumentException("version must be positive");
        }
        if (predicates.IsEmpty)
        {
            throw new ArgumentException("a rule must have at least one predicate");
        }
        if (effectiveAt < 0)
        {
            throw new ArgumentException("effective_at cannot be negative");
        }
        if (predicates.Any(predicate => predicate.Scope != scope))
        {
            throw new ArgumentException($"every predicate of rule {PythonText.Repr(ruleId)} must share the rule's own scope");
        }
        RuleId = ruleId;
        Version = version;
        Scope = scope;
        Action = action;
        Predicates = predicates;
        Priority = priority;
        EffectiveAt = effectiveAt;
        Reason = reason;
    }

    public string RuleId { get; }

    public int Version { get; }

    public string Scope { get; }

    public string Action { get; }

    public ImmutableArray<PolicyPredicate> Predicates { get; }

    public BigInteger Priority { get; }

    public BigInteger EffectiveAt { get; }

    public string Reason { get; }

    public bool Matches(JsonObject context) => Predicates.All(predicate => predicate.Matches(context));
}

public sealed record PolicyDecision(string Scope, bool Allowed, PolicyRule? Citation);

/// <summary>Per rule id append-only history; deny outranks allow, then priority, then the smallest rule id.</summary>
public sealed class PolicyRegistry
{
    private readonly Dictionary<string, List<PolicyRule>> versions = new(StringComparer.Ordinal);
    private readonly List<string> order = [];

    public PolicyRule Publish(string ruleId, string scope, string action, ImmutableArray<PolicyPredicate> predicates,
        BigInteger priority, BigInteger effectiveAt, string reason)
    {
        if (!versions.TryGetValue(ruleId, out var history))
        {
            versions[ruleId] = history = [];
            order.Add(ruleId);
        }
        var rule = new PolicyRule(ruleId, history.Count + 1, scope, action, predicates, priority, effectiveAt, reason);
        history.Add(rule);
        return rule;
    }

    public IReadOnlyList<PolicyRule> Versions(string ruleId) =>
        versions.TryGetValue(ruleId, out var history)
            ? history
            : throw new PolicyRuleNotFoundException($"no rule registered under id {PythonText.Repr(ruleId)}", ruleId);

    public PolicyRule Version(string ruleId, BigInteger number) =>
        Versions(ruleId).FirstOrDefault(rule => rule.Version == number)
        ?? throw new PolicyVersionNotFoundException($"rule {PythonText.Repr(ruleId)} has no version {number}", ruleId, number);

    /// <summary>An explicit snapshot, ordered by (rule id, version); one pin per rule id.</summary>
    public ImmutableArray<PolicyRule> ResolveVersions(IReadOnlyList<(string RuleId, BigInteger Version)> pins)
    {
        if (pins.Select(pin => pin.RuleId).Distinct(StringComparer.Ordinal).Count() != pins.Count)
        {
            throw new ArgumentException("a policy snapshot cannot pin the same rule id twice");
        }
        return [.. pins.OrderBy(pin => pin.RuleId, CodePointComparer.Instance).ThenBy(pin => pin.Version)
            .Select(pin => Version(pin.RuleId, pin.Version))];
    }

    /// <summary>Only each rule id's version effective at the instant participates. O(h + r * p).</summary>
    public PolicyDecision Evaluate(string scope, JsonObject context, BigInteger asOf)
    {
        var matching = new List<PolicyRule>();
        foreach (var ruleId in order)
        {
            var effective = versions[ruleId].Where(rule => rule.EffectiveAt <= asOf)
                .OrderByDescending(rule => rule.EffectiveAt).ThenByDescending(rule => rule.Version).FirstOrDefault();
            if (effective is not null && effective.Scope == scope && effective.Matches(context))
            {
                matching.Add(effective);
            }
        }
        return Decide(matching, scope, context);
    }

    /// <summary>An already-resolved rule set: rejects outrank allows; nothing matching is open by default. O(r * p).</summary>
    public static PolicyDecision Decide(IEnumerable<PolicyRule> rules, string scope, JsonObject context)
    {
        var matching = rules.Where(rule => rule.Scope == scope && rule.Matches(context)).ToList();
        var rejects = matching.Where(rule => rule.Action == "reject").ToList();
        var pool = rejects.Count > 0 ? rejects : matching.Where(rule => rule.Action == "allow").ToList();
        if (pool.Count == 0)
        {
            return new PolicyDecision(scope, true, null);
        }
        var winner = pool.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.RuleId, CodePointComparer.Instance).First();
        return new PolicyDecision(scope, winner.Action == "allow", winner);
    }
}
