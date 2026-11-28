using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// The objective vector recomputed from the result alone, so the formula is checked against every
/// implementation independently of which packing it chose: two engines may return different
/// packings, but neither may misreport the score of its own.
/// </summary>
internal static class ObjectiveCheck
{
    /// <summary>O(containers · placements + unpacked + item types).</summary>
    public static void Check(JsonValue request, JsonValue result, RequestContext context, Ordered<ContainerSpec> fabricated, List<ResultIssue> issues)
    {
        var configuration = Json.Get(request, "configuration", Json.EmptyObject);
        var selected = Json.Get(configuration, "objective", new JsonString("default"));
        var reportedName = Json.Get(result, "objective");
        if ((Json.Contains(configuration, "objective") || reportedName is not JsonNull) && !Json.PyEquals(reportedName, selected))
        {
            issues.Add(Issues.Of("objective_mismatch", $"reported {PyFormat.Repr(reportedName)}, requested {PyFormat.Repr(selected)}"));
        }
        var reported = Json.Items(Json.At(result, "score"), "score").Select(component => Json.PyInt(component, "score")).ToImmutableArray();
        var billing = new Billing(
            Json.Get(configuration, "dimensional_weight_divisor"),
            Json.Get(configuration, "dimensional_weight_length_unit", new JsonString("in")),
            Json.Get(configuration, "dimensional_weight_weight_unit", new JsonString("lb")));
        var recomputed = Objective(result, context, fabricated, selected, billing);
        if (!reported.SequenceEqual(recomputed))
        {
            issues.Add(Issues.Of("score_mismatch", $"reported {PyFormat.IntList(reported)}, recomputed {PyFormat.IntList(recomputed)}"));
        }
    }

    private static ImmutableArray<BigInteger> Objective(JsonValue result, RequestContext context, Ordered<ContainerSpec> fabricated, JsonValue name,
        Billing billing)
    {
        var totals = new Totals();
        var billed = Json.IsText(name, "shipping_cost") || Json.IsText(name, "lowest_landed_cost");
        var containers = Json.Items(Json.At(result, "containers"), "result.containers");
        foreach (var packed in containers)
        {
            // A made-to-measure container is scored as the box recomputed for it.
            var spec = fabricated.GetOrDefault(Json.Get(packed, "id")) ?? context.Containers.GetOrDefault(Json.At(packed, "container_type"));
            if (spec is not null)
            {
                Accumulate(totals, packed, spec, context.Clearance, billed ? billing : null, Json.IsText(name, "lowest_landed_cost"));
            }
        }
        var unpackedItems = Json.Items(Json.At(result, "unpacked_items"), "result.unpacked_items");
        BigInteger unpacked = unpackedItems.Length;
        BigInteger opened = containers.Length;
        switch ((name as JsonString)?.Value)
        {
            case "default":
                return [unpacked, opened, totals.Cost, totals.Unused, totals.Height];
            case "lowest_cost":
                return [unpacked, totals.Cost, opened, totals.Unused, totals.Height];
            case "shipping_cost":
                RequireDivisor(billing, "shipping_cost");
                return [unpacked, totals.Billable, opened, totals.Unused, totals.Height];
            case "lowest_landed_cost":
                RequireDivisor(billing, "lowest_landed_cost");
                return [unpacked, totals.Landed, opened, totals.Unused, totals.Height];
            case "open_dimension_height":
                return [unpacked, totals.AchievedHeight, opened, totals.Cost, totals.Unused];
            case "maximum_value":
                return [unpacked, ValueForgone(unpackedItems, context), opened, totals.Cost, totals.Unused];
            case "maximum_volume":
                return [UnpackedVolumePpm(unpackedItems, context), unpacked, opened, totals.Cost, totals.Unused];
            default:
                throw MalformedResultException.ContractBreak($"unknown requested objective {PyFormat.Repr(name)}");
        }
    }

    private static void Accumulate(Totals totals, JsonValue packed, ContainerSpec spec, BigInteger clearance, Billing? billing, bool landed)
    {
        totals.Cost += Json.Int(spec.CostMinor, "cost_minor");
        var volume = spec.Volume;
        if (volume.IsZero)
        {
            return;
        }
        var used = Json.PyInt(Json.At(packed, "used_volume_ticks3"), "used_volume_ticks3");
        totals.Unused += Exact.FloorDiv((volume - used) * PartsPerMillion.Scale, volume);
        // The height key is measured on envelope tops, so the clearance margin counts.
        var top = Json.Items(Json.At(packed, "placements"), "placements")
            .Select(placement => Extents.Tick(Json.At(placement, "position"), "z") + Extents.Tick(Json.At(placement, "dimensions"), "height") + clearance)
            .DefaultIfEmpty(BigInteger.Zero)
            .Max();
        totals.Height += Exact.FloorDiv(top * PartsPerMillion.Scale, spec.Inner[2]);
        totals.AchievedHeight += top;
        if (billing is null || billing.Divisor is JsonNull)
        {
            return;
        }
        var gross = Json.PyInt(Json.Get(Json.Get(packed, "gross_weight", Json.EmptyObject), "ticks", new JsonInteger(0)), "gross_weight");
        // A flat bag is billed on the package it conformed to.
        var size = spec.FlatBag ? Extents.AchievedExtent(packed, clearance) : spec.Outer ?? spec.Inner;
        var charged = BigInteger.Max(gross, billing.DimensionalWeight(size));
        totals.Billable += charged;
        if (landed)
        {
            totals.Landed += RateCard.Charge(spec, charged);
        }
    }

