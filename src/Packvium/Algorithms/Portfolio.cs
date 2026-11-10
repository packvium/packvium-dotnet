using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Algorithms;

/// <summary>One start's finished multi-container answer, before ranking.</summary>
public sealed record RawSolution(
    string SolverName,
    ImmutableArray<PackedContainer> Containers,
    ImmutableArray<UnpackedItem> Unpacked,
    SearchStats Stats,
    bool TimeLimitReached = false,
    bool EffortLimitReached = false,
    bool Exhaustive = false);

public sealed record PortfolioRun(ImmutableArray<RawSolution> Solutions, ImmutableArray<StartRecord> Starts);

/// <summary>A Strategy that orders items for one portfolio start.</summary>
public interface IItemOrderStrategy
{
    string Name { get; }

    IReadOnlyList<ItemInstance> Order(IReadOnlyList<ItemInstance> items, long seed);
}

/// <summary>A Strategy that ranks one container's trial among the templates offered for the next container.</summary>
public interface IContainerSelector
{
    SortKey Score(Container container, SingleContainerSolution solution);
}

/// <summary>Most items, then cheapest, then tightest, then id.</summary>
public sealed class DefaultContainerSelector : IContainerSelector
{
    public SortKey Score(Container container, SingleContainerSolution solution)
    {
        var state = solution.State;
        return SortKey.Of(-state.PlacementCount, container.CostMinor,
            container.InnerDimensions.Volume - state.UsedVolumeTicks, container.Id);
    }
}

/// <summary>
/// Seeded linear congruential generator. Only the high bits are published, and draws past the
/// largest whole multiple of the bound are rejected, so a shuffle carries no modulo bias.
/// </summary>
public sealed class DeterministicRandom(long seed)
{
    private const long Modulus = 1L << 31;
    private const long Multiplier = 1_103_515_245;
    private const long Increment = 12_345;
    private const long DrawLimit = 1L << 32;

    private long state = (seed % Modulus + Modulus) % Modulus;

    public long NextInt(long upper)
    {
        if (upper <= 1)
        {
            return 0;
        }
        var bound = DrawLimit - DrawLimit % upper;
        while (true)
        {
            var draw = Bits() * 65536 + Bits();
            if (draw < bound)
            {
                return draw % upper;
            }
        }
    }

    public List<T> Shuffled<T>(IEnumerable<T> values)
    {
        var output = values.ToList();
        for (var index = output.Count - 1; index > 0; index--)
        {
            var other = (int)NextInt(index + 1);
            (output[index], output[other]) = (output[other], output[index]);
        }
        return output;
    }

    private long Bits()
    {
        state = (Multiplier * state + Increment) % Modulus;
        return state >> 15;
    }
}
