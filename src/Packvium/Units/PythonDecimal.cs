using System;
using System.Numerics;
using System.Text;

namespace Packvium.Units;

/// <summary>
/// <c>Fraction(Decimal(text))</c>: the reference engine's last-resort reading of a quantity.
/// <c>Decimal</c> accepts more than a plain decimal -- an exponent, a leading or trailing dot,
/// underscores anywhere, any Unicode decimal digit -- and every one of those is a request the
/// reference engine answers, so this reads exactly that grammar.
/// </summary>
internal static class PythonDecimal
{
    /// <summary>
    /// Beyond this an exponent names a number no tick count can hold or a fraction no rounding
    /// keeps; refusing it bounds the work a single quantity can ask for.
    /// </summary>
    private const int MaxExponentMagnitude = 100_000;

    public static Rational ToRational(string text)
    {
        var ascii = ToAscii(PythonText.Strip(text));
        var position = 0;
        var negative = false;
        if (position < ascii.Length && (ascii[position] == '+' || ascii[position] == '-'))
        {
            negative = ascii[position] == '-';
            position++;
        }
        var rest = ascii[position..];
        if (IsSpecial(rest, out var special))
        {
            throw special;
        }
        return Finite(ascii, position, negative);
    }

    /// <summary>
    /// CPython's <c>numeric_as_ascii</c>: underscores dropped, whitespace mapped to a space the
    /// grammar then refuses, every other non-ASCII character read as its decimal value.
    /// </summary>
    private static string ToAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '_')
            {
                continue;
            }
            if (rune.Value is > 0 and <= 127)
            {
                builder.Append((char)rune.Value);
                continue;
            }
            if (PythonText.IsSpace(rune))
            {
                builder.Append(' ');
                continue;
            }
            var digit = PythonText.DecimalValue(rune);
            if (digit < 0)
            {
                throw ConversionSyntax();
            }
            builder.Append((char)('0' + digit));
        }
        return builder.ToString();
    }

    private static bool IsSpecial(string text, out Exception error)
    {
        var lowered = text.ToLowerInvariant();
        if (lowered is "inf" or "infinity")
        {
            error = new OverflowException("cannot convert Infinity to integer ratio");
            return true;
        }
        var nanDigits = lowered.StartsWith("snan", StringComparison.Ordinal) ? lowered[4..]
            : lowered.StartsWith("nan", StringComparison.Ordinal) ? lowered[3..]
            : null;
        if (nanDigits is not null && AllAsciiDigits(nanDigits))
        {
            error = new ArgumentException("cannot convert NaN to integer ratio");
            return true;
        }
        error = null!;
        return false;
    }

    private static Rational Finite(string ascii, int position, bool negative)
    {
        var coefficient = BigInteger.Zero;
        var integerDigits = 0;
        var fractionalDigits = 0;
        while (position < ascii.Length && char.IsAsciiDigit(ascii[position]))
        {
            coefficient = coefficient * 10 + (ascii[position++] - '0');
            integerDigits++;
        }
        if (position < ascii.Length && ascii[position] == '.')
        {
            position++;
            while (position < ascii.Length && char.IsAsciiDigit(ascii[position]))
            {
                coefficient = coefficient * 10 + (ascii[position++] - '0');
                fractionalDigits++;
            }
        }
        if (integerDigits + fractionalDigits == 0)
        {
            throw ConversionSyntax();
        }
        var exponent = ReadExponent(ascii, position) - fractionalDigits;
        var signed = negative ? -coefficient : coefficient;
        return exponent >= 0
            ? Rational.Of(signed * BigInteger.Pow(10, exponent))
            : new Rational(signed, BigInteger.Pow(10, -exponent));
    }

    private static int ReadExponent(string ascii, int position)
    {
        if (position == ascii.Length)
        {
            return 0;
        }
        if (ascii[position] != 'e' && ascii[position] != 'E')
        {
            throw ConversionSyntax();
        }
        var digits = ascii[(position + 1)..];
        var negative = digits.StartsWith('-');
        if (digits.StartsWith('+') || negative)
        {
            digits = digits[1..];
        }
        if (digits.Length == 0 || !AllAsciiDigits(digits))
        {
            throw ConversionSyntax();
        }
        var magnitude = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (magnitude > MaxExponentMagnitude)
        {
            throw new OverflowException("decimal exponent is beyond any representable quantity");
        }
        return negative ? -(int)magnitude : (int)magnitude;
    }

    private static bool AllAsciiDigits(string text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static ArithmeticException ConversionSyntax() =>
        new("[<class 'decimal.ConversionSyntax'>]");
}
