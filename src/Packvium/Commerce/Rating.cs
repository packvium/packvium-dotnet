using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Commerce;

/// <summary>The requested zone or accessorials are not defined by the resolved tariff; exactly one is set.</summary>
public sealed class UnavailableServiceException(string message, string? zone = null,
    ImmutableArray<string> accessorialIds = default) : Exception(message)
{
    public string? Zone { get; } = zone;

    public ImmutableArray<string> AccessorialIds { get; } = accessorialIds.IsDefault ? [] : accessorialIds;
}

/// <summary>No tariff under that carrier and service, or no such version, or none effective yet.</summary>
public sealed class TariffNotFoundException(string message) : Exception(message);

/// <summary>A named accessorial: a flat charge or a permille of the base charge, never both.</summary>
public sealed record AccessorialCharge
{
    public AccessorialCharge(string accessorialId, BigInteger? flatChargeMinor, BigInteger? permilleOfBase)
    {
        if (accessorialId.Length == 0)
        {
            throw new ArgumentException("accessorial_id is required");
        }
        if (flatChargeMinor.HasValue == permilleOfBase.HasValue)
        {
            throw new ArgumentException("an accessorial must set exactly one of flat_charge_minor or permille_of_base");
        }
        if (flatChargeMinor < 0)
        {
            throw new ArgumentException("flat_charge_minor cannot be negative");
        }
        if (permilleOfBase < 0)
        {
            throw new ArgumentException("permille_of_base cannot be negative");
        }
        AccessorialId = accessorialId;
        FlatChargeMinor = flatChargeMinor;
        PermilleOfBase = permilleOfBase;
    }

    public string AccessorialId { get; }

    public BigInteger? FlatChargeMinor { get; }

    public BigInteger? PermilleOfBase { get; }

    public BigInteger ChargeMinor(BigInteger baseChargeMinor) =>
        FlatChargeMinor ?? Rating.CeilDiv(baseChargeMinor * PermilleOfBase!.Value, 1000);
}

/// <summary>One immutable, numbered, effective-dated version of a carrier service's rate card.</summary>
public sealed record Tariff
{
    public Tariff(string carrierId, string serviceId, int version, BigInteger effectiveAt,
        BigInteger dimensionalWeightDivisor, ImmutableArray<KeyValuePair<string, BigInteger>> costPerDimensionalKgMinor,
        BigInteger minimumChargeMinor, BigInteger fuelSurchargePermille,
        ImmutableArray<KeyValuePair<string, AccessorialCharge>> accessorials)
    {
        if (carrierId.Length == 0)
        {
            throw new ArgumentException("carrier_id is required");
        }
        if (serviceId.Length == 0)
        {
            throw new ArgumentException("service_id is required");
        }
        if (version <= 0)
        {
            throw new ArgumentException("version must be positive");
        }
        if (effectiveAt < 0)
        {
            throw new ArgumentException("effective_at cannot be negative");
        }
        if (dimensionalWeightDivisor <= 0)
        {
            throw new ArgumentException("dimensional_weight_divisor must be positive");
        }
        if (minimumChargeMinor < 0)
        {
            throw new ArgumentException("minimum_charge_minor cannot be negative");
        }
        if (fuelSurchargePermille < 0)
        {
            throw new ArgumentException("fuel_surcharge_permille cannot be negative");
        }
        if (costPerDimensionalKgMinor.Any(entry => entry.Value < 0))
        {
            throw new ArgumentException("cost_per_dimensional_kg_minor entries cannot be negative");
        }
        CarrierId = carrierId;
        ServiceId = serviceId;
        Version = version;
        EffectiveAt = effectiveAt;
        DimensionalWeightDivisor = dimensionalWeightDivisor;
        CostPerDimensionalKgMinor = costPerDimensionalKgMinor.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        MinimumChargeMinor = minimumChargeMinor;
        FuelSurchargePermille = fuelSurchargePermille;
        Accessorials = accessorials.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    public string CarrierId { get; }

    public string ServiceId { get; }

    public int Version { get; }

    public BigInteger EffectiveAt { get; }

    public BigInteger DimensionalWeightDivisor { get; }

    /// <summary>Zone to minor cost per kilogram.</summary>
    public IReadOnlyDictionary<string, BigInteger> CostPerDimensionalKgMinor { get; }

    public BigInteger MinimumChargeMinor { get; }

    public BigInteger FuelSurchargePermille { get; }

    public IReadOnlyDictionary<string, AccessorialCharge> Accessorials { get; }
}

/// <summary>Every component that contributed to the total, and the exact tariff version that produced it.</summary>
public sealed record RateBreakdown(
    string CarrierId,
    string ServiceId,
    int TariffVersion,
    string Zone,
    BigInteger ActualWeightG,
    BigInteger DimensionalWeightG,
    BigInteger BilledWeightG,
    BigInteger BaseChargeMinor,
    bool MinimumChargeApplied,
    BigInteger FuelSurchargeMinor,
    ImmutableArray<(string AccessorialId, BigInteger Amount)> AccessorialChargesMinor,
    BigInteger TotalMinor);

/// <summary>A shipment's weight, volume, destination zone and the accessorials it needs.</summary>
public sealed record RatingRequest
{
    public RatingRequest(string zone, BigInteger actualWeightG, BigInteger volumeMm3,
        ImmutableArray<string> requestedAccessorials)
    {
        if (zone.Length == 0)
        {
            throw new ArgumentException("zone is required");
        }
        if (actualWeightG < 0)
        {
            throw new ArgumentException("actual_weight_g cannot be negative");
        }
        if (volumeMm3 < 0)
        {
            throw new ArgumentException("volume_mm3 cannot be negative");
        }
        if (requestedAccessorials.Any(accessorial => accessorial.Length == 0))
        {
            throw new ArgumentException("requested accessorial ids must be non-empty");
        }
        if (requestedAccessorials.Distinct(StringComparer.Ordinal).Count() != requestedAccessorials.Length)
        {
            throw new ArgumentException("requested accessorial ids must be unique");
        }
        Zone = zone;
        ActualWeightG = actualWeightG;
        VolumeMm3 = volumeMm3;
        RequestedAccessorials = requestedAccessorials;
    }

