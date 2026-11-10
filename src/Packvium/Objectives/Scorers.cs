using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Objectives;

/// <summary>An exact-integer objective vector, compared lexicographically; lower is better.</summary>
public sealed class ScoreVector : IComparable<ScoreVector>, IEquatable<ScoreVector>
{
    public ScoreVector(IEnumerable<long> keys) => Keys = [.. keys];

    public ImmutableArray<long> Keys { get; }

    public int CompareTo(ScoreVector? other)
    {
        if (other is null)
        {
            return 1;
        }
        for (var index = 0; index < Math.Min(Keys.Length, other.Keys.Length); index++)
        {
            var order = Keys[index].CompareTo(other.Keys[index]);
            if (order != 0)
            {
                return order;
            }
        }
        return Keys.Length.CompareTo(other.Keys.Length);
    }

    public bool Equals(ScoreVector? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ScoreVector other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys)
        {
            hash.Add(key);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(ScoreVector? left, ScoreVector? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ScoreVector? left, ScoreVector? right) => !(left == right);

    public static bool operator <(ScoreVector left, ScoreVector right) => left.CompareTo(right) < 0;

    public static bool operator >(ScoreVector left, ScoreVector right) => left.CompareTo(right) > 0;

    public static bool operator <=(ScoreVector left, ScoreVector right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ScoreVector left, ScoreVector right) => left.CompareTo(right) >= 0;
}

/// <summary>A Strategy that ranks finished solutions; see <c>docs/OBJECTIVE.md</c>.</summary>
public interface ISolutionScorer
{
    ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked);
}

/// <summary>An objective name this library does not implement, or one missing its required configuration.</summary>
public sealed class UnknownObjectiveException(string message) : ArgumentException(message);

/// <summary>
/// The canonical vector: unpacked count, container count, cost, unused volume ppm and stack
/// height ppm, each floored per container so every engine agrees bit for bit. O(c).
/// </summary>
public sealed class DefaultSolutionScorer : ISolutionScorer
{
    public const long Scale = 1_000_000;

    public ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked) =>
        new(Keys(containers, unpacked));

    internal static long[] Keys(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked)
    {
        long cost = 0;
        long unused = 0;
        long height = 0;
        foreach (var packed in containers)
        {
            var volume = packed.Container.InnerDimensions.Volume;
            cost = checked(cost + packed.Container.CostMinor);
            unused = checked(unused + (long)IntMath.FloorDiv((volume - packed.UsedVolume) * Scale, volume));
            height = checked(height + (long)((Int128)packed.MaxZTicks * Scale / packed.Container.InnerDimensions.Height.Ticks));
        }
        return [unpacked.Count, containers.Count, cost, unused, height];
    }
}

/// <summary>Cost ahead of container count; completeness still dominates.</summary>
public sealed class LowestCostSolutionScorer : ISolutionScorer
{
    public ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked)
    {
        var keys = DefaultSolutionScorer.Keys(containers, unpacked);
        return new([keys[0], keys[2], keys[1], keys[3], keys[4]]);
    }
}

/// <summary>Billable weight -- the greater of gross and dimensional -- ahead of container count.</summary>
public class ShippingCostSolutionScorer(PackingConfig config) : ISolutionScorer
{
    protected PackingConfig Config { get; } = config;

    public static ShippingCostSolutionScorer FromConfig(PackingConfig? config) =>
        config?.DimensionalWeightDivisor is null
            ? throw new UnknownObjectiveException(
                "the shipping_cost objective requires configuration.dimensional_weight_divisor")
            : new ShippingCostSolutionScorer(config);

    public virtual ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers,
        IReadOnlyList<UnpackedItem> unpacked)
    {
        var keys = DefaultSolutionScorer.Keys(containers, unpacked);
        var billable = containers.Aggregate(0L,
            (total, packed) => checked(total + Billing.BilledTicks(packed.Container, packed.GrossWeight.Ticks, Config)));
        return new([keys[0], billable, keys[1], keys[3], keys[4]]);
    }
}

