using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// The decoded documents read the way the reference's Python reads them: <c>d.get</c>,
/// <c>d[key]</c>, iteration, <c>in</c>, truthiness, <c>==</c>, ordering and hashing, each with
/// Python's rule for every JSON value type. Anything Python would answer with a <c>KeyError</c>,
/// <c>TypeError</c> or <c>AttributeError</c> is a <see cref="MalformedResultException.Shape"/> refusal.
/// </summary>
internal static class Json
{
    public static readonly JsonObject EmptyObject = new([]);

    public static readonly JsonArray EmptyList = new([]);

    /// <summary>Python's <c>value.get(key, fallback)</c>; a non-object has no <c>.get</c>.</summary>
    public static JsonValue Get(JsonValue value, string key, JsonValue? fallback = null)
    {
        if (value is not JsonObject record)
        {
            throw MalformedResultException.ShapeBreak($"expected an object holding \"{key}\", got {Kind(value)}");
        }
        return record.Get(key) ?? fallback ?? JsonNull.Instance;
    }

    /// <summary>Python's <c>value[key]</c> with a string key.</summary>
    public static JsonValue At(JsonValue value, string key) =>
        value is JsonObject record && record.Get(key) is { } found
            ? found
            : throw MalformedResultException.ShapeBreak($"missing \"{key}\"");

    /// <summary>Python's <c>key in value</c> for a string key: an object's keys, a list's elements, a string's substrings.</summary>
    public static bool Contains(JsonValue value, string key) => value switch
    {
        JsonObject record => record.Contains(key),
        JsonArray list => list.Items.Any(item => item is JsonString text && text.Value == key),
        JsonString text => text.Value.Contains(key, StringComparison.Ordinal),
        _ => throw MalformedResultException.ShapeBreak($"{Kind(value)} cannot hold \"{key}\""),
    };

    /// <summary>
    /// Python's <c>needle in haystack</c> for any needle: an object's keys by hash, a list's elements
    /// by <c>==</c>, a string's substrings (a non-string needle there is a <c>TypeError</c>).
    /// </summary>
    public static bool In(JsonValue needle, JsonValue haystack)
    {
        switch (haystack)
        {
            case JsonObject record:
                HashKey(needle);
                return needle is JsonString key && record.Contains(key.Value);
            case JsonArray list:
                return list.Items.Any(item => PyEquals(item, needle));
            case JsonString text when needle is JsonString part:
                return text.Value.Contains(part.Value, StringComparison.Ordinal);
            default:
                throw MalformedResultException.ShapeBreak($"cannot look for {Kind(needle)} in {Kind(haystack)}");
        }
    }

    /// <summary>
    /// What <c>for element in value</c> walks: a list's elements, an object's keys, a string's code
    /// points. Anything else is not iterable.
    /// </summary>
    public static ImmutableArray<JsonValue> Items(JsonValue value, string what) => value switch
    {
        JsonArray list => list.Items,
        JsonObject record => [.. record.Keys.Select(key => (JsonValue)new JsonString(key))],
        JsonString text => [.. CodePoints(text.Value).Select(point => (JsonValue)new JsonString(point))],
        _ => throw MalformedResultException.ShapeBreak($"{what} must be a list, got {Kind(value)}"),
    };

    /// <summary>Python's <c>len(value)</c>.</summary>
    public static int Length(JsonValue value, string what) => value switch
    {
        JsonArray list => list.Items.Length,
        JsonObject record => record.Keys.Length,
        JsonString text => CodePoints(text.Value).Count(),
        _ => throw MalformedResultException.ShapeBreak($"{what} has no length, being {Kind(value)}"),
    };

    public static string Text(JsonValue value, string what) =>
        value is JsonString text ? text.Value : throw MalformedResultException.ShapeBreak($"{what} must be a string, got {Kind(value)}");

    /// <summary>
    /// An integer the reference does arithmetic on. A boolean is Python's <c>int</c>; a float would
    /// carry the reference into float arithmetic this validator does not follow, so it is refused.
    /// </summary>
    public static BigInteger Int(JsonValue value, string what) =>
        IntInstance(value) ?? throw MalformedResultException.ShapeBreak($"{what} must be an integer, got {Kind(value)}");

