using System.Collections.Generic;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// A <c>frozenset</c> of JSON values, as the reference holds a tag list: members are told apart by
/// <see cref="Json.HashKey"/>, so <c>1</c> and <c>True</c> are one tag, and the first spelling stays.
/// O(1) membership.
/// </summary>
internal sealed class TagSet
{
    private readonly Dictionary<string, JsonValue> members;

    private TagSet(Dictionary<string, JsonValue> members)
    {
        this.members = members;
    }

    public static TagSet Empty { get; } = new([]);

    public bool IsEmpty => members.Count == 0;

    /// <summary><c>frozenset(value)</c>: what iterating the value yields; an unhashable member is Python's <c>TypeError</c>.</summary>
    public static TagSet Of(JsonValue value, string what)
    {
        var members = new Dictionary<string, JsonValue>(System.StringComparer.Ordinal);
        foreach (var member in Json.Items(value, what))
        {
            members.TryAdd(Json.HashKey(member), member);
        }
        return new TagSet(members);
    }

    /// <summary><c>value in tags</c>; an unhashable value is Python's <c>TypeError</c>.</summary>
    public bool Contains(JsonValue value) => members.ContainsKey(Json.HashKey(value));

    public bool Contains(string tag) => Contains(new JsonString(tag));

    /// <summary>Whether <c>self &amp; other</c> is non-empty.</summary>
    public bool Overlaps(TagSet other) => members.Keys.Any(other.members.ContainsKey);

    /// <summary><c>sorted(tags)</c>, by Python's ordering of the members.</summary>
    public IEnumerable<JsonValue> Sorted() => Json.Sorted(members.Values);
}
