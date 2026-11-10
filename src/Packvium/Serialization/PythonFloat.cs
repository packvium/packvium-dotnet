using System;
using System.Globalization;
using System.Text;

namespace Packvium.Serialization;

/// <summary>Python's <c>repr(float)</c>, which is also <c>str(float)</c> and how <c>json</c> writes a float.</summary>
public static class PythonFloat
{
    /// <summary>
    /// The shortest digits that round-trip, written plainly when the decimal exponent is in
    /// [-4, 16) and in scientific form otherwise, with a two-digit minimum exponent.
    /// </summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }
        var (negative, digits, exponent) = ShortestDigits(value);
        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }
        if (exponent is >= -4 and < 16)
        {
            AppendPlain(builder, digits, exponent);
        }
        else
        {
            AppendScientific(builder, digits, exponent);
        }
        return builder.ToString();
    }

    /// <summary>The shortest round-trip digits and the decimal exponent of the first one.</summary>
    private static (bool Negative, string Digits, int Exponent) ShortestDigits(double value)
    {
        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = roundTrip.StartsWith('-') || (value == 0 && double.IsNegative(value));
        var mantissa = roundTrip.TrimStart('-');
        var exponent = 0;
        var marker = mantissa.IndexOf('E', StringComparison.Ordinal);
        if (marker >= 0)
        {
            exponent = int.Parse(mantissa[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = mantissa[..marker];
        }
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = point < 0 ? mantissa.Length : point;
        var allDigits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var leadingZeros = allDigits.Length - allDigits.TrimStart('0').Length;
        var significant = allDigits.Trim('0');
        if (significant.Length == 0)
        {
            return (negative, "0", 0);
        }
        return (negative, significant, exponent + integerDigits - 1 - leadingZeros);
    }

    private static void AppendPlain(StringBuilder builder, string digits, int exponent)
    {
        if (exponent < 0)
        {
            builder.Append("0.").Append('0', -exponent - 1).Append(digits);
            return;
        }
        if (digits.Length <= exponent + 1)
        {
            builder.Append(digits).Append('0', exponent + 1 - digits.Length).Append(".0");
            return;
        }
        builder.Append(digits, 0, exponent + 1).Append('.').Append(digits, exponent + 1, digits.Length - exponent - 1);
    }

    private static void AppendScientific(StringBuilder builder, string digits, int exponent)
    {
        builder.Append(digits[0]);
        if (digits.Length > 1)
        {
            builder.Append('.').Append(digits, 1, digits.Length - 1);
        }
        builder.Append('e').Append(exponent < 0 ? '-' : '+')
            .Append(Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture));
    }
}
