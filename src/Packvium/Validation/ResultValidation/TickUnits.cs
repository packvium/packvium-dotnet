using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>One unit table in the reference's order: a name and its ticks per unit.</summary>
internal sealed record UnitTable(ImmutableArray<(string Name, long Ticks)> Units)
{
    /// <summary>The names longest first, ties in table order, as Python's stable reverse sort leaves them.</summary>
    public ImmutableArray<string> SuffixOrder { get; } = [.. Units.Select(unit => unit.Name).OrderByDescending(name => name.Length)];

    /// <summary><c>table[unit]</c>, an exact key.</summary>
    public BigInteger PerUnit(string unit)
    {
        foreach (var (name, ticks) in Units)
        {
            if (name == unit)
            {
                return ticks;
            }
        }
        throw MalformedResultException.ShapeBreak($"unknown unit \"{unit}\"");
    }
}

/// <summary>
/// Measures to exact ticks, re-derived from the unit tables rather than taken from the engine:
/// a validator built on the engine's unit parser could not catch a bug in it.
/// </summary>
internal static class TickUnits
{
    public const long TicksPerMillimetre = 16_000;

    public const long TicksPerGram = 8_000_000;

    public const long TicksPerKilogram = 8_000_000_000;

    /// <summary>Length ticks per unit. One tick is 1/16000 mm, so an inch is exactly 406 400 ticks.</summary>
    public static readonly UnitTable Length = new([
        ("mm", 16_000), ("cm", 160_000), ("m", 16_000_000), ("in", 406_400), ("inch", 406_400),
        ("inches", 406_400), ("ft", 4_876_800), ("tick", 1), ("ticks", 1),
    ]);

    /// <summary>Weight ticks per unit. One tick is 1/8 microgram.</summary>
    public static readonly UnitTable Weight = new([
        ("mg", 8_000), ("g", 8_000_000), ("kg", 8_000_000_000), ("oz", 226_796_185),
        ("lb", 3_628_738_960), ("lbs", 3_628_738_960), ("tick", 1), ("ticks", 1),
    ]);

    /// <summary>
    /// <c>to_ticks</c>: a number, a string with an optional unit suffix, or <c>{value, unit}</c>,
    /// to exact ticks, rounding halves to even.
    /// </summary>
    public static BigInteger ToTicks(JsonValue measure, string defaultUnit, UnitTable table)
    {
        var unit = defaultUnit;
        var raw = measure;
        if (measure is JsonObject)
        {
            raw = Json.At(measure, "value");
            unit = Json.Text(Json.Get(measure, "unit", new JsonString(defaultUnit)), "a measure's unit");
        }
        else if (measure is JsonString text && UnitSuffix(PythonText.Strip(text.Value), table) is var (number, suffix))
        {
            raw = new JsonString(number);
            unit = suffix;
        }
        return RoundHalfEven(ParseRational(raw).Multiply(table.PerUnit(Lower(unit))));
    }

    /// <summary><c>dimensions_to_ticks</c>: the three sides of a <c>{length, width, height}</c> object.</summary>
    public static ImmutableArray<BigInteger> Dimensions(JsonValue value, string unit) =>
    [
        ToTicks(Json.At(value, "length"), unit, Length),
        ToTicks(Json.At(value, "width"), unit, Length),
        ToTicks(Json.At(value, "height"), unit, Length),
    ];

    /// <summary>
    /// <c>parse_rational</c>: an integer exactly, anything else through its <c>str()</c> as a
    /// fraction, or a mixed fraction (<c>12 3/8</c>) when the text holds a space.
    /// </summary>
    public static Ratio ParseRational(JsonValue value)
    {
        if (Json.IntInstance(value) is { } integer)
        {
            return Ratio.Integer(integer);
        }
        var stripped = PythonText.Strip(PyFormat.Str(value));
        if (!stripped.Contains(' ', System.StringComparison.Ordinal))
        {
            return Fractions.Parse(stripped);
        }
        var split = SplitOnce(stripped);
        var whole = Fractions.Parse(split.Whole);
        var fraction = Fractions.Parse(split.Fraction);
        return whole.Add(split.Whole.StartsWith('-') ? fraction.Negate() : fraction);
    }

    /// <summary><c>Fraction(str(value))</c>, the reference's reading of a ratio field.</summary>
    public static Ratio RatioOf(JsonValue value) => Fractions.Parse(PyFormat.Str(value));

    /// <summary>
    /// The longest unit suffix the text ends with, case-insensitively, that leaves a non-empty
    /// number before it.
    /// </summary>
    private static (string Number, string Suffix)? UnitSuffix(string stripped, UnitTable table)
    {
        var lowered = Lower(stripped);
        foreach (var suffix in table.SuffixOrder)
        {
            if (!lowered.EndsWith(suffix, System.StringComparison.Ordinal) || stripped.Length < suffix.Length)
            {
                continue;
            }
            var number = PythonText.Strip(stripped[..^suffix.Length]);
            if (number.Length > 0)
            {
                return (number, suffix);
            }
        }
        return null;
    }

    /// <summary>
    /// <c>str.lower()</c> as far as an ASCII unit table can tell: ASCII capitals and the Kelvin
    /// sign lower to an ASCII letter, and nothing else lowers into one.
    /// </summary>
    private static string Lower(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                >= 'A' and <= 'Z' => (char)(character + ('a' - 'A')),
                'K' => 'k',
                _ => character,
            });
        }
        return builder.ToString();
    }

    /// <summary><c>text.split(None, 1)</c> for a stripped text that holds whitespace.</summary>
    private static (string Whole, string Fraction) SplitOnce(string text)
    {
        var end = 0;
        while (end < text.Length && !PythonText.IsSpace(text[end]))
        {
            end++;
        }
        var start = end;
        while (start < text.Length && PythonText.IsSpace(text[start]))
        {
            start++;
        }
        return (text[..end], text[start..]);
    }

    private static BigInteger RoundHalfEven(Ratio scaled)
    {
        var floor = scaled.Floor();
        var twiceRemainder = (scaled.Numerator - floor * scaled.Denominator) * 2;
        var order = twiceRemainder.CompareTo(scaled.Denominator);
        return order > 0 || (order == 0 && !floor.IsEven) ? floor + 1 : floor;
    }
}
