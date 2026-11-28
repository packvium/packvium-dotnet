using System.Globalization;
using System.Linq;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>
/// Python's <c>repr</c> and <c>str</c> of a value decoded from JSON, which a reference message
/// quotes: <c>None</c>, <c>True</c>, <c>'text'</c>, <c>[1, 'a']</c>, <c>{'k': 1.5}</c>. O(size of the value).
/// </summary>
public static class PythonRepr
{
    public static string Repr(JsonValue value) => value switch
    {
        JsonNull => "None",
        JsonBool boolean => boolean.Value ? "True" : "False",
        JsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        JsonFloat number => PythonFloat.Repr(number.Value),
        JsonString text => PythonText.Repr(text.Value),
        JsonArray array => $"[{string.Join(", ", array.Items.Select(Repr))}]",
        JsonObject record => $"{{{string.Join(", ", record.Members.Select(member => $"{PythonText.Repr(member.Key)}: {Repr(member.Value)}"))}}}",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary><c>str(value)</c>: a string is itself, anything else its <c>repr</c>.</summary>
    public static string Str(JsonValue value) => value is JsonString text ? text.Value : Repr(value);
}
