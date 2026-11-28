using System;
using System.Numerics;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Python's <c>Fraction</c>: always reduced, the denominator always positive, so
/// <see cref="Numerator"/> and <see cref="Denominator"/> read the same numbers the reference reads.
/// </summary>
internal sealed record Ratio
{
    private Ratio(BigInteger numerator, BigInteger denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static Ratio Zero { get; } = new(BigInteger.Zero, BigInteger.One);

    public static Ratio Of(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw MalformedResultException.ShapeBreak("Fraction with a zero denominator");
        }
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        if (divisor.IsZero)
        {
            divisor = BigInteger.One;
        }
        if (denominator.Sign < 0)
        {
            divisor = -divisor;
        }
        return new Ratio(numerator / divisor, denominator / divisor);
    }

    public static Ratio Integer(BigInteger value) => new(value, BigInteger.One);

    public Ratio Add(Ratio other) => Of(Numerator * other.Denominator + other.Numerator * Denominator, Denominator * other.Denominator);

    public Ratio Multiply(BigInteger factor) => Of(Numerator * factor, Denominator);

    public Ratio Negate() => new(-Numerator, Denominator);

    public int CompareTo(Ratio other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    public int CompareTo(BigInteger value) => Numerator.CompareTo(value * Denominator);

    /// <summary><c>numerator // denominator</c>.</summary>
    public BigInteger Floor() => Exact.FloorDiv(Numerator, Denominator);

    /// <summary>
    /// Python's <c>float(fraction)</c>: the nearest double, ties to even. The quotient is taken to
    /// at least 55 significant bits exactly and the remainder decides the last kept one. O(size²).
    /// </summary>
    public double ToDouble()
    {
        if (Numerator.IsZero)
        {
            return 0.0;
        }
        var magnitude = BigInteger.Abs(Numerator);
        var shift = 55 - (int)(magnitude.GetBitLength() - Denominator.GetBitLength());
        var quotient = shift >= 0
            ? BigInteger.DivRem(magnitude << shift, Denominator, out var remainder)
            : BigInteger.DivRem(magnitude, Denominator << -shift, out remainder);
        var extra = (int)quotient.GetBitLength() - 53;
        var mantissa = quotient >> extra;
        var rest = quotient - (mantissa << extra);
        var half = BigInteger.One << (extra - 1);
        if (rest > half || (rest == half && (!remainder.IsZero || !mantissa.IsEven)))
        {
            mantissa += 1;
        }
        var value = Math.ScaleB((double)mantissa, extra - shift);
        return Numerator.Sign < 0 ? -value : value;
    }
}
