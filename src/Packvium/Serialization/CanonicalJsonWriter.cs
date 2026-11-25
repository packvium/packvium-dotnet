using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Packvium.Serialization;

/// <summary>
/// A value has no canonical spelling. <see cref="Code"/> names the rule it broke:
/// <c>number_out_of_range</c>, <c>invalid_string</c> or <c>invalid_value</c>.
/// </summary>
public sealed class CanonicalJsonException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// RFC 8785 canonical JSON: the one spelling every engine agrees on
/// byte for byte, and the one JavaScript already writes.
///
/// - Object keys are sorted by their UTF-16 code units (<see cref="string.CompareOrdinal(string, string)"/>),
///   not by code point as <see cref="PythonJson"/> sorts them.
/// - Strings escape <c>"</c>, <c>\</c> and U+0000..U+001F (<c>\b \t \n \f \r</c> by name, the
///   rest as lowercase <c>\u00xx</c>); everything else, U+2028 included, is written as itself.
/// - Numbers are written as ECMAScript's <c>Number::toString</c> writes them.
/// - No whitespace.
///
/// A number whose magnitude exceeds 2^53 - 1, or is not finite, is refused, because JavaScript
/// already holds a different number by the time it can see it; a string carrying a lone UTF-16
/// surrogate is refused, because it has no UTF-8 spelling. O(n + k log k) for a document of size
/// n whose largest object has k keys.
/// </summary>
public static class CanonicalJsonWriter
{
    /// <summary>The largest magnitude every engine holds exactly.</summary>
    public const long MaxExactMagnitude = (1L << 53) - 1;

    private static readonly BigInteger MaxExactInteger = MaxExactMagnitude;

    public static string Write(JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        WriteValue(builder, value);
        return builder.ToString();
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        switch (value)
        {
            case JsonNull:
                builder.Append("null");
                break;
            case JsonBool boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case JsonString text:
                builder.Append(Quoted(text.Value));
                break;
            case JsonInteger integer:
                builder.Append(Integer(integer.Value));
                break;
            case JsonFloat number:
                builder.Append(Number(number.Value));
                break;
            case JsonObject obj:
                WriteObject(builder, obj);
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Items.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }
                    WriteValue(builder, array.Items[index]);
                }
                builder.Append(']');
                break;
            default:
                throw new CanonicalJsonException("invalid_value", $"a {value.GetType().Name} has no JSON spelling");
        }
    }

    /// <summary>Every key is spelled, and so checked, before any value is written, as the reference does.</summary>
    private static void WriteObject(StringBuilder builder, JsonObject obj)
    {
        var spelled = obj.Keys.Select(key => (Key: key, Spelling: Quoted(key)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();
        builder.Append('{');
        for (var index = 0; index < spelled.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            builder.Append(spelled[index].Spelling).Append(':');
            WriteValue(builder, obj.Require(spelled[index].Key));
        }
        builder.Append('}');
    }

    private static string Quoted(string text) =>
        Units.CanonicalText.Quote(text)
        ?? throw new CanonicalJsonException("invalid_string", "a string carries a lone UTF-16 surrogate");

    private static string Integer(BigInteger value) => BigInteger.Abs(value) > MaxExactInteger
        ? throw new CanonicalJsonException("number_out_of_range", $"{value} is beyond what every engine holds exactly")
        : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ECMAScript's <c>Number::toString</c>: the shortest round-trip digits, written plainly while
    /// the decimal point falls within 21 digits of them and six places below the units, and in
    /// exponent form otherwise (<c>1e+21</c>, <c>1e-7</c>). Integral values carry no fraction.
    /// </summary>
    private static string Number(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxExactMagnitude)
        {
            throw new CanonicalJsonException("number_out_of_range",
                $"{PythonFloat.Repr(value)} is beyond what every engine holds exactly");
        }
        if (value == 0)
        {
            return "0";
        }
        var (negative, digits, exponent) = PythonFloat.ShortestDigits(value);
        var count = digits.Length;
        var point = exponent + 1;
        string rendered;
        if (count <= point && point <= 21)
        {
            rendered = digits + new string('0', point - count);
        }
        else if (point is > 0 and <= 21)
        {
            rendered = digits[..point] + "." + digits[point..];
        }
        else if (point is > -6 and <= 0)
        {
            rendered = "0." + new string('0', -point) + digits;
        }
        else
        {
            var power = point - 1;
            rendered = digits[..1] + (count > 1 ? "." + digits[1..] : string.Empty)
                       + "e" + (power >= 0 ? "+" : "-") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        }
        return (negative ? "-" : string.Empty) + rendered;
    }
}
