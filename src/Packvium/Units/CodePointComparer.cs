using System.Collections.Generic;

namespace Packvium.Units;

/// <summary>
/// Orders strings the way Python compares <c>str</c>: by Unicode code point. Ordinal comparison
/// in .NET compares UTF-16 code units, which puts every character above U+FFFF before
/// U+E000..U+FFFF -- the same divergence that once split JavaScript from the other engines.
/// </summary>
public sealed class CodePointComparer : IComparer<string>
{
    public static CodePointComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }
        var left = x.EnumerateRunes();
        var right = y.EnumerateRunes();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft.CompareTo(hasRight);
            }
            var order = left.Current.Value.CompareTo(right.Current.Value);
            if (order != 0)
            {
                return order;
            }
        }
    }
}
