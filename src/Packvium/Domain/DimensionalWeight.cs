using System;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Domain;

public static class DimensionalWeight
{
    /// <summary>
    /// Carrier-style volumetric weight, <c>(L x W x H in lengthUnit) / divisor</c>, floored. Exact
    /// integers throughout so the number can be reproduced by hand against a carrier's calculator.
    /// </summary>
    public static Weight Of(Dimensions dimensions, long divisor, string lengthUnit = "in", string weightUnit = "lb")
    {
        if (divisor <= 0)
        {
            throw new ArgumentException("divisor must be positive");
        }
        BigInteger lengthTicksPerUnit = Length.MultiplierOf(lengthUnit, strip: true);
        BigInteger weightTicksPerUnit = Weight.MultiplierOf(weightUnit, strip: true);
        var numerator = (BigInteger)dimensions.Volume * weightTicksPerUnit;
        var denominator = BigInteger.Pow(lengthTicksPerUnit, 3) * divisor;
        var ticks = numerator / denominator;
        return ticks > long.MaxValue
            ? throw new OverflowException("weight exceeds the 64-bit tick range")
            : new Weight((long)ticks);
    }
}
