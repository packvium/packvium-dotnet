using System.Globalization;
using System.Numerics;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// <c>Fraction(text)</c> by the grammar of CPython 3.11's <c>fractions._RATIONAL_FORMAT</c>:
/// optional whitespace and sign, then <c>n/d</c>, or a decimal with an optional exponent; digit
/// groups may be split by single underscores. Only ASCII digits are read. O(length²) for the
/// digit conversion.
/// </summary>
internal static class Fractions
{
    /// <summary>
    /// The largest decimal exponent read. Python would build any power of ten; past this one the
    /// value is far outside every measure the contract describes, so it is refused instead.
    /// </summary>
    private const int MaxExponent = 10_000;

    public static Ratio Parse(string text)
    {
        var reader = new Cursor(text);
        reader.SkipSpace();
        var negative = reader.Peek() == '-';
        if (negative || reader.Peek() == '+')
        {
            reader.Advance();
        }
        if (!reader.AtDigit() && !(reader.Peek() == '.' && reader.AtDigit(1)))
        {
            throw Invalid(text);
        }
        var numerator = reader.DigitGroups() is { } whole ? Integer(whole) : BigInteger.Zero;
        var denominator = BigInteger.One;
        if (reader.Peek() == '/')
        {
            reader.Advance();
            denominator = Integer(reader.DigitGroups() ?? throw Invalid(text));
        }
        else
        {
            if (reader.Peek() == '.')
            {
                reader.Advance();
                if (reader.DigitGroups() is { } decimals)
                {
                    var digits = decimals.Replace("_", string.Empty, System.StringComparison.Ordinal);
                    var scale = BigInteger.Pow(10, digits.Length);
                    numerator = numerator * scale + Integer(digits);
                    denominator = scale;
                }
            }
            if (reader.Peek() is 'e' or 'E')
            {
                reader.Advance();
                var power = Exponent(reader, text);
                if (power >= 0)
                {
                    numerator *= BigInteger.Pow(10, power);
                }
                else
                {
                    denominator *= BigInteger.Pow(10, -power);
                }
            }
        }
        reader.SkipSpace();
        if (!reader.AtEnd)
        {
            throw Invalid(text);
        }
        return Ratio.Of(negative ? -numerator : numerator, denominator);
    }

    private static int Exponent(Cursor reader, string text)
    {
        var negative = reader.Peek() == '-';
        if (negative || reader.Peek() == '+')
        {
            reader.Advance();
        }
        var digits = (reader.DigitGroups() ?? throw Invalid(text)).Replace("_", string.Empty, System.StringComparison.Ordinal).TrimStart('0');
        if (digits.Length > 5 || (digits.Length > 0 && int.Parse(digits, CultureInfo.InvariantCulture) > MaxExponent))
        {
            throw MalformedResultException.ShapeBreak("an exponent beyond what this validator computes");
        }
        var power = digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture);
        return negative ? -power : power;
    }

    private static BigInteger Integer(string digits) =>
        BigInteger.Parse(digits.Replace("_", string.Empty, System.StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture);

    private static MalformedResultException Invalid(string text) =>
        MalformedResultException.ShapeBreak($"Invalid literal for Fraction: {PythonText.Repr(text)}");

    /// <summary>A read position in the text being parsed.</summary>
    private sealed class Cursor(string text)
    {
        private int position;

        public bool AtEnd => position >= text.Length;

        public char Peek(int ahead = 0) => position + ahead < text.Length ? text[position + ahead] : '\0';

        public bool AtDigit(int ahead = 0) => Peek(ahead) is >= '0' and <= '9';

        public void Advance() => position++;

        public void SkipSpace()
        {
            while (!AtEnd && PythonText.IsSpace(text[position]))
            {
                position++;
            }
        }

        /// <summary><c>\d+(_\d+)*</c> at the cursor, or null with the cursor unmoved.</summary>
        public string? DigitGroups()
        {
            var start = position;
            while (AtDigit() || (Peek() == '_' && position > start && Peek(-1) != '_' && AtDigit(1)))
            {
                position++;
            }
            return position > start ? text[start..position] : null;
        }
    }
}
