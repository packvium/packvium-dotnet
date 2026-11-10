using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>
/// The built-in conversions the reference request reader applies to raw JSON values --
/// <c>int()</c>, <c>float()</c>, <c>bool()</c>, <c>str()</c>, iteration -- for every scalar JSON
/// can carry. Python would also stringify an array or object through <c>repr</c>; no schema-valid
/// request can reach that, so it is refused here instead of reproduced.
/// </summary>
internal static class PythonCoercion
{
    public static long Int(JsonValue value) => ToLong(BigInt(value));

    public static BigInteger BigInt(JsonValue value) => value switch
    {
        JsonInteger integer => integer.Value,
        JsonBool boolean => boolean.Value ? 1 : 0,
        JsonFloat number when double.IsNaN(number.Value) =>
            throw new ArgumentException("cannot convert float NaN to integer"),
        JsonFloat number when double.IsInfinity(number.Value) =>
            throw new OverflowException("cannot convert float infinity to integer"),
        JsonFloat number => new BigInteger(Math.Truncate(number.Value)),
        JsonString text => ParseInt(text.Value),
        _ => throw new ArgumentException(
            $"int() argument must be a string, a bytes-like object or a real number, not '{TypeName(value)}'"),
    };

    public static double Float(JsonValue value) => value switch
    {
        JsonInteger integer => DecimalRendering.TrueDivide(integer.Value, BigInteger.One),
        JsonBool boolean => boolean.Value ? 1.0 : 0.0,
        JsonFloat number => number.Value,
        JsonString text => ParseFloat(text.Value),
        _ => throw new ArgumentException(
            $"float() argument must be a string or a real number, not '{TypeName(value)}'"),
    };

    /// <summary>Python truthiness.</summary>
    public static bool Bool(JsonValue value) => value switch
    {
        JsonNull => false,
        JsonBool boolean => boolean.Value,
        JsonInteger integer => !integer.Value.IsZero,
        JsonFloat number => number.Value != 0.0,
        JsonString text => text.Value.Length > 0,
        JsonArray array => !array.Items.IsEmpty,
        JsonObject obj => !obj.Keys.IsEmpty,
        _ => throw new ArgumentException($"not a JSON value: {value.GetType().Name}"),
    };

    public static string Str(JsonValue value) => value switch
    {
        JsonString text => text.Value,
        JsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        JsonBool boolean => boolean.Value ? "True" : "False",
        JsonNull => "None",
        JsonFloat number => PythonFloat.Repr(number.Value),
        _ => throw new ArgumentException($"a {TypeName(value)} is not accepted where a string is expected"),
    };

    /// <summary>
    /// A value passed through untouched and later compared as an integer: Python accepts an int
    /// or a bool there, and nothing else survives the comparison.
    /// </summary>
    public static long? RawInt(JsonValue? value) => value switch
    {
        null or JsonNull => null,
        JsonInteger or JsonBool => Int(value),
        _ => throw new ArgumentException($"'<' not supported between instances of '{TypeName(value)}' and 'int'"),
    };

    /// <summary>What <c>for x in value</c> yields: array items, object keys, or a string's characters.</summary>
    public static IEnumerable<JsonValue> Iterate(JsonValue value) => value switch
    {
        JsonArray array => array.Items,
        JsonObject obj => obj.Keys.Select(key => (JsonValue)new JsonString(key)),
        JsonString text => text.Value.EnumerateRunes().Select(rune => (JsonValue)new JsonString(rune.ToString())),
        _ => throw new ArgumentException($"'{TypeName(value)}' object is not iterable"),
    };

    /// <summary>Set members: strings only, which is all the schema admits.</summary>
    public static IEnumerable<string> StringMembers(JsonValue value) =>
        Iterate(value).Select(member => member is JsonString text
            ? text.Value
            : throw new ArgumentException($"a {TypeName(member)} member is not accepted where a string is expected"));

    public static JsonObject Object(JsonValue value) =>
        value as JsonObject ?? throw new ArgumentException($"'{TypeName(value)}' object has no attribute 'get'");

    public static string TypeName(JsonValue value) => value switch
    {
        JsonNull => "NoneType",
        JsonBool => "bool",
        JsonInteger => "int",
        JsonFloat => "float",
        JsonString => "str",
        JsonArray => "list",
        _ => "dict",
    };

    private static long ToLong(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? (long)value
            : throw new OverflowException("integer exceeds the 64-bit range this engine carries");

    /// <summary><c>int(str)</c>: surrounding whitespace, a sign, digits with single underscores between them.</summary>
    private static BigInteger ParseInt(string text)
    {
        var ascii = AsciiNumber(PythonText.Strip(text));
        var body = ascii.TrimStart('+', '-');
        var signs = ascii.Length - body.Length;
        if (signs > 1 || !ValidUnderscoredDigits(body))
        {
            throw new ArgumentException($"invalid literal for int() with base 10: '{text}'");
        }
        var magnitude = BigInteger.Parse(body.Replace("_", string.Empty, StringComparison.Ordinal),
            CultureInfo.InvariantCulture);
        return ascii.StartsWith('-') ? -magnitude : magnitude;
    }

    /// <summary><c>float(str)</c>: decimal or exponent form, PEP 515 underscores, <c>inf</c>/<c>nan</c>.</summary>
    private static double ParseFloat(string text)
    {
        var ascii = AsciiNumber(PythonText.Strip(text));
        var body = ascii.Length > 0 && (ascii[0] == '+' || ascii[0] == '-') ? ascii[1..] : ascii;
        var negative = ascii.StartsWith('-');
        var special = body.ToLowerInvariant() switch
        {
            "inf" or "infinity" => double.PositiveInfinity,
            "nan" => double.NaN,
            _ => (double?)null,
        };
        if (special is { } value)
        {
            return negative ? -value : value;
        }
        if (!ValidFloatBody(body))
        {
            throw new ArgumentException($"could not convert string to float: '{text}'");
        }
        return double.Parse(ascii.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static bool ValidFloatBody(string body)
    {
        var exponentAt = body.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? body : body[..exponentAt];
        if (exponentAt >= 0)
        {
            var exponent = body[(exponentAt + 1)..].TrimStart('+', '-');
            if (body.Length - exponentAt - 1 - exponent.Length > 1 || !ValidUnderscoredDigits(exponent))
            {
                return false;
            }
        }
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        if (point < 0)
        {
            return ValidUnderscoredDigits(mantissa);
        }
        var whole = mantissa[..point];
        var fraction = mantissa[(point + 1)..];
        return (whole.Length > 0 || fraction.Length > 0)
               && (whole.Length == 0 || ValidUnderscoredDigits(whole))
               && (fraction.Length == 0 || ValidUnderscoredDigits(fraction));
    }

    private static bool ValidUnderscoredDigits(string digits)
    {
        if (digits.Length == 0 || digits[0] == '_' || digits[^1] == '_')
        {
            return false;
        }
        for (var index = 0; index < digits.Length; index++)
        {
            var character = digits[index];
            if (character == '_' ? digits[index - 1] == '_' : !char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Non-ASCII decimal digits read as their values, as Python's number parsers do.</summary>
    private static string AsciiNumber(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var digit = rune.IsAscii ? -1 : PythonText.DecimalValue(rune);
            if (digit >= 0)
            {
                builder.Append((char)('0' + digit));
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }
        return builder.ToString();
    }
}
