using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Packvium.Units;

/// <summary>
/// The handful of Python <c>str</c> rules the reference engine's parsers depend on. .NET's own
/// definitions differ at the edges (<see cref="char.IsWhiteSpace(char)"/> excludes U+001C..U+001F,
/// which <c>str.isspace</c> includes), and an edge where two engines disagree is a request one
/// accepts and the other refuses.
/// </summary>
internal static class PythonText
{
    /// <summary><c>str.isspace</c>, which is also what <c>\s</c> and <c>str.strip()</c> use.</summary>
    public static bool IsSpace(Rune rune) => rune.Value switch
    {
        >= 0x09 and <= 0x0D => true,
        >= 0x1C and <= 0x20 => true,
        0x85 or 0xA0 or 0x1680 => true,
        >= 0x2000 and <= 0x200A => true,
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 => true,
        _ => false,
    };

    public static bool IsSpace(char value) => IsSpace(new Rune(value));

    /// <summary><c>str.strip()</c> with no argument.</summary>
    public static string Strip(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }
        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary>
    /// The decimal value of a Unicode <c>Nd</c> digit, or -1. Python's <c>\d</c>, <c>int()</c> and
    /// <c>Decimal()</c> all accept every decimal digit, not only ASCII ones.
    /// </summary>
    public static int DecimalValue(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber
            ? (int)Rune.GetNumericValue(rune)
            : -1;

    /// <summary>
    /// The lookup key <c>unit.lower().strip()</c> produces, for the purpose of finding it in an
    /// ASCII unit table. Only ASCII capitals and the Kelvin sign (U+212A) lower to an ASCII
    /// letter; every other character lowers to something no table contains, so it is kept as
    /// it is and the lookup misses exactly as it does in Python.
    /// </summary>
    public static string UnitKey(string unit, bool strip)
    {
        var builder = new StringBuilder(unit.Length);
        foreach (var character in unit)
        {
            builder.Append(character switch
            {
                >= 'A' and <= 'Z' => (char)(character + ('a' - 'A')),
                'K' => 'k',
                _ => character,
            });
        }
        var lowered = builder.ToString();
        return strip ? Strip(lowered) : lowered;
    }

    /// <summary>
    /// Whether <paramref name="text"/> matches the ASCII <paramref name="pattern"/> under
    /// <c>re.IGNORECASE</c> for a <c>str</c> pattern, which folds more than ASCII case: the
    /// Kelvin sign matches <c>k</c>, the long s matches <c>s</c>, and both dotted and dotless
    /// i match <c>i</c>.
    /// </summary>
    public static bool EqualsIgnoreCase(string text, string pattern)
    {
        if (text.Length != pattern.Length)
        {
            return false;
        }
        for (var index = 0; index < text.Length; index++)
        {
            if (!CharMatchesIgnoreCase(text[index], pattern[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <c>repr(str)</c>: single quotes unless the text holds a single quote and no double one,
    /// with backslash, the chosen quote, the three named controls and every non-printable code
    /// point escaped. Rule messages quote tags this way, and a message is part of a result.
    /// </summary>
    public static string Repr(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == quote || value == '\\')
            {
                builder.Append('\\').Append((char)value);
            }
            else if (value is '\t' or '\n' or '\r')
            {
                builder.Append(value switch { '\t' => "\\t", '\n' => "\\n", _ => "\\r" });
            }
            else if (value < 0x20 || value == 0x7F || (value >= 0x80 && !IsPrintable(rune)))
            {
                builder.Append(value switch
                {
                    < 0x100 => $"\\x{value:x2}",
                    < 0x10000 => $"\\u{value:x4}",
                    _ => $"\\U{value:x8}",
                });
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }
        return builder.Append(quote).ToString();
    }

    /// <summary><c>repr(list[str])</c>: how a reference message prints a sorted list of names.</summary>
    public static string ReprList(IEnumerable<string> texts) => $"[{string.Join(", ", texts.Select(Repr))}]";

    /// <summary><c>str.isprintable</c> for one code point: not a control, format, surrogate, private, unassigned or separator other than space.</summary>
    private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator => false,
        UnicodeCategory.SpaceSeparator => rune.Value == ' ',
        _ => true,
    };

    private static bool CharMatchesIgnoreCase(char candidate, char lowerAscii)
    {
        if (candidate == lowerAscii || candidate == lowerAscii - ('a' - 'A'))
        {
            return true;
        }
        return (candidate, lowerAscii) switch
        {
            ('K', 'k') => true,
            ('ſ', 's') => true,
            ('İ', 'i') or ('ı', 'i') => true,
            _ => false,
        };
    }
}
