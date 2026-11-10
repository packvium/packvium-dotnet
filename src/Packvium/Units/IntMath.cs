using System;

namespace Packvium.Units;

/// <summary>
/// Python's integer division rounds toward negative infinity; C#'s truncates toward zero. The
/// two agree on non-negative operands only, and the reference engine also divides values that
/// can be negative (axle moments, ceilings written as <c>-(-a // b)</c>), so every such site
/// goes through these.
/// </summary>
public static class IntMath
{
    public static long FloorDiv(long numerator, long denominator)
    {
        var quotient = Math.DivRem(numerator, denominator, out var remainder);
        return remainder != 0 && (remainder < 0) != (denominator < 0) ? quotient - 1 : quotient;
    }

    public static long CeilDiv(long numerator, long denominator) => -FloorDiv(-numerator, denominator);

    public static Int128 FloorDiv(Int128 numerator, Int128 denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator - quotient * denominator;
        return remainder != 0 && (remainder < 0) != (denominator < 0) ? quotient - 1 : quotient;
    }

    public static Int128 CeilDiv(Int128 numerator, Int128 denominator) => -FloorDiv(-numerator, denominator);

    /// <summary>A 128-bit value known to fit in 64 bits, refused rather than truncated if not.</summary>
    public static long ToLong(Int128 value) => checked((long)value);
}
