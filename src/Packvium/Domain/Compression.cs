using System;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>Applied pressure strictly above an item's declared limit: an invalid placement, not a worse one.</summary>
public sealed class CrushViolationException(string message) : ArgumentException(message);

/// <summary>An exact pressure in kPa, held as a reduced non-negative rational.</summary>
public readonly record struct Pressure
{
    public Pressure(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.Sign <= 0)
        {
            throw new ArgumentException("pressure denominator must be positive");
        }
        if (numerator.Sign < 0)
        {
            throw new ArgumentException("pressure cannot be negative");
        }
        Numerator = numerator;
        Denominator = denominator;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static Pressure Zero => new(BigInteger.Zero, BigInteger.One);

    public static Pressure Reduced(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.Sign <= 0)
        {
            throw new ArgumentException("pressure denominator must be positive");
        }
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new Pressure(numerator / divisor, denominator / divisor);
    }

    /// <summary>Cross multiplication, so the comparison never leaves the integers.</summary>
    public bool ExceedsKpa(long limitKpa) => Numerator > limitKpa * Denominator;

    public override string ToString() => $"{Numerator}/{Denominator} kPa";
}

/// <summary>
/// Occupied height of a <c>compressible</c> item under the load resting on it, fixed by
/// <c>docs/IRREGULAR-ITEMS.md</c>. Every value is an exact integer or a reduced rational.
/// Arbitrary precision because the pressure numerator multiplies a weight by the square of a
/// metre in ticks, which leaves 128 bits for loads a real pallet can carry.
/// </summary>
public static class Compression
{
    /// <summary>Parts per million, the scale <c>compression_ratio</c> is carried at once parsed.</summary>
    public const long Ppm = 1_000_000;

    public const long StandardGravityNumerator = 980_665;
    public const long StandardGravityDenominator = 100_000;
    public const long PascalsPerKilopascal = 1_000;

    private const long TicksPerMetre = Length.TicksPerMm * 1_000;

    /// <summary>Pressure from the mass resting above an item over its uncompressed footprint.</summary>
    public static Pressure AppliedPressure(Weight topLoad, Int128 footprintAreaTicks2)
    {
        if (footprintAreaTicks2 <= 0)
        {
            throw new ArgumentException("footprint area must be positive");
        }
        var numerator = (BigInteger)topLoad.Ticks * StandardGravityNumerator * TicksPerMetre * TicksPerMetre;
        var denominator = (BigInteger)Weight.TicksPerKg * StandardGravityDenominator * PascalsPerKilopascal
            * (BigInteger)footprintAreaTicks2;
        return Pressure.Reduced(numerator, denominator);
    }

    /// <summary>
    /// Occupied height under load, rounded up and never below one tick: a discrete packer may
    /// never claim less space than the continuous model allows.
    /// </summary>
    public static long EffectiveHeightTicks(long heightTicks, long compressionRatioPpm, long maxPressureKpa,
        Pressure pressure)
    {
        if (heightTicks <= 0)
        {
            throw new ArgumentException("height must be positive");
        }
        if (compressionRatioPpm is < 0 or > Ppm)
        {
            throw new ArgumentException("compression ratio must be between zero and one million ppm");
        }
        if (maxPressureKpa < 0)
        {
            throw new ArgumentException("maximum pressure cannot be negative");
        }
        if (pressure.ExceedsKpa(maxPressureKpa))
        {
            throw new CrushViolationException(
                $"applied pressure {pressure} exceeds the declared limit of {maxPressureKpa} kPa");
        }
        if (maxPressureKpa == 0)
        {
            return heightTicks;
        }
        var divisor = maxPressureKpa * Ppm * pressure.Denominator;
        var retained = divisor - compressionRatioPpm * pressure.Numerator;
        var ceiling = (heightTicks * retained + divisor - 1) / divisor;
        return (long)BigInteger.Max(BigInteger.One, ceiling);
    }

    public static Int128 EffectiveVolumeTicks3(long lengthTicks, long widthTicks, long heightTicks,
        long compressionRatioPpm, long maxPressureKpa, Pressure pressure)
    {
        if (lengthTicks <= 0 || widthTicks <= 0)
        {
            throw new ArgumentException("footprint dimensions must be positive");
        }
        return checked((Int128)lengthTicks * widthTicks
                       * EffectiveHeightTicks(heightTicks, compressionRatioPpm, maxPressureKpa, pressure));
    }

    /// <summary>
    /// The public ratio rule, <c>floor(ratio * 1000000 + 0.5)</c>, applied once at the boundary,
    /// in binary floating point exactly as the reference engine evaluates it.
    /// </summary>
    public static long RatioToPpm(double ratio)
    {
        if (!(ratio >= 0.0 && ratio <= 1.0))
        {
            throw new ArgumentException("compression_ratio must be between zero and one");
        }
        return (long)(ratio * Ppm + 0.5);
    }
}
