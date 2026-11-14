using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Algorithms;

/// <summary>
/// A regular lattice for one geometrically fungible item profile, computed in closed form:
/// O(r) to choose the rotation, then O(n) to place (or O(1) on the compact path). Every rule a
/// regular stack could break is folded into the layer count and capacity; anything the lattice
/// cannot honour falls back to <see cref="ExtremePointSolver"/>.
/// </summary>
public sealed class GridSolver : ISingleContainerSolver
{
    public string Name => "grid";

    public bool OrderInsensitive => true;

    public static bool Supports(IReadOnlyList<ItemInstance> items)
    {
        if (items.Count == 0)
        {
            return false;
        }
        var prototype = items[0].Item;
        var profile = LatticeProfile(prototype);
        var previous = prototype;
        foreach (var instance in items)
        {
            // Quantities share one immutable Item, so only a different object can bring a
            // different profile; one mismatch decides, without building the rest.
            var item = instance.Item;
            if (!ReferenceEquals(item, prototype) && !ReferenceEquals(item, previous)
                && !string.Equals(LatticeProfile(item), profile, StringComparison.Ordinal))
            {
                return false;
            }
            previous = item;
        }
        return true;
    }

    public SingleContainerSolution PackOne(Container container, int sequence, IReadOnlyList<ItemInstance> items,
        PackingConfig config, SearchStats stats, Deadline deadline)
    {
        if (!LatticeApplies(container, items))
        {
            return new ExtremePointSolver().PackOne(container, sequence, items, config, stats, deadline);
        }
        var prototype = items[0].Item;
        var state = new ContainerState(container, sequence);
        var layout = BestLayout(container, prototype, config);
        var total = (int)Math.Min(layout.Capacity, items.Count);
        if (prototype.Group is not null && total < items.Count)
        {
            return new SingleContainerSolution(state, [.. items], DominantLattice: true);
        }
        var clearance = config.Clearance.Ticks;
        var nestingTicks = prototype.NestingHeight?.Ticks ?? 0;
        if (total > 0 && !config.RequirePlacementCoordinates && nestingTicks == 0
            && items.Take(total).Select(item => item.Item.Id).Distinct().Count() == 1)
        {
            var summary = new LatticeSummary(prototype.Id, layout.Rotation, layout.Physical, layout.Envelope, layout.Nx,
                layout.Ny, layout.LayerStep, clearance, total, prototype.Weight.Ticks);
            state.AddLattice(summary, [.. items.Take(total)]);
            stats.SearchNodesExpanded++;
            stats.CandidatePointsConsidered += total;
            stats.CandidatesEvaluated += total;
            stats.PlacementsAttempted += total;
            return new SingleContainerSolution(state, [.. items.Skip(total)]);
        }
        var placed = PlaceLattice(state, items, total, layout, clearance, stats, deadline);
        if (prototype.Group is not null && placed < items.Count)
        {
            return new SingleContainerSolution(new ContainerState(container, sequence), [.. items], false,
                deadline.Expired, DominantLattice: true);
        }
        return new SingleContainerSolution(state, [.. items.Skip(placed)], false, placed < total && deadline.Expired,
            DominantLattice: true);
    }

    /// <summary>
    /// Everything about an item that determines its behaviour in a regular lattice, as one
    /// injective string: two declared types are fungible when this matches.
    /// </summary>
    internal static string LatticeProfile(Item item)
    {
        var footprints = item.Dimensions.UniqueRotations(item.AllowedRotations)
            .Select(pair => (pair.Dimensions.Length.Ticks, pair.Dimensions.Width.Ticks, pair.Dimensions.Height.Ticks))
            .Distinct().Order();
        var builder = new StringBuilder();
        builder.AppendJoin(';', footprints.Select(f => $"{f.Item1},{f.Item2},{f.Item3}"));
        void Field(object? value) => builder.Append('|').Append(value switch
        {
            null => "~",
            string text => "s" + text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text,
            IEnumerable<string> set => string.Join(",", set.Order(StringComparer.Ordinal).Select(tag =>
                tag.Length.ToString(CultureInfo.InvariantCulture) + ":" + tag)),
            bool flag => flag ? "T" : "F",
            long number => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("unexpected lattice profile field"),
        });
        Field(item.Weight.Ticks);
        Field(item.Stackable);
        Field(item.MustBeOnFloor);
        Field(item.MaxTopLoad?.Ticks);
        Field(item.Tags);
        Field(item.IncompatibleTags);
        Field(item.EligibleContainerTags);
        Field(item.GroundContactRule);
        // Nesting overlap is valid only between instances of one declared type.
        Field(item.NestingHeight is null ? null : item.Id);
        Field(item.NestingHeight?.Ticks);
        Field(item.Group);
        Field(item.MaxStackedItems);
        Field(item.StopIndex);
        return builder.ToString();
    }

