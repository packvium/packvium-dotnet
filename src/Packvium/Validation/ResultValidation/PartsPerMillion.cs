using System.Numerics;

namespace Packvium.Validation.ResultChecks;

/// <summary>Ratios carried as parts per million: the objective's ratios, support, void-fill reserve and compression.</summary>
internal static class PartsPerMillion
{
    public const int Scale = 1_000_000;

    /// <summary>
    /// A public ratio in parts per million exactly as every solver converts it: read as an IEEE
    /// double, then <c>floor(ratio * 10^6 + 0.5)</c> in double arithmetic. That differs from
    /// rounding the exact decimal on about one in a hundred seven-decimal halves (<c>0.0001245</c>
    /// is 124 ppm, not 125), so judging by the exact rational would refuse placements, and reported
    /// reserves, every solver produces. Each step is a correctly rounded IEEE operation, so the
    /// value is the same in every language.
    /// </summary>
    public static BigInteger Of(Ratio ratio)
    {
        var scaled = ratio.ToDouble() * Scale + 0.5;
        if (!double.IsFinite(scaled))
        {
            throw MalformedResultException.ShapeBreak("cannot convert an infinite ratio to an integer");
        }
        return new BigInteger(scaled);
    }

    /// <summary>Exact <c>floor(base_area * ppm / 10^6)</c>, the least supported area the ratio allows.</summary>
    public static BigInteger RequiredSupportArea(BigInteger baseArea, BigInteger ppm) => Exact.FloorDiv(baseArea * ppm, Scale);

    /// <summary><c>floor(volume * ppm / 10^6)</c>, split so the product stays as small as the reference keeps it.</summary>
    public static BigInteger Reserve(BigInteger volume, BigInteger ppm) =>
        Exact.FloorDiv(volume, Scale) * ppm + Exact.FloorDiv(Exact.FloorMod(volume, Scale) * ppm, Scale);
}
