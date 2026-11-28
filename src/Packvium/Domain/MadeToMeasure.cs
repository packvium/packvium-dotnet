using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>Three per-axis tick counts: length, width and height.</summary>
public readonly record struct Triple(long Length, long Width, long Height)
{
    public long this[int axis] => axis switch
    {
        0 => Length,
        1 => Width,
        _ => Height,
    };

    public static Triple Of(Dimensions dimensions) =>
        new(dimensions.Length.Ticks, dimensions.Width.Ticks, dimensions.Height.Ticks);

    public Dimensions AsDimensions() => Dimensions.OfTicks(Length, Width, Height);

    public BigInteger Volume => (BigInteger)Length * Width * Height;
}

/// <summary>
/// What an on-demand box machine can cut beyond the declared maximum, per axis in ticks
/// <see cref="Minimum"/> equal to the maximum pins
/// an axis; <see cref="TarePerSquareMetre"/> is weight ticks per square metre of inner surface, and
/// <see cref="OuterAllowance"/> the board added to each inner side. Every derivation is exact
/// integer arithmetic on <see cref="BigInteger"/>, so the rungs, the box and its price are the
/// reference's whatever the request's magnitudes.
/// </summary>
public sealed record MadeToMeasure
{
    /// <summary>One length tick is 1/16 000 000 m.</summary>
    public const long LengthTicksPerMetre = 16_000_000;

    /// <summary>The largest integer every binding returns unchanged; no fabricated price may pass it.</summary>
    public const long MaxExactInteger = (1L << 53) - 1;

    private static readonly BigInteger SquareMetreTicks2 = (BigInteger)LengthTicksPerMetre * LengthTicksPerMetre;

    /// <summary>Each compact rung's side as a fraction of the integer cube root of the round's volume.</summary>
    private static readonly (int Numerator, int Denominator)[] RungFactors = [(5, 4), (3, 2), (2, 1)];

    public MadeToMeasure(Triple minimum, Triple increment, long costPerSquareMetreMinor = 0, long tarePerSquareMetre = 0,
        Triple outerAllowance = default)
    {
        if (Math.Min(minimum.Length, Math.Min(minimum.Width, minimum.Height)) <= 0)
        {
            throw new ObjectRefusal("invalid_value", "made_to_measure minimum must be positive");
        }
        if (Math.Min(increment.Length, Math.Min(increment.Width, increment.Height)) <= 0)
        {
            throw new ObjectRefusal("invalid_value", "made_to_measure increment must be positive");
        }
        if (costPerSquareMetreMinor < 0 || tarePerSquareMetre < 0)
        {
            throw new ObjectRefusal("below_minimum", "made_to_measure prices cannot be negative");
        }
        if (Math.Min(outerAllowance.Length, Math.Min(outerAllowance.Width, outerAllowance.Height)) < 0)
        {
            throw new ObjectRefusal("negative_measure", "made_to_measure outer_allowance cannot be negative");
        }
        Minimum = minimum;
        Increment = increment;
        CostPerSquareMetreMinor = costPerSquareMetreMinor;
        TarePerSquareMetre = tarePerSquareMetre;
        OuterAllowance = outerAllowance;
    }

    public Triple Minimum { get; }

    public Triple Increment { get; }

    public long CostPerSquareMetreMinor { get; }

    public long TarePerSquareMetre { get; }

    public Triple OuterAllowance { get; }

    /// <summary>
    /// Refuses a machine whose grid does not land on its own maximum, so every reachable size is
    /// unambiguous and rounding an extent inside the maximum up onto the grid never passes it. O(1).
    /// </summary>
    public void Admits(Triple maximum)
    {
        string[] names = ["length", "width", "height"];
        for (var axis = 0; axis < 3; axis++)
        {
            if (Minimum[axis] > maximum[axis])
            {
                throw new ObjectRefusal("invalid_value", $"made_to_measure minimum {names[axis]} exceeds inner_dimensions");
            }
            if ((maximum[axis] - Minimum[axis]) % Increment[axis] != 0)
            {
                throw new ObjectRefusal("invalid_value",
                    $"made_to_measure increment {names[axis]} does not divide inner_dimensions minus minimum");
            }
        }
    }

    /// <summary>
    /// Refuses a machine whose largest box would not fit this port's 64-bit lengths, weights and
    /// minor units (1.4 deviation 1). Outer size, price and tare all grow with every side, so the
    /// largest box bounds every box the machine can cut, and no fabrication can overflow later. O(1).
    /// </summary>
    public void RequireSixtyFourBitBox(Triple maximum, long feeMinor, long declaredTare)
    {
        var outer = (BigInteger)Math.Max(maximum.Length, Math.Max(maximum.Width, maximum.Height))
                    + Math.Max(OuterAllowance.Length, Math.Max(OuterAllowance.Width, OuterAllowance.Height));
        if (outer > long.MaxValue || BoxCostMinor(feeMinor, maximum) > long.MaxValue
            || BoxTareTicks(declaredTare, maximum) > long.MaxValue)
        {
            throw new ObjectRefusal("invalid_value", "made_to_measure prices, weighs or sizes the largest box past 64 bits");
        }
    }

