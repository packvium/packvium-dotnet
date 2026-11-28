using System;
using System.Globalization;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// A JSON number as Python compares it: an exact integer (a boolean is one), or a float, and an
/// integer against a float is compared exactly rather than through a rounded conversion.
/// </summary>
internal readonly record struct Number(BigInteger? Exact, double Float) : IComparable<Number>
{
    public static Number Integer(BigInteger value) => new(value, 0);

    public static Number? Of(JsonValue value) => value switch
    {
        JsonInteger integer => Integer(integer.Value),
        JsonBool flag => Integer(flag.Value ? BigInteger.One : BigInteger.Zero),
        JsonFloat number => IsIntegral(number.Value) ? Integer(new BigInteger(number.Value)) : new Number(null, number.Value),
        _ => null,
    };

    public int CompareTo(Number other)
    {
        if (Exact is { } left && other.Exact is { } right)
        {
            return left.CompareTo(right);
        }
        if (Exact is null && other.Exact is null)
        {
            return Float.CompareTo(other.Float);
        }
        return Exact is { } integer ? CompareIntegerToFloat(integer, other.Float) : -CompareIntegerToFloat(other.Exact!.Value, Float);
    }

    /// <summary>Python's hash identity: an integral value by its integer, any other float by its bits.</summary>
    public string HashKey() =>
        Exact is { } integer
            ? "n" + integer.ToString(CultureInfo.InvariantCulture)
            : "f" + Float.ToString("R", CultureInfo.InvariantCulture);

    private static bool IsIntegral(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    /// <summary>A non-integral or infinite float against an integer: never equal.</summary>
    private static int CompareIntegerToFloat(BigInteger integer, double value)
    {
        if (double.IsNaN(value))
        {
            return 1;
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? -1 : 1;
        }
        return integer <= new BigInteger(Math.Floor(value)) ? -1 : 1;
    }
}
