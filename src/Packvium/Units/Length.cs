using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace Packvium.Units;

/// <summary>Exact fixed-point length. One tick equals 1/16000 mm.</summary>
public readonly record struct Length : IComparable<Length>
{
    public const long TicksPerMm = 16_000;
    public const long TicksPerInch = 406_400;

    private static readonly FrozenDictionary<string, long> Multipliers = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["tick"] = 1,
        ["ticks"] = 1,
        ["mm"] = TicksPerMm,
        ["millimeter"] = TicksPerMm,
        ["millimeters"] = TicksPerMm,
        ["cm"] = TicksPerMm * 10,
        ["m"] = TicksPerMm * 1000,
        ["in"] = TicksPerInch,
        ["inch"] = TicksPerInch,
        ["inches"] = TicksPerInch,
        ["ft"] = TicksPerInch * 12,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The unit codes the request schema names wherever it asks for a length unit by itself (an
    /// output unit, a dimensional-weight unit); a measure's own unit may also be spelt out.
    /// </summary>
    public static ImmutableArray<string> Codes { get; } = ["mm", "cm", "m", "in", "ft"];

    /// <summary>Suffixes <c>Length.parse</c> recognises in a string, in its regex's order.</summary>
    private static readonly string[] Suffixes = ["mm", "cm", "m", "in", "inch", "inches", "ft", "tick", "ticks"];

    public Length(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentException("length cannot be negative");
        }
        Ticks = ticks;
    }

    public long Ticks { get; }

    public static Length Zero => default;

    public static Length Of(Rational value, string unit = "mm", Rounding rounding = Rounding.Nearest) =>
        new(UnitTicks.Scale(value, MultiplierOf(unit, strip: true), rounding, "length"));

    public static Length Of(string value, string unit = "mm", Rounding rounding = Rounding.Nearest) =>
        Of(QuantityText.Parse(value), unit, rounding);

    public static Length Of(BigInteger value, string unit = "mm", Rounding rounding = Rounding.Nearest) =>
        Of(Rational.Of(value), unit, rounding);

    public static Length Mm(string value, Rounding rounding = Rounding.Nearest) => Of(value, "mm", rounding);

    public static Length Inches(string value, Rounding rounding = Rounding.Nearest) => Of(value, "in", rounding);

    /// <summary>
    /// <c>Length.parse</c> for a string: a trailing unit (<c>"12 3/8 in"</c>) wins over
    /// <paramref name="defaultUnit"/>.
    /// </summary>
    public static Length Parse(string value, string defaultUnit = "mm", Rounding rounding = Rounding.Nearest) =>
        UnitTicks.SplitSuffix(value, Suffixes) is var (quantity, unit)
            ? Of(quantity, unit, rounding)
            : Of(value, defaultUnit, rounding);

    public Rational AsFraction(string unit = "mm") => new(Ticks, MultiplierOf(unit, strip: false));

    public string DecimalText(string unit = "mm", int places = 8) =>
        DecimalRendering.Render(Ticks, MultiplierOf(unit, strip: false), places);

    public int CompareTo(Length other) => Ticks.CompareTo(other.Ticks);

    public static bool operator <(Length left, Length right) => left.Ticks < right.Ticks;

    public static bool operator >(Length left, Length right) => left.Ticks > right.Ticks;

    public static bool operator <=(Length left, Length right) => left.Ticks <= right.Ticks;

    public static bool operator >=(Length left, Length right) => left.Ticks >= right.Ticks;

    /// <summary>Ticks per one <paramref name="unit"/>; throws for a unit the table does not hold.</summary>
    /// <summary>Whether <paramref name="unit"/>, lowered and stripped as the parser reads it, is a length unit.</summary>
    public static bool Knows(string unit) => Multipliers.ContainsKey(PythonText.UnitKey(unit, strip: true));

    /// <summary>
    /// The output unit a request without <c>output.length_unit</c> is written in: the request's own
    /// <c>units.length</c> spelt as the schema's output code, case and surrounding space ignored and a
    /// spelt-out name folded to its code (<c>"Inches"</c> is <c>in</c>). A unit no output code names,
    /// the internal <c>tick</c>, gives <c>mm</c>, the default of both blocks. O(1).
    /// </summary>
    public static string OutputCode(string requestUnit) =>
        Multipliers.TryGetValue(PythonText.UnitKey(requestUnit, strip: true), out var multiplier)
            ? Codes.FirstOrDefault(code => Multipliers[code] == multiplier, "mm")
            : "mm";

    public static long MultiplierOf(string unit, bool strip) =>
        Multipliers.TryGetValue(PythonText.UnitKey(unit, strip), out var multiplier)
            ? multiplier
            : throw (strip
                ? new UnitRefusal("invalid_unit", $"unsupported length unit: {unit}")
                : new ArgumentException($"unsupported length unit: {unit}"));
}
