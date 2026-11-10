using System;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// The smallest useful call: some items, some boxes, one answer.
///
/// Run it:
///
///     dotnet run --project examples -- basic
///
/// <c>Dimensions.Mm</c> and <c>Dimensions.Inches</c> are both exact: "4 in" is stored as an exact
/// tick count, so an imperial spec sheet and a metric container agree without a tolerance.
/// <c>keepUpright</c> is a rule, not a hint, and <c>costMinor</c> is what the box costs you --
/// the default objective ignores it; Objectives.cs shows the ones that do not.
/// </summary>
public static class Basic
{
    public static void Run()
    {
        var result = new Packer(PackingConfig.Balanced()).Pack(
            [
                new Item("book", Dimensions.Mm("210", "140", "30"), Weight.Parse("450 g"), quantity: 4),
                new Item("mug", Dimensions.Inches("4", "4", "5"), Weight.Parse("12 oz"), quantity: 2, keepUpright: true),
            ],
            [
                new Container("box-m", Dimensions.Mm("400", "300", "250"), maxPayload: Weight.Parse("20 kg"), costMinor: 180),
                new Container("box-l", Dimensions.Mm("500", "400", "350"), maxPayload: Weight.Parse("30 kg"), costMinor: 250),
            ]);

        Console.WriteLine($"status     {result.Status.Value()}");
        Console.WriteLine($"containers {Python.List(result.Containers.Select(c => c.Container.Id))}");
        Console.WriteLine($"packed     {result.Containers.Sum(c => c.Placements.Length)} of 6");
        Console.WriteLine($"unpacked   [{string.Join(", ", result.Unpacked.Select(u => $"('{u.Instance.Item.Id}', '{u.Reason}')"))}]");
        Console.WriteLine($"score      {Python.Tuple(result.Score)}  <- lexicographic, exact integers, lower is better");
        Console.WriteLine();
        Console.WriteLine("the full result as JSON is what Serialization.cs explores:");
        Console.WriteLine(Python.List(ResultWriter.Write(result).Keys.Order(CodePointComparer.Instance)));
    }
}
