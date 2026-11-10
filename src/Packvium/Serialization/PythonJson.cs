using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Packvium.Units;
using System.Text;

namespace Packvium.Serialization;

/// <summary>
/// <c>json.dumps(value, ensure_ascii=False, ...)</c>: the encoder the reference engine writes its
/// CLI output with and the conformance harness canonicalizes with.
/// </summary>
public static class PythonJson
{
    /// <summary>
    /// <paramref name="indent"/> null gives the compact <c>(",", ":")</c> separators the canonical
    /// form uses; a number gives <c>indent=n</c> with Python's default <c>(",", ": ")</c>.
    /// </summary>
    public static string Dumps(JsonValue value, bool sortKeys, int? indent = null)
    {
        var builder = new StringBuilder();
        Write(builder, value, sortKeys, indent, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonValue value, bool sortKeys, int? indent, int depth)
    {
        switch (value)
        {
            case JsonNull:
                builder.Append("null");
                break;
            case JsonBool boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case JsonInteger integer:
                builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonFloat number:
                builder.Append(FloatLiteral(number.Value));
                break;
            case JsonString text:
                WriteString(builder, text.Value);
                break;
            case JsonArray array:
                WriteSequence(builder, '[', ']', array.Items, indent, depth,
                    item => Write(builder, item, sortKeys, indent, depth + 1));
                break;
            case JsonObject obj:
                var members = sortKeys ? obj.Members.OrderBy(m => m.Key, CodePointComparer.Instance) : obj.Members;
                WriteSequence(builder, '{', '}', members.ToList(), indent, depth, member =>
                {
                    WriteString(builder, member.Key);
                    builder.Append(indent is null ? ":" : ": ");
                    Write(builder, member.Value, sortKeys, indent, depth + 1);
                });
                break;
            default:
                throw new ArgumentException($"not a JSON value: {value.GetType().Name}");
        }
    }

    private static void WriteSequence<T>(StringBuilder builder, char open, char close, IReadOnlyList<T> items,
        int? indent, int depth, Action<T> writeItem)
    {
        builder.Append(open);
        if (items.Count == 0)
        {
            builder.Append(close);
            return;
        }
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            if (indent is { } width)
            {
                builder.Append('\n').Append(' ', width * (depth + 1));
            }
            writeItem(items[index]);
        }
        if (indent is { } closingWidth)
        {
            builder.Append('\n').Append(' ', closingWidth * depth);
        }
        builder.Append(close);
    }

    /// <summary><c>float.__repr__</c>, except the non-finite spellings <c>json</c> writes.</summary>
    private static string FloatLiteral(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "Infinity"
        : double.IsNegativeInfinity(value) ? "-Infinity"
        : PythonFloat.Repr(value);

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }
}
