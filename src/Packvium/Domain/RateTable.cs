using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

/// <summary>A billed weight the rate table does not price: structured, never a silent top-bracket clamp.</summary>
public sealed class UnratedWeightException(string message) : ArgumentException(message);

/// <summary>
/// A carrier rate card carried by the request. The price charged is the one at the first bracket
/// at or above the billed weight. Everything is an exact integer -- grams, minor currency units,
/// parts per thousand -- because a landed cost in the objective must reproduce bit for bit.
/// A price may dip between brackets: a promotional band is a real rate card.
/// </summary>
public sealed class RateTable
{
    public RateTable(IEnumerable<long> weightBracketsG, IEnumerable<long> pricesMinor, long minimumChargeMinor = 0,
        long fuelSurchargePermille = 0)
    {
        WeightBracketsG = weightBracketsG.ToImmutableArray();
        PricesMinor = pricesMinor.ToImmutableArray();
        MinimumChargeMinor = minimumChargeMinor;
        FuelSurchargePermille = fuelSurchargePermille;
        Validate();
    }

    public ImmutableArray<long> WeightBracketsG { get; }

    public ImmutableArray<long> PricesMinor { get; }

    public long MinimumChargeMinor { get; }

    public long FuelSurchargePermille { get; }

    /// <summary>
    /// The exact landed cost of one shipment at this billed weight, or null when the tariff does
    /// not price it -- so a search can compare an unpriceable candidate rather than abort on one.
    /// </summary>
    public long? ChargeMinorOrNull(long billedWeightG)
    {
        for (var index = 0; index < WeightBracketsG.Length; index++)
        {
            if (billedWeightG <= WeightBracketsG[index])
            {
                var basePrice = Math.Max(PricesMinor[index], MinimumChargeMinor);
                // Ceiling, never a float: rounding down would let a fractional unit of revenue vanish.
                var surcharge = checked(basePrice * FuelSurchargePermille + 999) / 1000;
                return checked(basePrice + surcharge);
            }
        }
        return null;
    }

    public long ChargeMinor(long billedWeightG) =>
        ChargeMinorOrNull(billedWeightG) ?? throw new UnratedWeightException(
            $"billed weight {billedWeightG} g is above the rate table's last bracket "
            + $"({WeightBracketsG[^1]} g); the shipment has no published price");

    private void Validate()
    {
        if (WeightBracketsG.IsEmpty)
        {
            throw new ArgumentException("rate_table requires at least one weight bracket");
        }
        if (WeightBracketsG.Length != PricesMinor.Length)
        {
            throw new ArgumentException("rate_table weight_brackets_g and prices_minor must be the same length");
        }
        if (WeightBracketsG.Any(bound => bound <= 0))
        {
            throw new ArgumentException("rate_table weight brackets must be positive");
        }
        if (WeightBracketsG.Zip(WeightBracketsG.Skip(1)).Any(pair => pair.Second <= pair.First))
        {
            throw new ArgumentException("rate_table weight brackets must be strictly ascending");
        }
        if (PricesMinor.Any(price => price < 0))
        {
            throw new ArgumentException("rate_table prices cannot be negative");
        }
        if (MinimumChargeMinor < 0)
        {
            throw new ArgumentException("rate_table minimum_charge_minor cannot be negative");
        }
        if (FuelSurchargePermille < 0)
        {
            throw new ArgumentException("rate_table fuel_surcharge_permille cannot be negative");
        }
    }
}
