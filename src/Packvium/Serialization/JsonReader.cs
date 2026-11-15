using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Packvium.Serialization;

/// <summary>Parses JSON text into <see cref="JsonValue"/> with Python's number and duplicate-key rules.</summary>
public static class JsonReader
{
    private static readonly JsonReaderOptions Options = new() { MaxDepth = 512 };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static JsonValue Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    public static JsonValue Parse(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, Options);
        if (!reader.Read())
        {
            throw new JsonException("Expecting value: empty document");
        }
        var value = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Extra data after the top-level value");
        }
        return value;
    }

    private static JsonValue ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.StartObject => ReadObject(ref reader),
        JsonTokenType.StartArray => ReadArray(ref reader),
        JsonTokenType.String => new JsonString(ReadString(ref reader)),
        JsonTokenType.Number => ReadNumber(ref reader),
        JsonTokenType.True => new JsonBool(true),
        JsonTokenType.False => new JsonBool(false),
        JsonTokenType.Null => JsonNull.Instance,
        _ => throw new JsonException($"unexpected token {reader.TokenType}"),
    };

    private static JsonObject ReadObject(ref Utf8JsonReader reader)
    {
        var members = new List<KeyValuePair<string, JsonValue>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = ReadString(ref reader);
            reader.Read();
            members.Add(new KeyValuePair<string, JsonValue>(key, ReadValue(ref reader)));
        }
        return new JsonObject(members);
    }

    private static JsonArray ReadArray(ref Utf8JsonReader reader)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            items.Add(ReadValue(ref reader));
        }
        return new JsonArray(items.ToImmutable());
    }

    /// <summary>
    /// A string or key as Python's <c>json</c> reads it. An escaped lone surrogate
    /// (<c>"\ud800"</c>) is kept, as Python keeps it, so the writer that cannot spell it is
    /// the one that refuses it; <see cref="Utf8JsonReader.GetString"/> would throw instead.
    /// </summary>
    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
        {
            return reader.GetString()!;
        }
        var raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
        return Unescape(raw);
    }

    /// <summary>
    /// Decodes the raw bytes between a string's quotes. The reader has already checked the escape
    /// syntax; the UTF-8 between escapes is decoded strictly, as <see cref="Utf8JsonReader.GetString"/> does.
    /// </summary>
    private static string Unescape(byte[] raw)
    {
        var builder = new StringBuilder(raw.Length);
        var start = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != (byte)'\\')
            {
                continue;
            }
            builder.Append(StrictUtf8.GetString(raw, start, index - start));
            var marker = (char)raw[++index];
            if (marker == 'u')
            {
                builder.Append((char)int.Parse(Encoding.ASCII.GetString(raw, index + 1, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture));
                index += 4;
            }
            else
            {
                builder.Append(marker switch
                {
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => marker,
                });
            }
            start = index + 1;
        }
        return builder.Append(StrictUtf8.GetString(raw, start, raw.Length - start)).ToString();
    }

    /// <summary>An integer literal stays an exact integer; a fraction or exponent makes a double.</summary>
    private static JsonValue ReadNumber(ref Utf8JsonReader reader)
    {
        var raw = Encoding.ASCII.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
        if (raw.AsSpan().IndexOfAny(".eE") < 0)
        {
            return new JsonInteger(BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }
        return new JsonFloat(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }
}
