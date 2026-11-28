using System.Numerics;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Integer division with Python's semantics: floor division and modulo round toward negative
/// infinity, and a zero divisor is Python's <c>ZeroDivisionError</c>.
/// </summary>
internal static class Exact
{
    /// <summary><c>a // b</c>.</summary>
    public static BigInteger FloorDiv(BigInteger dividend, BigInteger divisor)
    {
        if (divisor.IsZero)
        {
            throw MalformedResultException.ShapeBreak("division by zero");
        }
        var quotient = BigInteger.DivRem(dividend, divisor, out var remainder);
        return !remainder.IsZero && (remainder.Sign < 0) != (divisor.Sign < 0) ? quotient - 1 : quotient;
    }

    /// <summary><c>a % b</c>, which takes the divisor's sign.</summary>
    public static BigInteger FloorMod(BigInteger dividend, BigInteger divisor) => dividend - FloorDiv(dividend, divisor) * divisor;

    /// <summary><c>-(-a // b)</c>.</summary>
    public static BigInteger CeilDiv(BigInteger dividend, BigInteger divisor) => -FloorDiv(-dividend, divisor);
}
