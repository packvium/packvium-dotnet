using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Packvium.Serialization;

namespace Packvium.Commerce;

/// <summary>
/// A caller bug: the document or request is not well formed. Distinct from a rejection, which
/// the commercial model is entitled to make and which is an ordinary result document.
/// </summary>
public sealed class CommerceInputException(string message) : Exception(message);

/// <summary>
/// The carrier of a structured rejection from where it is detected to the API boundary, where
/// it becomes <c>{"status": "rejected", ...}</c>. Never escapes this namespace.
/// </summary>
internal sealed class RejectionException(string code, ImmutableArray<KeyValuePair<string, JsonValue>> fields)
    : Exception(code)
{
    public string Code { get; } = code;

    public ImmutableArray<KeyValuePair<string, JsonValue>> Fields { get; } = fields;
}