    private static BigInteger ValueForgone(ImmutableArray<JsonValue> unpackedItems, RequestContext context)
    {
        var forgone = BigInteger.Zero;
        foreach (var item in unpackedItems)
        {
            if (context.Items.GetOrDefault(Json.At(item, "item_type")) is { } spec && Json.Truthy(spec.Value))
            {
                forgone += Json.Int(spec.Value, "value");
            }
        }
        return forgone;
    }

    /// <summary>The share of the requested item volume left unpacked, rounded up so an incomplete answer never scores zero.</summary>
    private static BigInteger UnpackedVolumePpm(ImmutableArray<JsonValue> unpackedItems, RequestContext context)
    {
        var unpackedVolume = BigInteger.Zero;
        foreach (var item in unpackedItems)
        {
            if (context.Items.GetOrDefault(Json.At(item, "item_type")) is { } spec)
            {
                unpackedVolume += spec.NominalVolume;
            }
        }
        var requested = context.Items.Values.Aggregate(BigInteger.Zero, (sum, spec) => sum + spec.NominalVolume * Json.Int(spec.Quantity, "quantity"));
        return requested.IsZero ? BigInteger.Zero : Exact.CeilDiv(unpackedVolume * PartsPerMillion.Scale, requested);
    }

    private static void RequireDivisor(Billing billing, string objective)
    {
        if (billing.Divisor is JsonNull)
        {
            throw MalformedResultException.ContractBreak($"{objective} objective requires configuration.dimensional_weight_divisor");
        }
    }

    /// <summary>The running sums the objective vector is built from.</summary>
    private sealed class Totals
    {
        public BigInteger Cost { get; set; }

        public BigInteger Unused { get; set; }

        public BigInteger Height { get; set; }

        public BigInteger Billable { get; set; }

        public BigInteger AchievedHeight { get; set; }

        public BigInteger Landed { get; set; }
    }

    /// <summary>The request's dimensional-weight settings, as JSON values until a billed container reads them.</summary>
    private sealed record Billing(JsonValue Divisor, JsonValue LengthUnit, JsonValue WeightUnit)
    {
        /// <summary><c>floor(volume · weight ticks per unit / (length ticks per unit³ · divisor))</c>, exact.</summary>
        public BigInteger DimensionalWeight(ImmutableArray<BigInteger> size)
        {
            var length = TickUnits.Length.PerUnit(Json.Text(LengthUnit, "dimensional_weight_length_unit"));
            var weight = TickUnits.Weight.PerUnit(Json.Text(WeightUnit, "dimensional_weight_weight_unit"));
            var volume = size[0] * size[1] * size[2];
            return Exact.FloorDiv(volume * weight, BigInteger.Pow(length, 3) * Json.Int(Divisor, "dimensional_weight_divisor"));
        }
    }
}

/// <summary>
/// One container's carrier charge from the request's own rate card: billed weight up to whole
/// grams, the first bracket that holds it, the minimum charge, then the fuel surcharge rounded up.
/// </summary>
internal static class RateCard
{
    public static BigInteger Charge(ContainerSpec spec, BigInteger billedTicks)
    {
        if (spec.RateTable is JsonNull)
        {
            throw MalformedResultException.ContractBreak(
                $"container {PyFormat.Repr(spec.Id)} has no rate_table, so lowest_landed_cost cannot price it");
        }
        var table = spec.RateTable;
        var brackets = Json.Items(Json.At(table, "weight_brackets_g"), "weight_brackets_g").Select(bound => Json.PyInt(bound, "bracket")).ToList();
        var prices = Json.Items(Json.At(table, "prices_minor"), "prices_minor").Select(price => Json.PyInt(price, "price")).ToList();
        var grams = Exact.CeilDiv(billedTicks, TickUnits.TicksPerGram);
        foreach (var (bound, price) in brackets.Zip(prices))
        {
            if (grams <= bound)
            {
                var floor = Json.PyInt(Json.Get(table, "minimum_charge_minor", new JsonInteger(0)), "minimum_charge_minor");
                var baseCharge = BigInteger.Max(price, floor);
                var permille = Json.PyInt(Json.Get(table, "fuel_surcharge_permille", new JsonInteger(0)), "fuel_surcharge_permille");
                return baseCharge + Exact.CeilDiv(baseCharge * permille, 1000);
            }
        }
        if (brackets.Count == 0)
        {
            throw MalformedResultException.ShapeBreak("an empty rate table");
        }
        throw MalformedResultException.ContractBreak(FormattableString.Invariant(
            $"container {PyFormat.Repr(spec.Id)} bills at {grams} g, above its rate table's last bracket ({brackets[^1]} g); the shipment has no published price"));
    }
}
