using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;
using Packvium.Validation;

namespace Packvium.Examples;

/// <summary>
/// Even out the weight across pallets after packing.
///
/// Run it:
///
///     dotnet run --project examples -- rebalancing
///
/// The packer opens as few containers as it can and fills the first one tightly. For two pallets
/// on a forklift, that is the wrong shape of answer: one pallet near its weight limit and one
/// nearly empty, when two pallets of similar weight are safer to lift, wrap and stack.
///
/// <c>WeightRebalance.Rebalance</c> is a separate, opt-in step you run on a finished packing. It
/// moves one item at a time from the heaviest container to a lighter one, and keeps a move only
/// when the item has a real place in its new container and the whole packing still passes the
/// independent validator. It never adds a container, never drops an item, and stops when no
/// single move narrows the gap (or after <c>maxMoves</c>, 64 by default). It is greedy, so the
/// result is better, not necessarily the most even split possible.
/// </summary>
public static class Rebalancing
{
    private static readonly Item[] Items =
    [
        new("paint-20l", Dimensions.Mm("300", "300", "380"), Weight.Parse("24 kg"), 16, keepUpright: true),
        new("brush-case", Dimensions.Mm("400", "300", "200"), Weight.Parse("3 kg"), 10),
    ];

    private static readonly Container Pallet = new("pallet", Dimensions.Mm("1200", "800", "1000"),
        tareWeight: Weight.Parse("25 kg"), maxPayload: Weight.Parse("300 kg"));

    // Counted work, not a clock, decides the plan (see Reproducibility.cs); the time limits here
    // and in the rebalance below are fuses far above what either step needs.
    private static readonly PackingConfig Config = new(SolverProfile.Balanced, 60_000,
        effortBudget: new EffortBudget(MaxRestarts: 8));

    public static void Run()
    {
        Python.Section("1. As packed: as few pallets as possible, the first one full");
        var result = new Packer(Config).Pack(Items, [Pallet]);
        Console.WriteLine($"  {Weights(result.Containers)}");

        Python.Section("2. Rebalanced");
        // The request is what the validator checks the new packing against: every item that was
        // asked for must still be accounted for, exactly once.
        var request = new PackingRequest(Items, [Pallet]);
        var rebalanced = WeightRebalance.Rebalance(request, result.Containers, result.Unpacked, Config, timeLimitMs: 60_000);
        foreach (var move in rebalanced.Moves)
        {
            Console.WriteLine($"  moved {move.ItemId} from {move.FromContainerId} to {move.ToContainerId}");
        }
        Console.WriteLine($"  {Weights(rebalanced.Containers)}");
        var report = IndependentSolutionValidator.Validate(request, rebalanced.Containers, Config.MinimumSupportRatio,
            Config.Clearance, result.Unpacked);
        Console.WriteLine($"  still a valid packing: {report.Valid}");
        Python.Say("",
            "Heavy items move first, because each one narrows the gap most. The paint stays",
            "upright in its new place and nothing is left resting on a gap where a moved can",
            "used to be -- a move that would do that is rejected, not made.");

        Python.Section("3. When there is nothing to improve");
        Item[] cases = [Items[1]];
        var single = new Packer(Config).Pack(cases, [Pallet]);
        var unchanged = WeightRebalance.Rebalance(new PackingRequest(cases, [Pallet]), single.Containers, single.Unpacked,
            Config, timeLimitMs: 60_000);
        Console.WriteLine($"  {Weights(unchanged.Containers)}  improved: {unchanged.Improved}");
        Python.Say("",
            "One pallet has nothing to balance against, and the result says so rather than",
            "inventing a second pallet. The same holds when every candidate move would break a",
            "rule: `moves` is empty and the packing comes back as it was.");
    }

    private static string Weights(IEnumerable<PackedContainer> containers) =>
        string.Join(", ", containers.Select(packed =>
            $"{packed.Id} {packed.PayloadWeight.DecimalText("kg", 1)} kg ({packed.Placements.Length} items)"));
}