    public string Zone { get; }

    public BigInteger ActualWeightG { get; }

    public BigInteger VolumeMm3 { get; }

    public ImmutableArray<string> RequestedAccessorials { get; }
}

/// <summary>
/// Exact landed-cost arithmetic: every inexact division rounds up, never down and never
/// through a float. O(a) for a requested accessorials.
/// </summary>
public static class Rating
{
    public static BigInteger CeilDiv(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentException("denominator must be positive");
        }
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder > 0 ? quotient + 1 : quotient;
    }

    public static RateBreakdown RateTariff(Tariff tariff, RatingRequest request)
    {
        var name = $"tariff {tariff.CarrierId}/{tariff.ServiceId} v{tariff.Version}";
        if (!tariff.CostPerDimensionalKgMinor.TryGetValue(request.Zone, out var ratePerKg))
        {
            throw new UnavailableServiceException($"{name} has no rate for zone {PythonText.Repr(request.Zone)}",
                zone: request.Zone);
        }
        var unknown = request.RequestedAccessorials.Where(id => !tariff.Accessorials.ContainsKey(id))
            .Distinct(StringComparer.Ordinal).Order(CodePointComparer.Instance).ToImmutableArray();
        if (!unknown.IsEmpty)
        {
            throw new UnavailableServiceException($"{name} does not offer accessorial(s) [{string.Join(", ", unknown.Select(PythonText.Repr))}]",
                accessorialIds: unknown);
        }
        var dimensionalWeight = CeilDiv(request.VolumeMm3, tariff.DimensionalWeightDivisor);
        var billedWeight = BigInteger.Max(request.ActualWeightG, dimensionalWeight);
        var rawBase = CeilDiv(billedWeight * ratePerKg, 1000);
        var minimumApplied = rawBase < tariff.MinimumChargeMinor;
        var baseCharge = minimumApplied ? tariff.MinimumChargeMinor : rawBase;
        var fuel = CeilDiv(baseCharge * tariff.FuelSurchargePermille, 1000);
        var accessorials = request.RequestedAccessorials
            .Select(id => (id, tariff.Accessorials[id].ChargeMinor(baseCharge))).ToImmutableArray();
        var total = accessorials.Aggregate(baseCharge + fuel, (sum, charge) => sum + charge.Item2);
        return new RateBreakdown(tariff.CarrierId, tariff.ServiceId, tariff.Version, request.Zone, request.ActualWeightG,
            dimensionalWeight, billedWeight, baseCharge, minimumApplied, fuel, accessorials, total);
    }
}

/// <summary>Per carrier-service append-only tariff history; a version's number is its 1-based position.</summary>
public sealed class CarrierRegistry
{
    private readonly Dictionary<(string, string), List<Tariff>> versions = [];

    public Tariff Publish(string carrierId, string serviceId, BigInteger effectiveAt, BigInteger dimensionalWeightDivisor,
        ImmutableArray<KeyValuePair<string, BigInteger>> costPerDimensionalKgMinor, BigInteger minimumChargeMinor,
        BigInteger fuelSurchargePermille, ImmutableArray<KeyValuePair<string, AccessorialCharge>> accessorials)
    {
        if (!versions.TryGetValue((carrierId, serviceId), out var history))
        {
            versions[(carrierId, serviceId)] = history = [];
        }
        var tariff = new Tariff(carrierId, serviceId, history.Count + 1, effectiveAt, dimensionalWeightDivisor,
            costPerDimensionalKgMinor, minimumChargeMinor, fuelSurchargePermille, accessorials);
        history.Add(tariff);
        return tariff;
    }

    public IReadOnlyList<Tariff> Versions(string carrierId, string serviceId) =>
        versions.TryGetValue((carrierId, serviceId), out var history)
            ? history
            : throw new TariffNotFoundException($"no tariff registered for {carrierId}/{serviceId}");

    public Tariff Tariff(string carrierId, string serviceId, BigInteger version) =>
        Versions(carrierId, serviceId).FirstOrDefault(tariff => tariff.Version == version)
        ?? throw new TariffNotFoundException($"{carrierId}/{serviceId} has no version {version}");

    /// <summary>The highest effective_at not after the instant, ties to the later-published version.</summary>
    public Tariff EffectiveTariff(string carrierId, string serviceId, BigInteger asOf) =>
        Versions(carrierId, serviceId).Where(tariff => tariff.EffectiveAt <= asOf)
            .OrderByDescending(tariff => tariff.EffectiveAt).ThenByDescending(tariff => tariff.Version)
            .FirstOrDefault()
        ?? throw new TariffNotFoundException($"{carrierId}/{serviceId} has no tariff version effective as of {asOf}");
}
