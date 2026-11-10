using System;
using System.Numerics;

namespace Packvium.Units;

/// <summary>The conversion and suffix rules <see cref="Length"/> and <see cref="Weight"/> share.</summary>
internal static class UnitTicks
{
    /// <summary>
    /// <c>value * multiplier</c>, rounded to a tick. Ticks are 64-bit, as in PHP: a quantity
    /// past that range is refused rather than carried, so every engine bounded by a native
    /// integer answers the same requests.
    /// </summary>
    public static long Scale(Rational value, long multiplier, Rounding rounding, string quantity)
    {
        var ticks = value.Multiply(multiplier).Round(rounding);
        if (ticks.Sign < 0)
        {
            throw new ArgumentException($"{quantity} cannot be negative");
        }
        if (ticks > long.MaxValue)
        {
            throw new OverflowException($"{quantity} exceeds the 64-bit tick range");
        }
        return (long)ticks;
    }

    /// <summary>
    /// The reference parser's <c>re.fullmatch(r"(.+?)\s*(suffix|...)", text.strip(), re.I)</c>:
    /// the shortest non-empty prefix whose remainder, after whitespace, is one of
    /// <paramref name="suffixes"/>. Returns the stripped prefix and the suffix as written.
    /// </summary>
    public static (string Quantity, string Unit)? SplitSuffix(string value, string[] suffixes)
    {
        var text = PythonText.Strip(value);
        for (var end = 1; end < text.Length; end++)
        {
            if (text[end - 1] == '\n')
            {
                // `.` does not match a newline, so no longer prefix can match either.
                return null;
            }
            var rest = text.AsSpan(end);
            var whitespace = 0;
            while (whitespace < rest.Length && PythonText.IsSpace(rest[whitespace]))
            {
                whitespace++;
            }
            var unit = rest[whitespace..].ToString();
            foreach (var suffix in suffixes)
            {
                if (PythonText.EqualsIgnoreCase(unit, suffix))
                {
                    return (PythonText.Strip(text[..end]), unit);
                }
            }
        }
        return null;
    }
}
