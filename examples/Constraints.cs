using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// Constraints: the rules a real load has to obey, and how to read the refusals.
///
/// Run it:
///
///     dotnet run --project examples -- constraints
///
/// Each rule is a hard predicate, never a preference: a placement that breaks one is not
/// worse, it is not considered. When a rule leaves an item behind, the result says which
/// item and why, in a sentence a person can read.
/// </summary>
public static class Constraints
{
    /// <summary>
    /// An example must not change answer merely because the host was busy. These solves need
    /// a fraction of the budget; the generous wall-clock value is only a safety fuse, so a
    /// loaded machine cannot cut the multi-start portfolio short and let a different start win.
    /// </summary>
    private const long SafetyFuseMs = 60_000;

    public static void Run()
    {
        Item[] items =
        [
            // keepUpright forbids every rotation that would tip the item over.
            new("paint", Dimensions.Mm("200", "200", "250"), Weight.Parse("5 kg"), quantity: 2, keepUpright: true),
            // mustBeOnFloor pins it to the floor; maxTopLoad caps what rests directly on it.
            new("glass-panel", Dimensions.Mm("400", "300", "40"), Weight.Parse("8 kg"), quantity: 1, mustBeOnFloor: true,
                maxTopLoad: Weight.Parse("2 kg")),
            // stackable: false means nothing may be placed on this item at all.
            new("cake", Dimensions.Mm("250", "250", "150"), Weight.Parse("1 kg"), quantity: 1, stackable: false),
            // incompatibleTags is checked both ways, so tagging one side is enough.
            new("bleach", Dimensions.Mm("120", "120", "300"), Weight.Parse("2 kg"), quantity: 2, tags: ["hazmat"],
                incompatibleTags: ["food"]),
            new("flour", Dimensions.Mm("200", "150", "100"), Weight.Parse("1500 g"), quantity: 3, tags: ["food"]),
            // Longer than the crate in every orientation: here to show what a refusal looks like.
            new("ladder", Dimensions.Mm("1800", "300", "100"), Weight.Parse("6 kg"), quantity: 1),
        ];
        // maxPayload is the weight the container may carry, excluding its own tare.
        Container[] containers =
        [
            new("crate", Dimensions.Mm("600", "500", "500"), tareWeight: Weight.Parse("3 kg"),
                maxPayload: Weight.Parse("25 kg"), costMinor: 400),
        ];
        var result = new Packer(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs)).Pack(items, containers);

        Console.WriteLine($"status: {result.Status.Value()}");
        Console.WriteLine($"containers opened: {result.Containers.Length}");
        foreach (var (packed, index) in result.Containers.Select((packed, index) => (packed, index + 1)))
        {
            Console.WriteLine($"\ncrate #{index} ({packed.Container.Id}): {packed.Placements.Length} placement(s)");
            foreach (var placement in packed.Placements)
            {
                var position = placement.Position;
                Console.WriteLine($"  {placement.Instance.Item.Id,-12} at ({Mm(position.X)}, {Mm(position.Y)}, {Mm(position.Z)}) mm");
            }
        }
        // Two crates for a load that fits one by volume: bleach refuses food, so the
        // constraint, not the geometry, opened the second crate.
        if (result.Unpacked.IsEmpty)
        {
            Console.WriteLine("\neverything fitted -- widen the crate or add items to see a refusal explained");
        }
        else
        {
            Console.WriteLine("\nnot packed:");
            foreach (var unpacked in result.Unpacked)
            {
                Console.WriteLine($"  {unpacked.Instance.Item.Id,-12} {Explanations.ExplainUnpackedItem(unpacked)}");
            }
        }

        // Each rule below runs twice -- without it and with it -- so the rule, not the
        // geometry, is provably what changed the answer.
        Container[] shelf = [new("shelf", Dimensions.Mm("800", "400", "500"), maxPayload: Weight.Parse("40 kg"))];

        // allowedRotations narrows the six orientations; Rotations.Upright keeps height vertical.
        var pole = Dimensions.Mm("90", "90", "700");
        Compare("allowed_rotations -- a pole that only fits lying down, forbidden from lying down",
            [new Item("pole", pole, Weight.Parse("1 kg"))],
            [new Item("pole", pole, Weight.Parse("1 kg"), allowedRotations: Rotations.Upright)],
            shelf);

        // maxStackedItems caps how many units may sit above one item.
        Container[] column = [new("column", Dimensions.Mm("160", "160", "600"), maxPayload: Weight.Parse("40 kg"))];
        var tin = Dimensions.Mm("150", "150", "120");
        Compare("max_stacked_items -- five tins fit in one column; three-high needs two columns",
            [new Item("tin", tin, Weight.Parse("800 g"), quantity: 5)],
            [new Item("tin", tin, Weight.Parse("800 g"), quantity: 5, maxStackedItems: 3)],
            column);

        // minimumSupportRatio: how much of an item's base must rest on something solid.
        Container[] ledge = [new("ledge", Dimensions.Mm("400", "400", "350"), maxPayload: Weight.Parse("40 kg"))];
        var plinth = new Item("plinth", Dimensions.Mm("200", "200", "300"), Weight.Parse("5 kg"), mustBeOnFloor: true);
        var slab = Dimensions.Mm("400", "400", "60");
        Compare("minimum_support_ratio -- a slab perched on a quarter of its base",
            [plinth, new Item("slab", slab, Weight.Parse("9 kg"))],
            [plinth, new Item("slab", slab, Weight.Parse("9 kg"), minimumSupportRatio: 0.9)],
            ledge);

        // group is atomic: every member ships in one container or none of them does.
        Dimensions[] parts = [Dimensions.Mm("200", "200", "100"), Dimensions.Mm("200", "200", "100"), Dimensions.Mm("900", "100", "100")];
        Compare("group -- one member cannot be placed, so none of them is",
            [.. parts.Select((d, n) => new Item($"kit-{n + 1}", d, Weight.Parse("2 kg")))],
            [.. parts.Select((d, n) => new Item($"kit-{n + 1}", d, Weight.Parse("2 kg"), group: "assembly"))],
            shelf);
    }

    private static void Compare(string rule, IReadOnlyList<Item> without, IReadOnlyList<Item> withRule,
        IReadOnlyList<Container> containers)
    {
        Console.WriteLine($"\n{rule}");
        foreach (var (label, variant) in (ValueTuple<string, IReadOnlyList<Item>>[])[("without the rule", without), ("with the rule   ", withRule)])
        {
            var outcome = new Packer(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs)).Pack(variant, containers);
            Console.WriteLine($"  {label}: {outcome.Containers.Length} container(s), "
                              + $"{outcome.Containers.Sum(c => c.Placements.Length)} placed, {outcome.Unpacked.Length} refused");
            foreach (var unpacked in outcome.Unpacked)
            {
                Console.WriteLine($"      {Explanations.ExplainUnpackedItem(unpacked)}");
            }
        }
    }

    /// <summary>Positions are exact integers in 1/16000 mm; render them for a person.</summary>
    private static string Mm(long ticks) => new Length(ticks).DecimalText("mm");
}
