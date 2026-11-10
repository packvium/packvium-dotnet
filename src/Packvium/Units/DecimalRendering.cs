using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Packvium.Units;

/// <summary>
/// The fixed-decimal strings a result carries in its <c>value</c> fields, rendered exactly as
/// <c>format((Decimal(n) / Decimal(d)).quantize(Decimal(1).scaleb(-places)).normalize(), "f")</c>
/// does under Python's default decimal context.
/// </summary>
/// <remarks>
/// That expression rounds twice, both times ties-to-even: the division to the context's 28
/// significant digits, then the quotient to <c>places</c> decimals. A quotient that repeats --
/// any tick count rendered in inches, whose divisor carries a factor of 127 -- can land on an
/// exact half after the first rounding that a single rounding would not see, so both steps are
/// reproduced rather than one exact rounding.
/// </remarks>
public static class DecimalRendering
{
    /// <summary>Python's default <c>decimal</c> context precision.</summary>
    private const int ContextPrecision = 28;

    public static string Render(BigInteger numerator, BigInteger denominator, int places)
    {
        if (numerator.Sign < 0 || denominator.Sign <= 0)
        {
            throw new ArgumentException("decimal rendering takes a non-negative numerator and a positive denominator");
        }
        if (numerator.IsZero)
        {
            return "0";
        }
        var leading = LeadingDigitExponent(numerator, denominator);
        var significantScale = ContextPrecision - 1 - leading;
        var contextRounded = RoundScaled(numerator, denominator, significantScale);
        var quantized = contextRounded.Multiply(BigInteger.Pow(10, places)).Round(Rounding.Nearest);
        if (quantized.ToString(CultureInfo.InvariantCulture).Length > ContextPrecision)
        {
            throw new ArithmeticException("[<class 'decimal.InvalidOperation'>]");
        }
        return Format(quantized, places);
    }

    /// <summary>The <c>k</c> with <c>10^k &lt;= n/d &lt; 10^(k+1)</c>.</summary>
    private static int LeadingDigitExponent(BigInteger numerator, BigInteger denominator)
    {
        var estimate = numerator.ToString(CultureInfo.InvariantCulture).Length
            - denominator.ToString(CultureInfo.InvariantCulture).Length;
        while (Compare(numerator, denominator, estimate) < 0)
        {
            estimate--;
        }
        while (Compare(numerator, denominator, estimate + 1) >= 0)
        {
            estimate++;
        }
        return estimate;
    }

    /// <summary>Sign of <c>n/d - 10^k</c>.</summary>
    private static int Compare(BigInteger numerator, BigInteger denominator, int exponent) =>
        exponent >= 0
            ? numerator.CompareTo(denominator * BigInteger.Pow(10, exponent))
            : (numerator * BigInteger.Pow(10, -exponent)).CompareTo(denominator);

    /// <summary><c>n/d</c> rounded ties-to-even to <paramref name="scale"/> decimals, as an exact rational.</summary>
    private static Rational RoundScaled(BigInteger numerator, BigInteger denominator, int scale)
    {
        var scaled = scale >= 0
            ? new Rational(numerator * BigInteger.Pow(10, scale), denominator)
            : new Rational(numerator, denominator * BigInteger.Pow(10, -scale));
        var rounded = scaled.Round(Rounding.Nearest);
        return scale >= 0
            ? new Rational(rounded, BigInteger.Pow(10, scale))
            : Rational.Of(rounded * BigInteger.Pow(10, -scale));
    }

    /// <summary><c>normalize()</c> then <c>format(..., "f")</c>: trailing zeros dropped, no exponent.</summary>
    private static string Format(BigInteger scaledValue, int places)
    {
        if (scaledValue.IsZero)
        {
            return "0";
        }
        var digits = scaledValue.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        var integerPart = digits[..^places];
        var fractionalPart = digits[^places..].TrimEnd('0');
        return fractionalPart.Length == 0 ? integerPart : integerPart + "." + fractionalPart;
    }

    /// <summary>
    /// Python's <c>f"{value:.{places}f}"</c> for a binary double: the exact value the double
    /// holds, rounded ties-to-even. .NET's own fixed format rounds an exact tie away from zero,
    /// which renders 1/128 as <c>0.007813</c> where the reference engine writes <c>0.007812</c>.
    /// </summary>
    public static string FormatFixed(double value, int places)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("only a finite double has a fixed-point rendering");
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponentBits != 0)
        {
            mantissa |= 1L << 52;
        }
        var binaryExponent = (exponentBits == 0 ? 1 : exponentBits) - 1075;
        var exact = binaryExponent >= 0
            ? Rational.Of(new BigInteger(mantissa) << binaryExponent)
            : new Rational(mantissa, BigInteger.One << -binaryExponent);
        var scaled = exact.Multiply(BigInteger.Pow(10, places)).Round(Rounding.Nearest);
        var digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }
        builder.Append(digits[..^places]);
        if (places > 0)
        {
            builder.Append('.').Append(digits[^places..]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// <c>int / int</c> in Python: the double nearest the exact quotient, ties-to-even. Converting
    /// both operands to <see cref="double"/> first would round each, then round again.
    /// </summary>
    public static double TrueDivide(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("division by zero");
        }
        if (numerator.IsZero)
        {
            return 0.0;
        }
        var negative = numerator.Sign != denominator.Sign;
        var n = BigInteger.Abs(numerator);
        var d = BigInteger.Abs(denominator);
        // Scale so the integer quotient carries 55 significant bits: 53 kept, one rounding
        // bit, one more so the shift below never loses the rounding bit's position.
        var shift = 55 - (int)(n.GetBitLength() - d.GetBitLength());
        var quotient = BigInteger.DivRem(shift >= 0 ? n << shift : n, shift >= 0 ? d : d << -shift, out var remainder);
        var excess = (int)quotient.GetBitLength() - 53;
        var kept = quotient >> excess;
        var dropped = quotient - (kept << excess);
        var half = BigInteger.One << (excess - 1);
        var comparison = dropped.CompareTo(half);
        if (comparison > 0 || (comparison == 0 && (!remainder.IsZero || !kept.IsEven)))
        {
            kept += 1;
        }
        var result = Math.ScaleB((double)kept, excess - shift);
        if (double.IsInfinity(result))
        {
            throw new OverflowException("integer division result too large for a float");
        }
        return negative ? -result : result;
    }
}
