using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Python's <c>str()</c> and <c>repr()</c> for the values an issue detail prints. The reference
/// builds every detail with an f-string, so a detail carries Python's spelling of whatever it
/// names -- <c>None</c>, <c>True</c>, <c>'quoted'</c>, <c>(1, 2, 3)</c>, <c>1e-05</c> -- and
/// matching it is what lets one issue list be compared across languages byte for byte.
/// </summary>
internal static class PyFormat
{
    /// <summary><c>str(value)</c>: a string as itself, anything else as its <c>repr</c>.</summary>
    public static string Str(JsonValue value) => PythonRepr.Str(value);

    public static string Repr(JsonValue value) => PythonRepr.Repr(value);

    /// <summary><c>repr(str)</c>, escaped per code point against the Unicode 14.0.0 table.</summary>
    public static string StringRepr(string text) => PythonText.Repr(text);

    public static string Int(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>(a, b)</c> from already-formatted elements; a one-element tuple keeps its comma.</summary>
    public static string TupleOf(IReadOnlyList<string> elements) =>
        elements.Count == 1 ? $"({elements[0]},)" : $"({string.Join(", ", elements)})";

    public static string ListOf(IEnumerable<string> elements) => $"[{string.Join(", ", elements)}]";

    public static string IntTuple(IEnumerable<BigInteger> values) => TupleOf([.. values.Select(Int)]);

    public static string IntList(IEnumerable<BigInteger> values) => ListOf(values.Select(Int));

    public static string ValueTuple(IEnumerable<JsonValue> values) => TupleOf([.. values.Select(Repr)]);

    /// <summary>A tuple of JSON values, or <c>None</c> where the reference holds no tuple.</summary>
    public static string OptionalTuple(IReadOnlyList<JsonValue>? values) => values is null ? "None" : ValueTuple(values);

    public static string Bool(bool flag) => flag ? "True" : "False";

    /// <summary>
    /// <c>f"{value:.6f}"</c>: the exact binary value rounded half to even at six decimals, as
    /// Python rounds it; a negative value keeps its sign even when it rounds to zero.
    /// </summary>
    public static string Fixed6(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xFFFFFFFFFFFFFL;
        var mantissa = new BigInteger(exponent == 0 ? fraction : fraction | (1L << 52));
        var power = (exponent == 0 ? 1 : exponent) - 1075;
        var scaled = mantissa * 1_000_000;
        var rounded = power >= 0 ? scaled << power : RoundHalfEven(scaled, BigInteger.One << -power);
        var digits = rounded.ToString(CultureInfo.InvariantCulture).PadLeft(7, '0');
        var text = new StringBuilder();
        if (bits < 0)
        {
            text.Append('-');
        }
        return text.Append(digits.AsSpan(0, digits.Length - 6)).Append('.').Append(digits.AsSpan(digits.Length - 6)).ToString();
    }

    private static BigInteger RoundHalfEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var order = (remainder * 2).CompareTo(denominator);
        return order > 0 || (order == 0 && !quotient.IsEven) ? quotient + 1 : quotient;
    }
}
