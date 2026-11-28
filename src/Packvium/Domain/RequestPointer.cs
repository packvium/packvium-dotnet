using System;
using System.Globalization;
using System.Linq;

namespace Packvium.Domain;

/// <summary>RFC 6901 pointers into a request: each part prefixed by <c>/</c>, with <c>~</c> and <c>/</c> in keys escaped.</summary>
internal static class RequestPointer
{
    public static string Of(params object[] parts) => string.Concat(parts.Select(Part));

    private static string Part(object part)
    {
        var text = Convert.ToString(part, CultureInfo.InvariantCulture)!;
        return "/" + (text.AsSpan().IndexOfAny('~', '/') < 0
            ? text
            : text.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
    }
}