    private static bool LatticeApplies(Container container, IReadOnlyList<ItemInstance> items)
    {
        if (container.Obstacles.Length > 0 || items.Count == 0 || !Supports(items))
        {
            return false;
        }
        var prototype = items[0].Item;
        return !prototype.Tags.Overlaps(prototype.IncompatibleTags)
               && (prototype.EligibleContainerTags.Count == 0 || prototype.EligibleContainerTags.Overlaps(container.Tags))
               && !(container.VoidFillReserveRatio > 0)
               && container.MaxStackDensity is null
               && container.Axles is null
               && prototype.ShapeType == ShapeType.RigidCuboid;
    }

    private sealed record Layout(Rotation Rotation, Dimensions Physical, Dimensions Envelope, long Nx, long Ny,
        long LayerStep, long Capacity);

    private static Layout BestLayout(Container container, Item prototype, PackingConfig config)
    {
        // Every non-floor cell has one full-area direct supporter; "multiple" is satisfiable
        // only on the floor and so caps the lattice at one layer.
        var singleLayer = prototype.MustBeOnFloor || !prototype.Stackable || prototype.GroundContactRule == "multiple";
        var nestingTicks = prototype.NestingHeight?.Ticks ?? 0;
        (SortKey Score, Layout Layout)? best = null;
        foreach (var (rotation, physical) in prototype.Dimensions.UniqueRotations(prototype.AllowedRotations))
        {
            var envelope = config.Clearance.Ticks != 0 ? physical.Expand(config.Clearance) : physical;
            var inner = container.InnerDimensions;
            var nx = inner.Length.Ticks / envelope.Length.Ticks;
            var ny = inner.Width.Ticks / envelope.Width.Ticks;
            var layerStep = envelope.Height.Ticks - nestingTicks;
            var nz = inner.Height.Ticks < envelope.Height.Ticks
                ? 0
                : (inner.Height.Ticks - envelope.Height.Ticks) / layerStep + 1;
            if (singleLayer)
            {
                nz = Math.Min(nz, 1);
            }
            if (prototype.MaxTopLoad is { } topLoad && prototype.Weight.Ticks > 0)
            {
                nz = Math.Min(nz, topLoad.Ticks / prototype.Weight.Ticks + 1);
            }
            if (prototype.MaxStackedItems is { } stacked)
            {
                nz = Math.Min(nz, stacked + 1);
            }
            var capacity = checked(nx * ny * nz);
            if (container.MaxItems is { } maxItems)
            {
                capacity = Math.Min(capacity, maxItems);
            }
            if (container.MaxPayload is { } payload && prototype.Weight.Ticks != 0)
            {
                capacity = Math.Min(capacity, payload.Ticks / prototype.Weight.Ticks);
            }
            foreach (var limit in container.TagLimits.Where(limit => prototype.Tags.Contains(limit.Key)))
            {
                capacity = Math.Min(capacity, limit.Value);
            }
            var score = SortKey.Of(-capacity, envelope.Volume, rotation.Value());
            if (best is null || score < best.Value.Score)
            {
                best = (score, new Layout(rotation, physical, envelope, nx, ny, layerStep, capacity));
            }
        }
        return best!.Value.Layout;
    }

    private static int PlaceLattice(ContainerState state, IReadOnlyList<ItemInstance> items, int total, Layout layout,
        long clearance, SearchStats stats, Deadline deadline)
    {
        var target = (layout.Physical.Length.Ticks, layout.Physical.Width.Ticks, layout.Physical.Height.Ticks);
        var placed = 0;
        for (var index = 0; index < total; index++)
        {
            if (deadline.Expired)
            {
                break;
            }
            stats.SearchNodesExpanded++;
            stats.CandidatePointsConsidered++;
            var (x, y, z) = (index % layout.Nx, index / layout.Nx % layout.Ny, index / (layout.Nx * layout.Ny));
            var point = new Point(x * layout.Envelope.Length.Ticks, y * layout.Envelope.Width.Ticks, z * layout.LayerStep);
            var position = new Point(point.X + clearance, point.Y + clearance, point.Z + clearance);
            // Fungible types reach the same footprint through their own rotation labels, so the
            // stored rotation is looked up per item rather than copied from the prototype.
            var own = items[index].Item.Dimensions.UniqueRotations(items[index].Item.AllowedRotations)
                .First(pair => (pair.Dimensions.Length.Ticks, pair.Dimensions.Width.Ticks, pair.Dimensions.Height.Ticks) == target);
            state.AddDirect(new Placement(items[index], position, own.Rotation, own.Dimensions, point, layout.Envelope));
            placed++;
            stats.CandidatesEvaluated++;
            stats.PlacementsAttempted++;
        }
        return placed;
    }
}
