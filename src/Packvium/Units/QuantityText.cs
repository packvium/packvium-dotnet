using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Packvium.Units;

/// <summary>
/// Reads an authored quantity -- <c>12</c>, <c>12.5</c>, <c>3/16</c>, <c>12 3/8</c> -- into an
/// exact rational, following <c>units._fraction</c> rule for rule, including the Unicode
/// digits and whitespace Python's <c>re</c> and <c>Decimal</c> accept.
/// </summary>
public static class QuantityText
{
    /// <summary>Shared with every engine: PHP cannot hold 10^19, so nobody accepts more digits.</summary>
    private const int MaxFractionalDigits = 18;

    public static Rational Parse(string value)
    {
        var text = PythonText.Strip(value).Replace(' ', ' ');
        var runes = new List<Rune>();
        foreach (var rune in text.EnumerateRunes())
        {
            runes.Add(rune);
        }
        if (TryParseMixed(runes, text, out var mixed))
        {
            return mixed;
        }
        if (TryParseSimpleFraction(runes, out var simple))
        {
            return simple;
        }
        if (FractionalDigitsOfPlainDecimal(runes) > MaxFractionalDigits)
        {
            throw new ArgumentException("decimal has more fractional digits than a tick can distinguish");
        }
        return PythonDecimal.ToRational(text);
    }

    // ([+-]?\d+)\s+(\d+)\s*/\s*(\d+)
    private static bool TryParseMixed(List<Rune> runes, string text, out Rational result)
    {
        result = default;
        var scanner = new Scanner(runes);
        scanner.TakeSign();
        if (!scanner.TakeDigits(out var whole) || !scanner.TakeSpaces(atLeastOne: true)
            || !scanner.TakeDigits(out var numerator))
        {
            return false;
        }
        scanner.TakeSpaces(atLeastOne: false);
        if (!scanner.TakeSlash())
        {
            return false;
        }
        scanner.TakeSpaces(atLeastOne: false);
        if (!scanner.TakeDigits(out var denominator) || !scanner.AtEnd)
        {
            return false;
        }
        // The sign comes from the written text, not from the value of the whole part, so
        // "-0 1/2" is minus one half rather than plus one half.
        var magnitude = whole * denominator + numerator;
        result = new Rational(text.StartsWith('-') ? -magnitude : magnitude, denominator);
        return true;
    }

    // ([+-]?\d+)\s*/\s*(\d+)
    private static bool TryParseSimpleFraction(List<Rune> runes, out Rational result)
    {
        result = default;
        var scanner = new Scanner(runes);
        var negative = scanner.TakeSign() == '-';
        if (!scanner.TakeDigits(out var numerator))
        {
            return false;
        }
        scanner.TakeSpaces(atLeastOne: false);
        if (!scanner.TakeSlash())
        {
            return false;
        }
        scanner.TakeSpaces(atLeastOne: false);
        if (!scanner.TakeDigits(out var denominator) || !scanner.AtEnd)
        {
            return false;
        }
        result = new Rational(negative ? -numerator : numerator, denominator);
        return true;
    }

    // [+-]?\d+(?:\.(\d+))? -- the count of the captured fractional digits, or 0 on no match.
    private static int FractionalDigitsOfPlainDecimal(List<Rune> runes)
    {
        var scanner = new Scanner(runes);
        scanner.TakeSign();
        if (!scanner.TakeDigits(out _))
        {
            return 0;
        }
        if (scanner.AtEnd)
        {
            return 0;
        }
        if (!scanner.TakeDot())
        {
            return 0;
        }
        var start = scanner.Position;
        if (!scanner.TakeDigits(out _) || !scanner.AtEnd)
        {
            return 0;
        }
        return scanner.Position - start;
    }

    private sealed class Scanner(List<Rune> runes)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position == runes.Count;

        /// <summary>Consumes an optional sign and returns it, or <c>'\0'</c>.</summary>
        public char TakeSign()
        {
            if (!AtEnd && (runes[Position].Value == '+' || runes[Position].Value == '-'))
            {
                return (char)runes[Position++].Value;
            }
            return '\0';
        }

        public bool TakeDigits(out BigInteger value)
        {
            value = BigInteger.Zero;
            var start = Position;
            while (!AtEnd && PythonText.DecimalValue(runes[Position]) >= 0)
            {
                value = value * 10 + PythonText.DecimalValue(runes[Position]);
                Position++;
            }
            return Position > start;
        }

        public bool TakeSpaces(bool atLeastOne)
        {
            var start = Position;
            while (!AtEnd && PythonText.IsSpace(runes[Position]))
            {
                Position++;
            }
            return !atLeastOne || Position > start;
        }

        public bool TakeSlash() => TakeLiteral('/');

        public bool TakeDot() => TakeLiteral('.');

        private bool TakeLiteral(char literal)
        {
            if (!AtEnd && runes[Position].Value == literal)
            {
                Position++;
                return true;
            }
            return false;
        }
    }
}