/// <summary>
/// The money a shipment costs under each container's rate table. A billed weight past the last
/// bracket scores the unpriceable sentinel so a priceable alternative wins.
/// </summary>
public sealed class LandedCostSolutionScorer(PackingConfig config) : ShippingCostSolutionScorer(config)
{
    public static new LandedCostSolutionScorer FromConfig(PackingConfig? config) =>
        config?.DimensionalWeightDivisor is null
            ? throw new UnknownObjectiveException(
                "the lowest_landed_cost objective requires configuration.dimensional_weight_divisor")
            : new LandedCostSolutionScorer(config);

    public override ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers,
        IReadOnlyList<UnpackedItem> unpacked)
    {
        var keys = DefaultSolutionScorer.Keys(containers, unpacked);
        long landed = 0;
        foreach (var packed in containers)
        {
            var table = packed.Container.RateTable ?? throw new UnknownObjectiveException(
                "the lowest_landed_cost objective requires a rate_table on every container; "
                + $"'{packed.Container.Id}' has none");
            var billed = Billing.BilledTicks(packed.Container, packed.GrossWeight.Ticks, Config);
            if (table.ChargeMinorOrNull(Billing.Grams(billed)) is not { } charge)
            {
                landed = Billing.UnpriceableMinor;
                break;
            }
            landed = checked(landed + charge);
        }
        return new([keys[0], landed, keys[1], keys[3], keys[4]]);
    }
}

/// <summary>Raw achieved stack height ahead of container count, for a container whose height is not a real limit.</summary>
public sealed class OpenDimensionSolutionScorer : ISolutionScorer
{
    public ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked)
    {
        var keys = DefaultSolutionScorer.Keys(containers, unpacked);
        var height = containers.Aggregate(0L, (total, packed) => checked(total + packed.MaxZTicks));
        return new([keys[0], height, keys[1], keys[2], keys[3]]);
    }
}

/// <summary>The declared value left unpacked ahead of container count.</summary>
public sealed class MaximumValueSolutionScorer : ISolutionScorer
{
    public ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked)
    {
        var keys = DefaultSolutionScorer.Keys(containers, unpacked);
        var forgone = unpacked.Aggregate(0L, (total, item) => checked(total + (item.Instance.Item.Value ?? 0)));
        return new([keys[0], forgone, keys[1], keys[2], keys[3]]);
    }
}

/// <summary>The Registry of named objectives a request may select.</summary>
public static class ObjectiveScorers
{
    private static readonly SortedDictionary<string, Func<PackingConfig?, ISolutionScorer>> Factories =
        new(StringComparer.Ordinal)
        {
            ["default"] = _ => new DefaultSolutionScorer(),
            ["lowest_cost"] = _ => new LowestCostSolutionScorer(),
            ["shipping_cost"] = ShippingCostSolutionScorer.FromConfig,
            ["lowest_landed_cost"] = LandedCostSolutionScorer.FromConfig,
            ["open_dimension_height"] = _ => new OpenDimensionSolutionScorer(),
            ["maximum_value"] = _ => new MaximumValueSolutionScorer(),
        };

    public static ISolutionScorer Resolve(string name, PackingConfig? config = null) =>
        Factories.TryGetValue(name, out var factory)
            ? factory(config)
            : throw new UnknownObjectiveException(
                $"unknown objective '{name}'; expected one of [{string.Join(", ", Factories.Keys.Select(key => $"'{key}'"))}]");

    /// <summary>
    /// The first container in a finished answer its own rate table cannot price, as
    /// (container id, billed grams, last bracket); null for every other objective.
    /// </summary>
    public static (string ContainerId, long Grams, long Bound)? UnpriceableContainer(
        IReadOnlyList<PackedContainer> containers, PackingConfig config)
    {
        if (config.Objective != "lowest_landed_cost" || config.DimensionalWeightDivisor is null)
        {
            return null;
        }
        foreach (var packed in containers)
        {
            var grams = Billing.Grams(Billing.BilledTicks(packed.Container, packed.GrossWeight.Ticks, config));
            if (packed.Container.RateTable is not { } table)
            {
                return (packed.Container.Id, grams, 0);
            }
            if (table.ChargeMinorOrNull(grams) is null)
            {
                return (packed.Container.Id, grams, table.WeightBracketsG[^1]);
            }
        }
        return null;
    }
}
