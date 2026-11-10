using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Packing;

/// <summary>One level of a nested pack: its name, the containers it offers, and its own configuration.</summary>
public sealed record PackingLevel(string Name, ImmutableArray<Container> Containers, PackingConfig? Config = null);

/// <summary>One result per level, in the order the levels were given.</summary>
public sealed record NestedPackingResult(ImmutableArray<PackingResult> Levels);

/// <summary>
/// Packs level by level: every container a level opens becomes an item, at its outer size and
/// gross weight, for the next. Stops at the first level that cannot place everything.
/// </summary>
public static class NestedPacker
{
    public static NestedPackingResult Pack(IEnumerable<Item> items, IEnumerable<PackingLevel> levels)
    {
        var current = items.ToList();
        var results = ImmutableArray.CreateBuilder<PackingResult>();
        foreach (var level in levels)
        {
            var result = new Packer(level.Config ?? PackingConfig.Balanced()).Pack(current, level.Containers);
            results.Add(result);
            if (!result.Complete)
            {
                break;
            }
            current = [.. result.Containers.Select(container => container.AsItem())];
        }
        return new NestedPackingResult(results.ToImmutable());
    }
}
