using System.Collections.Generic;
using System.Numerics;

namespace Packvium.Domain;

public static class CentreOfMass
{
    /// <summary>
    /// How far the weighted centre of mass sits from the container's centre, in parts per
    /// million of the half-extent along the worse horizontal axis (Chebyshev, so it stays exact).
    /// One combined numerator per axis and a single final floor division. O(n).
    /// </summary>
    public static long OffsetPpm(Dimensions innerDimensions, IReadOnlyList<Placement> placements)
    {
        BigInteger totalWeight = 0;
        BigInteger doubledWeightedX = 0;
        BigInteger doubledWeightedY = 0;
        foreach (var placement in placements)
        {
            BigInteger weight = placement.Instance.Weight.Ticks;
            totalWeight += weight;
            doubledWeightedX += weight * (2 * (BigInteger)placement.Position.X + placement.Dimensions.Length.Ticks);
            doubledWeightedY += weight * (2 * (BigInteger)placement.Position.Y + placement.Dimensions.Width.Ticks);
        }
        if (totalWeight.IsZero)
        {
            return 0;
        }
        return OffsetPpm(totalWeight, doubledWeightedX, doubledWeightedY, innerDimensions.Length.Ticks,
            innerDimensions.Width.Ticks);
    }

    /// <summary>The shared final step, also used by the lattice closed form.</summary>
    internal static long OffsetPpm(BigInteger totalWeight, BigInteger doubledWeightedX, BigInteger doubledWeightedY,
        long length, long width)
    {
        var offsetX = BigInteger.Abs(doubledWeightedX - totalWeight * length) * 1_000_000 / (totalWeight * length);
        var offsetY = BigInteger.Abs(doubledWeightedY - totalWeight * width) * 1_000_000 / (totalWeight * width);
        return (long)BigInteger.Max(offsetX, offsetY);
    }
}
