using System;
using System.Numerics;

namespace Packvium.Units;

/// <summary>
/// An exact, always-reduced rational number with a positive denominator: the counterpart of
/// Python's <c>fractions.Fraction</c>, which the reference engine routes every parsed quantity
/// through before it becomes an integer tick count.
/// </summary>
public readonly record struct Rational
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }

    public Rational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException($"Fraction({numerator}, 0)");
        }
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public static Rational Of(BigInteger value) => new(value, BigInteger.One);

    /// <summary>As <c>str(Fraction)</c>: <c>127/80</c>, or the bare integer when the denominator is one.</summary>
    public override string ToString() => Denominator.IsOne
        ? Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public Rational Multiply(BigInteger factor) => new(Numerator * factor, Denominator);

    /// <summary>The integer this value rounds to, exactly as <c>units._rounded</c> decides it.</summary>
    public BigInteger Round(Rounding rounding)
    {
        var quotient = BigInteger.DivRem(BigInteger.Abs(Numerator), Denominator, out var remainder);
        var negative = Numerator.Sign < 0;
        if (remainder.IsZero)
        {
            return negative ? -quotient : quotient;
        }
        return rounding switch
        {
            Rounding.Floor => negative ? -(quotient + 1) : quotient,
            Rounding.Ceil => negative ? -quotient : quotient + 1,
            _ => RoundNearestEven(quotient, remainder, negative),
        };
    }

    private BigInteger RoundNearestEven(BigInteger quotient, BigInteger remainder, bool negative)
    {
        var doubled = remainder * 2;
        var magnitude = doubled.CompareTo(Denominator) switch
        {
            < 0 => quotient,
            > 0 => quotient + 1,
            _ => quotient.IsEven ? quotient : quotient + 1,
        };
        return negative ? -magnitude : magnitude;
    }
}
