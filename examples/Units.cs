using System;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;
using UnitLength = Packvium.Units.Length;

namespace Packvium.Examples;

/// <summary>
/// Units: why there are no floats anywhere, and what you can type.
///
/// Run it:
///
///     dotnet run --project examples -- units
///
/// Every length and weight is an exact integer count of ticks -- 1/16000 mm for length,
/// 1/8 microgram for weight -- and nothing in the request, the search or the result is a
/// floating-point number.
/// </summary>
public static class Units
{
    public static void Run()
    {
        // Integers, decimals, fractions and mixed fractions, in mm, cm, m, in and ft: a spec
        // sheet says "12 3/8 in", and retyping it as 12.375 is where mistakes live.
        foreach (var text in (string[])["30", "30.5 mm", "3/16 in", "12 3/8 in", "2 ft", "1.5 m"])
        {
            var length = UnitLength.Parse(text);
            Console.WriteLine($"{text,12}  ->  {length.Ticks,12} ticks  =  {length.DecimalText("mm")} mm");
        }
        Console.WriteLine();
        foreach (var text in (string[])["450 g", "12 oz", "1.5 kg", "2 3/4 lb"])
        {
            var weight = Weight.Parse(text);
            Console.WriteLine($"{text,12}  ->  {weight.Ticks,14} ticks  =  {weight.DecimalText("g")} g");
        }

        // 1/16000 mm makes every binary fraction of an inch down to 1/128 a whole number of ticks.
        Console.WriteLine();
        Console.WriteLine($"1 inch is {UnitLength.TicksPerInch} ticks, so 1/128 in is {UnitLength.TicksPerInch / 128} ticks exactly");
        var sixteenth = UnitLength.Parse("1/16 in");
        Console.WriteLine($"1/16 in as a fraction of a mm: {sixteenth.AsFraction("mm")} == {new Rational(sixteenth.Ticks, UnitLength.TicksPerMm)}");
        Console.WriteLine($"128 x (1/128 in) == 1 in ? {new UnitLength(UnitLength.Parse("1/128 in").Ticks * 128) == UnitLength.Parse("1 in")}");
        // A third of an inch is not a binary fraction, so it is rounded -- deterministically and visibly.
        var third = UnitLength.Parse("1/3 in");
        Console.WriteLine($"  3 x (1/3 in) == 1 in ? {new UnitLength(third.Ticks * 3) == UnitLength.Parse("1 in")} "
                          + $"-- 1/3 in is {UnitLength.TicksPerInch}/3 ticks, which is not an integer");

        // Where floats would actually bite, and the same three tenths as lengths.
        Console.WriteLine();
        Console.WriteLine($"0.1 + 0.2 == 0.3 in floats: {0.1 + 0.2 == 0.3}");
        Console.WriteLine($"the same three tenths as lengths: {UnitLength.Parse("0.1 mm").Ticks + UnitLength.Parse("0.2 mm").Ticks == UnitLength.Parse("0.3 mm").Ticks}");

        // A 100 mm cube into a 100 mm opening is a fit; one tick over is a refusal.
        Console.WriteLine();
        var opening = Dimensions.Mm("100", "100", "100");
        Console.WriteLine($"exactly 100mm fits:       {Dimensions.Mm("100", "100", "100").FitsInside(opening)}");
        var oneTickOver = new Dimensions(new UnitLength(opening.Length.Ticks + 1), opening.Width, opening.Height);
        Console.WriteLine($"one tick over does not:   {oneTickOver.FitsInside(opening)}");

        // Dimensional weight, the number carriers bill on: volume over a divisor, rounded up.
        Console.WriteLine();
        var billed = DimensionalWeight.Of(Dimensions.Mm("400", "400", "400"), 5000, "cm", "kg");
        Console.WriteLine($"400mm cube = 64,000 cm^3 / 5,000 = {billed.DecimalText("kg")} kg dimensional weight");

        // The wire carries the decimal string, never a float, so what you typed is what the other language reads.
        Console.WriteLine();
        Console.WriteLine($"on the wire: {Dict(ResultWriter.Length(UnitLength.Parse("12 3/8 in"), "mm"))} {Dict(ResultWriter.Weight(Weight.Parse("2 3/4 lb"), "g"))}");

        // An example must not change answer merely because the host was busy. This solve needs
        // a fraction of the budget below; the generous wall-clock value is only a safety fuse, so
        // a loaded machine cannot cut the multi-start portfolio short and let a different start win.
        var result = new Packer(PackingConfig.Balanced(timeLimitMs: 60_000)).Pack(
            [new Item("shelf", Dimensions.Inches("12 3/8", "9 1/2", "3/4"), Weight.Parse("2 3/4 lb"), quantity: 3)],
            [new Container("carton", Dimensions.Inches("13", "10", "4"), maxPayload: Weight.Parse("20 lb"))]);
        Console.WriteLine($"packed {result.PackedItemCount} shelves, {result.Unpacked.Length} left over");
    }

    /// <summary>A measure the way Python prints the dict: <c>{'ticks': 1, 'value': '1', 'unit': 'mm'}</c>.</summary>
    private static string Dict(JsonObject measure) =>
        $"{{'ticks': {((JsonInteger)measure.Require("ticks")).Value}, 'value': '{((JsonString)measure.Require("value")).Value}', 'unit': '{((JsonString)measure.Require("unit")).Value}'}}";
}
