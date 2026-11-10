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
        JsonTokenType.String => new JsonString(reader.GetString()!),
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
            var key = reader.GetString()!;
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