    public static BigInteger? OptionalInt(JsonValue value, string what) => value is JsonNull ? null : Int(value, what);

    /// <summary><c>isinstance(value, int)</c>, which a boolean satisfies.</summary>
    public static BigInteger? IntInstance(JsonValue value) => value switch
    {
        JsonInteger integer => integer.Value,
        JsonBool flag => flag.Value ? BigInteger.One : BigInteger.Zero,
        _ => null,
    };

    /// <summary><c>isinstance(value, int) and not isinstance(value, bool)</c>.</summary>
    public static BigInteger? StrictInt(JsonValue value) => value is JsonInteger integer ? integer.Value : null;

    /// <summary>Python's <c>int(value)</c>: a number truncates, a string parses as a base-10 integer.</summary>
    public static BigInteger PyInt(JsonValue value, string what)
    {
        switch (value)
        {
            case JsonInteger or JsonBool:
                return IntInstance(value)!.Value;
            case JsonFloat number when double.IsFinite(number.Value):
                return new BigInteger(Math.Truncate(number.Value));
            case JsonString text when ParseDecimalInteger(text.Value) is { } parsed:
                return parsed;
            default:
                throw MalformedResultException.ShapeBreak($"{what}: int() cannot convert {Kind(value)}");
        }
    }

    /// <summary>Python truthiness.</summary>
    public static bool Truthy(JsonValue value) => value switch
    {
        JsonNull => false,
        JsonBool flag => flag.Value,
        JsonInteger integer => !integer.Value.IsZero,
        JsonFloat number => number.Value != 0,
        JsonString text => text.Value.Length > 0,
        JsonArray list => list.Items.Length > 0,
        JsonObject record => record.Keys.Length > 0,
        _ => true,
    };

    /// <summary>
    /// Python <c>==</c>: a boolean equals its integer, an integer equals the float of the same value,
    /// lists compare element-wise and objects regardless of key order.
    /// </summary>
    public static bool PyEquals(JsonValue left, JsonValue right)
    {
        if (Number.Of(left) is { } leftNumber)
        {
            return Number.Of(right) is { } rightNumber && leftNumber.CompareTo(rightNumber) == 0;
        }
        return (left, right) switch
        {
            (JsonNull, JsonNull) => true,
            (JsonString a, JsonString b) => a.Value == b.Value,
            (JsonArray a, JsonArray b) => a.Items.Length == b.Items.Length
                && a.Items.Zip(b.Items).All(pair => PyEquals(pair.First, pair.Second)),
            (JsonObject a, JsonObject b) => a.Keys.Length == b.Keys.Length
                && a.Members.All(member => b.Get(member.Key) is { } other && PyEquals(member.Value, other)),
            _ => false,
        };
    }

    /// <summary><c>value == number</c> for an exact integer.</summary>
    public static bool EqualsInt(JsonValue value, BigInteger number) =>
        Number.Of(value) is { } read && read.CompareTo(Number.Integer(number)) == 0;

    /// <summary><c>value == text</c>.</summary>
    public static bool IsText(JsonValue value, string text) => value is JsonString read && read.Value == text;

    /// <summary><c>value is True</c>: identity with the boolean, which no integer has.</summary>
    public static bool IsTrue(JsonValue value) => value is JsonBool { Value: true };

    /// <summary>
    /// Python's ordering for what the reference sorts or compares: numbers with numbers, strings
    /// with strings by code point, lists element-wise. Anything else is Python's <c>TypeError</c>.
    /// </summary>
    public static int Compare(JsonValue left, JsonValue right)
    {
        if (Number.Of(left) is { } leftNumber && Number.Of(right) is { } rightNumber)
        {
            return leftNumber.CompareTo(rightNumber);
        }
        if (left is JsonString leftText && right is JsonString rightText)
        {
            return Math.Sign(CodePointComparer.Instance.Compare(leftText.Value, rightText.Value));
        }
        if (left is JsonArray leftList && right is JsonArray rightList)
        {
            for (var index = 0; index < leftList.Items.Length && index < rightList.Items.Length; index++)
            {
                if (!PyEquals(leftList.Items[index], rightList.Items[index]))
                {
                    return Compare(leftList.Items[index], rightList.Items[index]);
                }
            }
            return leftList.Items.Length.CompareTo(rightList.Items.Length);
        }
        throw MalformedResultException.ShapeBreak($"cannot order {Kind(left)} against {Kind(right)}");
    }

