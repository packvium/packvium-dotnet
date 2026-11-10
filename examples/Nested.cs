using System;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// Nested packing: units into cartons, cartons onto a pallet, in one call.
///
/// Run it:
///
///     dotnet run --project examples -- nested
///
/// Every container a level opens becomes an item for the next level, at its outer size and
/// its gross weight -- so the pallet sees the cartons the way a forklift does.
/// </summary>
public static class Nested
{
    public static void Run()
    {
        Item[] items =
        [
            new("mug", Dimensions.Mm("120", "120", "100"), Weight.Parse("400 g"), quantity: 24),
            new("plate", Dimensions.Mm("260", "260", "20"), Weight.Parse("600 g"), quantity: 16),
        ];
        PackingLevel[] levels =
        [
            // Level 1 chooses cartons; outerDimensions is what the next level packs.
            new("carton",
            [
                new Container("box-s", Dimensions.Mm("300", "300", "300"), outerDimensions: Dimensions.Mm("310", "310", "310"),
                    tareWeight: Weight.Parse("300 g"), maxPayload: Weight.Parse("15 kg"), costMinor: 120),
                new Container("box-l", Dimensions.Mm("400", "400", "400"), outerDimensions: Dimensions.Mm("412", "412", "412"),
                    tareWeight: Weight.Parse("500 g"), maxPayload: Weight.Parse("25 kg"), costMinor: 180),
            ],
            // One deterministic start is enough to teach nesting; the generous clock is only a fuse.
            PackingConfig.Fast(timeLimitMs: 60_000)),
            // Level 2 puts those cartons on a pallet.
            new("pallet",
            [
                new Container("euro", Dimensions.Mm("1200", "800", "1400"), tareWeight: Weight.Parse("25 kg"),
                    maxPayload: Weight.Parse("700 kg"), costMinor: 1500),
            ]),
        ];
        var result = NestedPacker.Pack(items, levels);

        // One result per level, in the order the levels were given.
        foreach (var (level, packedLevel) in levels.Zip(result.Levels))
        {
            Console.WriteLine($"== {level.Name} ==");
            Console.WriteLine($"   status: {packedLevel.Status.Value()}");
            Console.WriteLine($"   containers used: {packedLevel.Containers.Length}");
            foreach (var packed in packedLevel.Containers)
            {
                Console.WriteLine($"     {packed.Container.Id,-8} holding {packed.Placements.Length} item(s)");
            }
            if (!packedLevel.Unpacked.IsEmpty)
            {
                Console.WriteLine($"   left over: {packedLevel.Unpacked.Length}");
            }
            Console.WriteLine();
        }
        Console.WriteLine($"{result.Levels[0].Containers.Length} carton(s) travelling on {result.Levels[^1].Containers.Length} pallet(s)");
    }
}
