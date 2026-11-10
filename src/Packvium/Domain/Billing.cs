using System;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>The carrier billing rules every weight-priced objective and search shares.</summary>
public static class Billing
{
    /// <summary>
    /// Ranks a packing the tariff cannot price behind every priceable one; a search device,
    /// refused before it can be returned. Shared with the other engines.
    /// </summary>
    public const long UnpriceableMinor = long.MaxValue;

    /// <summary>Billed weight in whole grams, rounded up, as a carrier reads a scale.</summary>
    public static long Grams(long weightTicks) => IntMath.CeilDiv(weightTicks, Weight.TicksPerG);

    /// <summary><c>max(gross, dimensional)</c> over the outer dimensions when declared, else the inner ones.</summary>
    public static long BilledTicks(Container container, long grossTicks, PackingConfig config)
    {
        var divisor = config.DimensionalWeightDivisor
                      ?? throw new ArgumentException("a weight-priced objective requires configuration.dimensional_weight_divisor");
        var dimensions = container.OuterDimensions ?? container.InnerDimensions;
        var dimensional = DimensionalWeight.Of(dimensions, divisor, config.DimensionalWeightLengthUnit,
            config.DimensionalWeightWeightUnit);
        return Math.Max(grossTicks, dimensional.Ticks);
    }
}
