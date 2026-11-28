using System.Collections.Generic;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// An insertion-ordered map keyed the way a Python dict is: by <see cref="Json.HashKey"/>, so
/// <c>1</c> and <c>True</c> are one key, and assigning an existing key replaces its value in
/// place. Keys keep the JSON value they were first stored under, for printing. O(1) per operation.
/// </summary>
internal sealed class Ordered<T>
{
    private readonly List<(JsonValue Key, T Value)> entries = [];
    private readonly Dictionary<string, int> index = new(System.StringComparer.Ordinal);

    public int Count => entries.Count;

    public IEnumerable<(JsonValue Key, T Value)> Entries => entries;

    public IEnumerable<JsonValue> Keys => entries.Select(entry => entry.Key);

    public IEnumerable<T> Values => entries.Select(entry => entry.Value);

    public void Set(JsonValue key, T value)
    {
        var hashed = Json.HashKey(key);
        if (index.TryGetValue(hashed, out var position))
        {
            entries[position] = (entries[position].Key, value);
            return;
        }
        index[hashed] = entries.Count;
        entries.Add((key, value));
    }

    /// <summary><c>d.get(key)</c>; hashing the key first, as Python does even for an empty dict.</summary>
    public bool TryGet(JsonValue key, out T value)
    {
        if (Position(key) is { } position)
        {
            value = entries[position].Value;
            return true;
        }
        value = default!;
        return false;
    }

    public T? GetOrDefault(JsonValue key) => TryGet(key, out var value) ? value : default;

    public bool Has(JsonValue key) => Position(key) is not null;

    /// <summary>The entry's place in insertion order, or null when absent.</summary>
    public int? Position(JsonValue key) => index.TryGetValue(Json.HashKey(key), out var position) ? position : null;

    public T ValueAt(int position) => entries[position].Value;
}