    /// <summary><c>value &lt; number</c> style comparisons against an exact integer.</summary>
    public static int CompareInt(JsonValue value, BigInteger number) => Compare(value, new JsonInteger(number));

    /// <summary>Python's stable <c>sorted</c> under <see cref="Compare"/>. O(n log n) comparisons.</summary>
    public static ImmutableArray<JsonValue> Sorted(IEnumerable<JsonValue> values) => [.. StableSort([.. values], Compare)];

    /// <summary>
    /// A stable merge sort. The framework's sorts wrap an exception thrown by the comparison, and
    /// a comparison here throws the refusal Python's <c>TypeError</c> stands for, which must reach
    /// the caller as it is. O(n log n) comparisons, O(n) space.
    /// </summary>
    public static List<T> StableSort<T>(List<T> values, Comparison<T> comparison)
    {
        if (values.Count < 2)
        {
            return values;
        }
        var middle = values.Count / 2;
        var left = StableSort(values.GetRange(0, middle), comparison);
        var right = StableSort(values.GetRange(middle, values.Count - middle), comparison);
        var merged = new List<T>(values.Count);
        var (leftIndex, rightIndex) = (0, 0);
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            merged.Add(comparison(right[rightIndex], left[leftIndex]) < 0 ? right[rightIndex++] : left[leftIndex++]);
        }
        merged.AddRange(left.Skip(leftIndex));
        merged.AddRange(right.Skip(rightIndex));
        return merged;
    }

    /// <summary>
    /// The identity Python hashes a set member or dict key by, so <c>1</c>, <c>1.0</c> and
    /// <c>True</c> share one. A list or an object is unhashable.
    /// </summary>
    public static string HashKey(JsonValue value)
    {
        if (Number.Of(value) is { } number)
        {
            return number.HashKey();
        }
        return value switch
        {
            JsonNull => "null",
            JsonString text => "s" + text.Value,
            _ => throw MalformedResultException.ShapeBreak($"{Kind(value)} is unhashable"),
        };
    }

    public static string Kind(JsonValue value) => value switch
    {
        JsonNull => "null",
        JsonBool => "a boolean",
        JsonInteger => "an integer",
        JsonFloat => "a number",
        JsonString => "a string",
        JsonArray => "a list",
        _ => "an object",
    };

    /// <summary>The code points of <paramref name="text"/> as Python stores them; a lone surrogate is its own.</summary>
    public static IEnumerable<string> CodePoints(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var paired = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            yield return text.Substring(index, paired ? 2 : 1);
            if (paired)
            {
                index++;
            }
        }
    }

    /// <summary>
    /// <c>int(text)</c> for a string: optional surrounding whitespace, an optional sign, ASCII digits
    /// that single underscores may group. Null when Python would raise <c>ValueError</c>.
    /// </summary>
    private static BigInteger? ParseDecimalInteger(string text)
    {
        var body = PythonText.Strip(text);
        var negative = body.StartsWith('-');
        if (negative || body.StartsWith('+'))
        {
            body = body[1..];
        }
        if (!IsDigitGroups(body))
        {
            return null;
        }
        var magnitude = BigInteger.Parse(body.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -magnitude : magnitude;
    }

    /// <summary>Whether <paramref name="text"/> is <c>\d+(_\d+)*</c> over ASCII digits.</summary>
    internal static bool IsDigitGroups(string text)
    {
        if (text.Length == 0 || text[0] == '_' || text[^1] == '_')
        {
            return false;
        }
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '_' ? text[index - 1] == '_' : character is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }
}
