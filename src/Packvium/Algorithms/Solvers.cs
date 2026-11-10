using System.Collections.Generic;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;

namespace Packvium.Algorithms;

/// <summary>The keys every built-in item ordering starts with: priority, route stop, then value under <c>maximum_value</c>.</summary>
public static class Ordering
{
    /// <summary>
    /// A later stop loads first so it ends up underneath; an item with no stop rides the whole
    /// route and goes to the bottom with the last stop.
    /// </summary>
    public static object[] Lead(ItemInstance instance, PackingConfig config)
    {
        var item = instance.Item;
        var stop = item.StopIndex is { } index ? -(double)index : double.NegativeInfinity;
        return config.Objective != "maximum_value"
            ? [-item.Priority, stop]
            : [-item.Priority, stop, -(item.Value ?? 0)];
    }

    public static SortKey Key(ItemInstance instance, PackingConfig config, params object[] strategy) =>
        SortKey.Of([.. Lead(instance, config), .. strategy]);
}

public sealed class ExtremePointSolver(IReadOnlyList<IPlacementConstraint>? constraints = null) : ISingleContainerSolver
{
    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];

    public string Name => "extreme_points";

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline) =>
        BeamPack.Pack(container, sequence, items, config, BeamPack.DefaultConstraints(config, constraints), stats, deadline);
}

/// <summary>Re-sorts by base area, volume and weight behind the shared lead keys, so it ignores the order it is handed.</summary>
public sealed class LayerSolver(IReadOnlyList<IPlacementConstraint>? constraints = null) : ISingleContainerSolver
{
    private readonly IReadOnlyList<IPlacementConstraint> constraints = constraints ?? [];

    public string Name => "layer";

    public bool OrderInsensitive => true;

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        var ordered = items.OrderBy(i => Ordering.Key(i, config, -i.Dimensions.BaseArea, -i.Dimensions.Volume,
            -i.Weight.Ticks, i.Id)).ToList();
        return BeamPack.Pack(container, sequence, ordered, config, BeamPack.DefaultConstraints(config, constraints),
            stats, deadline);
    }
}