    /// <summary>The smallest cuttable size on <paramref name="axis"/> that is at least <paramref name="value"/>.</summary>
    public long Grid(int axis, long value)
    {
        var (minimum, step) = (Minimum[axis], Increment[axis]);
        var steps = IntMath.CeilDiv((Int128)Math.Max(value, minimum) - minimum, step);
        return checked((long)(minimum + steps * step));
    }

    /// <summary><see cref="Grid"/>, capped at the machine maximum: sizes a solving envelope, never a box.</summary>
    public long Clamp(int axis, long value, long maximum) => Math.Min(maximum, Grid(axis, value));

    /// <summary>The inner surface of a closed box, <c>2(LW + LH + WH)</c>, in square ticks.</summary>
    public static BigInteger SurfaceArea(Triple sides)
    {
        BigInteger length = sides.Length, width = sides.Width, height = sides.Height;
        return 2 * (length * width + length * height + width * height);
    }

    /// <summary><c>ceil(rate * area / T^2)</c>, as <c>p*q + ceil(p*r / T^2)</c> with <c>q, r = divmod(area, T^2)</c>.</summary>
    public static BigInteger PerSquareMetre(long rate, BigInteger area)
    {
        var whole = BigInteger.DivRem(area, SquareMetreTicks2, out var fraction);
        return rate * whole + CeilDiv(rate * fraction, SquareMetreTicks2);
    }

    /// <summary>A box's price: the container's own fee plus its material, rounded up.</summary>
    public static BigInteger BoxCostMinorFor(long priceMinor, long feeMinor, Triple sides) =>
        feeMinor + PerSquareMetre(priceMinor, SurfaceArea(sides));

    public BigInteger BoxCostMinor(long feeMinor, Triple sides) => BoxCostMinorFor(CostPerSquareMetreMinor, feeMinor, sides);

    /// <summary>The fabricated box's weight: the declared tare plus its board, rounded up.</summary>
    public BigInteger BoxTareTicks(long declaredTare, Triple sides) =>
        declaredTare + PerSquareMetre(TarePerSquareMetre, SurfaceArea(sides));

    /// <summary>The smallest non-negative c with c^3 at least <paramref name="value"/>: doubling, then bisection. O(log value).</summary>
    public static BigInteger CubeRootCeiling(BigInteger value)
    {
        BigInteger high = 1;
        while (high * high * high < value)
        {
            high *= 2;
        }
        BigInteger low = 0;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (middle * middle * middle >= value)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    /// <summary>
    /// The rungs one trial tries, ascending on every axis, the machine maximum last.
    /// <paramref name="edges"/> are each remaining instance's envelope edges. O(r) over the r
    /// instances plus the cube root; the list depends only on their multiset.
    /// </summary>
    public ImmutableArray<Triple> SolvingEnvelopes(Triple maximum, IEnumerable<Triple> edges)
    {
        BigInteger volume = 0;
        var floors = new long[3];
        foreach (var instance in edges)
        {
            volume += instance.Volume;
            long[] ranked = [.. new[] { instance.Length, instance.Width, instance.Height }.OrderDescending()];
            for (var rank = 0; rank < 3; rank++)
            {
                floors[rank] = Math.Max(floors[rank], ranked[rank]);
            }
        }
        var side = CubeRootCeiling(volume);
        var rungs = new List<Triple>();
        foreach (var (numerator, denominator) in RungFactors)
        {
            var scaled = CeilDiv(side * numerator, denominator);
            long Axis(int axis) => Clamp(axis, (long)BigInteger.Min(BigInteger.Max(scaled, floors[axis]), maximum[axis]),
                maximum[axis]);
            var rung = new Triple(Axis(0), Axis(1), Axis(2));
            if (rung.Volume >= volume && !rungs.Contains(rung))
            {
                rungs.Add(rung);
            }
        }
        if (!rungs.Contains(maximum))
        {
            rungs.Add(maximum);
        }
        return [.. rungs];
    }

    /// <summary>Each instance's envelope edges, nominal plus twice the clearance.</summary>
    public static List<Triple> InstanceEdges(IEnumerable<Dimensions> dimensions, long clearanceTicks)
    {
        var grow = checked(2 * clearanceTicks);
        return [.. dimensions.Select(size => new Triple(checked(size.Length.Ticks + grow), checked(size.Width.Ticks + grow),
            checked(size.Height.Ticks + grow)))];
    }

    /// <summary>ceil(n / d) for d &gt; 0: integer division truncates toward zero, so a positive remainder rounds up.</summary>
    private static BigInteger CeilDiv(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder.Sign > 0 ? quotient + 1 : quotient;
    }
}
