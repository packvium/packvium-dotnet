using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;

namespace Packvium.Units;

/// <summary>Exact weight. One tick equals 1/8 microgram.</summary>
public readonly record struct Weight : IComparable<Weight>
{
    public const long TicksPerMg = 8_000;
    public const long TicksPerG = 8_000_000;
    public const long TicksPerKg = 8_000_000_000;
    public const long TicksPerOz = 226_796_185;
    public const long TicksPerLb = 3_628_738_960;

    private static readonly FrozenDictionary<string, long> Multipliers = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["tick"] = 1,
        ["ticks"] = 1,
        ["mg"] = TicksPerMg,
        ["g"] = TicksPerG,
        ["kg"] = TicksPerKg,
        ["oz"] = TicksPerOz,
        ["lb"] = TicksPerLb,
        ["lbs"] = TicksPerLb,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Suffixes <c>Weight.parse</c> recognises in a string, in its regex's order.</summary>
    private static readonly string[] Suffixes = ["mg", "kg", "g", "oz", "lb", "lbs", "tick", "ticks"];

    public Weight(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentException("weight cannot be negative");
        }
        Ticks = ticks;
    }

    public long Ticks { get; }

    public static Weight Zero => default;

    public static Weight Of(Rational value, string unit = "g", Rounding rounding = Rounding.Nearest) =>
        new(UnitTicks.Scale(value, MultiplierOf(unit, strip: true), rounding, "weight"));

    public static Weight Of(string value, string unit = "g", Rounding rounding = Rounding.Nearest) =>
        Of(QuantityText.Parse(value), unit, rounding);

    public static Weight Of(BigInteger value, string unit = "g", Rounding rounding = Rounding.Nearest) =>
        Of(Rational.Of(value), unit, rounding);

    /// <summary><c>Weight.parse</c> for a string: a trailing unit wins over <paramref name="defaultUnit"/>.</summary>
    public static Weight Parse(string value, string defaultUnit = "g", Rounding rounding = Rounding.Nearest) =>
        UnitTicks.SplitSuffix(value, Suffixes) is var (quantity, unit)
            ? Of(quantity, unit, rounding)
            : Of(value, defaultUnit, rounding);

    public string DecimalText(string unit = "g", int places = 8) =>
        DecimalRendering.Render(Ticks, MultiplierOf(unit, strip: false), places);

    public int CompareTo(Weight other) => Ticks.CompareTo(other.Ticks);

    public static bool operator <(Weight left, Weight right) => left.Ticks < right.Ticks;

    public static bool operator >(Weight left, Weight right) => left.Ticks > right.Ticks;

    public static bool operator <=(Weight left, Weight right) => left.Ticks <= right.Ticks;

    public static bool operator >=(Weight left, Weight right) => left.Ticks >= right.Ticks;

    /// <summary>Ticks per one <paramref name="unit"/>; throws for a unit the table does not hold.</summary>
    public static long MultiplierOf(string unit, bool strip) =>
        Multipliers.TryGetValue(PythonText.UnitKey(unit, strip), out var multiplier)
            ? multiplier
            : throw new ArgumentException($"unsupported weight unit: {unit}");
}
