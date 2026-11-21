using System.Globalization;
using System.Text;

namespace Packvium.Units;

/// <summary>
/// A string's RFC 8785 spelling: <c>"</c>, <c>\</c> and U+0000..U+001F escaped (<c>\b \t \n \f \r</c>
/// by name, the rest as lowercase <c>\u00xx</c>), everything else written as itself. Kept at the
/// lowest layer so a domain refusal can quote a value exactly as the canonical writer does.
/// </summary>
public static class CanonicalText
{
    /// <summary>The quoted spelling, or null when the string carries a lone UTF-16 surrogate, which has none. O(n).</summary>
    public static string? Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return null;
                }
                builder.Append(character).Append(text[++index]);
                continue;
            }
            builder.Append(Escape(character));
        }
        return builder.Append('"').ToString();
    }

    /// <summary>How a refusal quotes a string: its canonical JSON, or the reference's phrase when it has none.</summary>
    public static string Spelling(string text) => Quote(text) ?? "an unspellable value";

    private static string Escape(char character) => character switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\t' => "\\t",
        '\n' => "\\n",
        '\f' => "\\f",
        '\r' => "\\r",
        < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
        _ => character.ToString(),
    };
}
