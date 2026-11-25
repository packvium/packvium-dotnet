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
    public static long BilledTicks(Container container, long grossTicks, PackingConfig config) =>
        BilledTicks(container.OuterDimensions ?? container.InnerDimensions, grossTicks, config);

    /// <summary>What a carrier bills a finished container: a flat bag on its achieved package.</summary>
    public static long BilledTicks(PackedContainer packed, PackingConfig config) =>
        BilledTicks(packed.BilledDimensions, packed.GrossWeight.Ticks, config);

    /// <summary><c>max(gross, dimensional)</c> over <paramref name="dimensions"/>.</summary>
    public static long BilledTicks(Dimensions dimensions, long grossTicks, PackingConfig config) =>
        Math.Max(grossTicks, DimensionalTicks(dimensions, config));

    /// <summary>The dimensional weight of <paramref name="dimensions"/>, in weight ticks.</summary>
    public static long DimensionalTicks(Dimensions dimensions, PackingConfig config)
    {
        var divisor = config.DimensionalWeightDivisor
                      ?? throw new ArgumentException("a weight-priced objective requires configuration.dimensional_weight_divisor");
        var dimensional = DimensionalWeight.Of(dimensions, divisor, config.DimensionalWeightLengthUnit,
            config.DimensionalWeightWeightUnit);
        return dimensional.Ticks;
    }
}
