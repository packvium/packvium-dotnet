using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>A public request field this engine has not implemented, refused rather than silently ignored.</summary>
public sealed class UnsupportedFeatureException(string message) : ArgumentException(message)
{
    public const string Code = "unsupported_feature";
}

/// <summary>
/// The reference's <c>reject_unsupported</c>: public request fields no engine may accept yet,
/// by the scope they appear in. A field a caller can set and the solver ignores is worse than
/// a refusal, so the guard runs before anything else is read.
/// </summary>
public static class UnsupportedFields
{
    /// <summary>
    /// Scope to field names, in the reference's scope order. <c>pallet_overhang_limit</c> was
    /// reserved at the 1.1.0 contract freeze and is refused by every engine until one
    /// implements it.
    /// </summary>
    public static ImmutableArray<(string Scope, ImmutableArray<string> Fields)> ByScope { get; } =
    [
        ("request", []),
        ("configuration", []),
        ("item", []),
        ("container", ["pallet_overhang_limit"]),
    ];

    /// <summary>Throws naming every unsupported field the request sets, each once, in code-point order. O(n f).</summary>
    public static void Reject(JsonObject request)
    {
        var found = new SortedSet<string>(CodePointComparer.Instance);
        found.UnionWith(Fields("request").Where(field => request.Get(field) is not null));
        if (request.Get("configuration") is JsonObject configuration)
        {
            found.UnionWith(Fields("configuration").Where(field => configuration.Get(field) is not null)
                .Select(field => $"configuration.{field}"));
        }
        foreach (var (scope, collection) in (ReadOnlySpan<(string, string)>)[("item", "items"), ("container", "containers")])
        {
            var entries = request.Get(collection) is JsonArray array ? array.Items.OfType<JsonObject>() : [];
            found.UnionWith(entries.SelectMany(entry => Fields(scope).Where(field => entry.Get(field) is not null))
                .Select(field => $"{scope}.{field}"));
        }
        if (found.Count == 0)
        {
            return;
        }
        throw new UnsupportedFeatureException(
            $"unsupported_feature: the .NET engine does not yet implement {string.Join(", ", found)}; "
            + "the request was rejected instead of silently ignoring public fields");
    }

    private static ImmutableArray<string> Fields(string scope) => ByScope.First(entry => entry.Scope == scope).Fields;
}
