using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// Objectives: the same items, different answers, depending on what "best" means.
///
/// Run it:
///
///     dotnet run --project examples -- objectives
///
/// Every objective is an exact integer vector compared lexicographically, and every one of
/// them puts "how many items were left behind" first: no objective trades completeness away.
/// </summary>
public static class Objectives
{
    /// <summary>
    /// An example must not change answer merely because the host was busy. These solves need
    /// a fraction of the budget; the generous wall-clock value is only a safety fuse, so a
    /// loaded machine cannot cut the multi-start portfolio short and let a different start win.
    /// </summary>
    private const long SafetyFuseMs = 60_000;

    private static readonly Item[] Widgets = [new("widget", Dimensions.Mm("100", "100", "100"), Weight.Parse("500 g"), quantity: 8)];

    public static void Run()
    {
        // default: fewest containers, then the tightest fit.
        var snug = new Container("snug", Dimensions.Mm("300", "300", "300"), maxPayload: Weight.Parse("20 kg"), costMinor: 500);
        var roomy = new Container("roomy", Dimensions.Mm("400", "400", "400"), maxPayload: Weight.Parse("20 kg"), costMinor: 150);
        Console.WriteLine($"default           {Solve(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs), [snug, roomy])}");

        // lowest_cost: the cheapest packaging, which is why the roomy box wins here.
        Console.WriteLine($"lowest_cost       {Solve(new PackingConfig(objective: "lowest_cost", timeLimitMs: SafetyFuseMs), [snug, roomy])}");

        // shipping_cost: carrier-billable weight, the greater of gross and dimensional. It needs a
        // divisor, and refuses rather than guessing one.
        var byWeight = new PackingConfig(objective: "shipping_cost", dimensionalWeightDivisor: 5000,
            dimensionalWeightLengthUnit: "cm", dimensionalWeightWeightUnit: "kg", timeLimitMs: SafetyFuseMs);
        Console.WriteLine($"shipping_cost     {Solve(byWeight, [snug, roomy])}");

        // lowest_landed_cost: carrier-billable money, from a rate card carried in the request. A
        // steep first bracket makes the lighter shipment the dearer one.
        var dearPerGram = new Container("snug", Dimensions.Mm("300", "300", "300"), maxPayload: Weight.Parse("20 kg"),
            rateTable: new RateTable([6_000, 20_000], [2_400, 3_100]));
        var cheapPerGram = new Container("roomy", Dimensions.Mm("400", "400", "400"), maxPayload: Weight.Parse("20 kg"),
            rateTable: new RateTable([6_000, 20_000], [900, 1_500]));
        var byMoney = new PackingConfig(objective: "lowest_landed_cost", dimensionalWeightDivisor: 5000,
            dimensionalWeightLengthUnit: "cm", dimensionalWeightWeightUnit: "kg", timeLimitMs: SafetyFuseMs);
        Console.WriteLine($"landed_cost       {Solve(byMoney, [dearPerGram, cheapPerGram])}");

        // A rate card that stops short of the shipment is a refusal, never a silent clamp.
        var tooNarrow = new Container("roomy", Dimensions.Mm("400", "400", "400"), maxPayload: Weight.Parse("20 kg"),
            rateTable: new RateTable([2_000], [900]));
        try
        {
            Solve(byMoney, [tooNarrow]);
        }
        catch (UnratedWeightException refusal)
        {
            Console.WriteLine($"landed_cost*      refused: {refusal.Message}");
        }

        // open_dimension_height: the shortest stack, for a box with no lid or a doorway to clear.
        Console.WriteLine($"open_dimension    {Solve(new PackingConfig(objective: "open_dimension_height", timeLimitMs: SafetyFuseMs), [snug, roomy])}");

        // maximum_value: when not everything fits, leave the cheap things behind. One tiny box,
        // so there is a choice to make at all.
        Container[] tiny = [new("tiny", Dimensions.Mm("200", "100", "100"), maxPayload: Weight.Parse("20 kg"), quantity: 1)];
        Item[] mixed =
        [
            new("gold", Dimensions.Mm("100", "100", "100"), Weight.Parse("500 g"), quantity: 2, value: 90_000),
            new("gravel", Dimensions.Mm("100", "100", "100"), Weight.Parse("500 g"), quantity: 2, value: 10),
        ];
        var result = new Packer(new PackingConfig(objective: "maximum_value", timeLimitMs: SafetyFuseMs)).Pack(mixed, tiny);
        var kept = result.Containers.SelectMany(c => c.Placements).Select(p => p.Instance.Item.Id).Order(StringComparer.Ordinal);
        var left = result.Unpacked.Select(u => u.Instance.Item.Id).Order(StringComparer.Ordinal);
        Console.WriteLine($"maximum_value     packed: {Python.List(kept)} left behind: {Python.List(left)}");
    }

    private static string Solve(PackingConfig config, IEnumerable<Container> containers)
    {
        var result = new Packer(config).Pack(Widgets, containers);
        var first = result.Containers.IsEmpty ? "none" : result.Containers[0].Container.Id;
        return $"('{first}', {Python.Tuple(result.Score)})";
    }
}
