using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Constraints;

/// <summary>Support ratios as exact integers: a float ratio is converted once and never compared as a float.</summary>
public static class SupportRatio
{
    public const long Scale = 1_000_000;

    public static long Scaled(double ratio) => (long)(ratio * Scale + 0.5);

    /// <summary>Exact <c>floor(baseArea * scaled / Scale)</c>.</summary>
    public static Int128 RequiredArea(Int128 baseArea, long scaled)
    {
        var whole = baseArea / Scale;
        var remainder = baseArea % Scale;
        return checked(whole * scaled + remainder * scaled / Scale);
    }

    /// <summary>Volume set aside for packing material, exact for a metre-scale container.</summary>
    public static Int128 ReservedVolume(Container container) =>
        RequiredArea(container.InnerDimensions.Volume, Scaled(container.VoidFillReserveRatio));

    public static Int128 UsableVolume(Container container) => container.InnerDimensions.Volume - ReservedVolume(container);
}

public sealed class FloorConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context) =>
        context.Item.Item.MustBeOnFloor && context.Point.Z != 0
            ? ConstraintResult.Reject("must_be_on_floor")
            : ConstraintResult.Allow();
}

/// <summary>Refuses a container the item did not opt into; no tags means unconstrained.</summary>
public sealed class ContainerEligibilityConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        var item = context.Item.Item;
        if (item.EligibleContainerTags.Count == 0 || item.EligibleContainerTags.Overlaps(context.Container.Tags))
        {
            return ConstraintResult.Allow();
        }
        return ConstraintResult.Reject("container_ineligible", context.Container.Id);
    }
}

public sealed class CompatibilityConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        var item = context.Item.Item;
        if (item.Tags.Count == 0 && item.IncompatibleTags.Count == 0)
        {
            return ConstraintResult.Allow();
        }
        foreach (var other in context.Placements.Select(placement => placement.Instance.Item))
        {
            if (item.IncompatibleTags.Overlaps(other.Tags) || other.IncompatibleTags.Overlaps(item.Tags))
            {
                return ConstraintResult.Reject("incompatible_items", $"{item.Id} is incompatible with {other.Id}");
            }
        }
        return ConstraintResult.Allow();
    }
}

/// <summary>Caps how many items carrying a tag may share one container.</summary>
public sealed class TagCountConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        var limits = context.Container.TagLimits;
        if (limits.IsEmpty)
        {
            return ConstraintResult.Allow();
        }
        var relevant = limits.Select(limit => limit.Key).Where(context.Item.Item.Tags.Contains)
            .Order(CodePointComparer.Instance);
        foreach (var tag in relevant)
        {
            var limit = limits.First(entry => entry.Key == tag).Value;
            var count = context.Placements.Count(placement => placement.Instance.Item.Tags.Contains(tag));
            if (count + 1 > limit)
            {
                return ConstraintResult.Reject("tag_count_exceeded", $"{tag}: limit {limit}, would be {count + 1}");
            }
        }
        return ConstraintResult.Allow();
    }
}

/// <summary>Minimum support ratio, ground-contact rules and, below half the base, the centre of gravity.</summary>
public sealed class SupportConstraint(double globalMinimum = 0.0) : IPlacementConstraint
{
    public double GlobalMinimum { get; } = globalMinimum;

    /// <summary>Whether this constraint can reject an above-floor placement of the item.</summary>
    public bool AppliesTo(ItemInstance item)
    {
        var rule = item.Item.GroundContactRule;
        return (rule is not null && rule != "free") || Math.Max(GlobalMinimum, item.Item.MinimumSupportRatio) > 0;
    }

    public ConstraintResult Evaluate(ConstraintContext context)
    {
        if (context.Point.Z == 0 || !AppliesTo(context.Item))
        {
            return ConstraintResult.Allow();
        }
        var candidate = context.EnvelopeBox;
        var support = DirectSupportView.For(context.Placements, context.Item, candidate);
        var supporters = support.Supporters.Select(entry => entry.Box).ToList();
        if (GroundContactFailure(context.Item.Item.GroundContactRule, candidate, support.Surfaces, supporters.Count)
            is { } contact)
        {
            return contact;
        }
        var required = Math.Max(GlobalMinimum, context.Item.Item.MinimumSupportRatio);
        if (required <= 0)
        {
            return ConstraintResult.Allow();
        }
        var supportingArea = support.SupportingArea;
        var baseArea = context.EnvelopeDimensions.BaseArea;
        if (supportingArea < SupportRatio.RequiredArea(baseArea, SupportRatio.Scaled(required)))
        {
            return ConstraintResult.Reject("insufficient_support",
                $"{supportingArea}/{baseArea} < {DecimalRendering.FormatFixed(required, 6)}");
        }
        // Above half the base the centroid cannot lie outside the contact hull: a footprint is
        // centrally symmetric, so the hull is only built when it can change the answer.
        if (supportingArea * 2 > baseArea)
        {
            return ConstraintResult.Allow();
        }
        var hull = SupportPolygon.ConvexHull(SupportPolygon.ContactHullPoints(candidate, supporters));
        if (!SupportPolygon.PointInHull(SupportPolygon.DoubledCentroid(candidate), hull))
        {
            return ConstraintResult.Reject("centre_of_gravity_unsupported",
                $"{supportingArea}/{baseArea} met but centroid outside the {hull.Count}-point support hull");
        }
        return ConstraintResult.Allow();
    }

    private static ConstraintResult? GroundContactFailure(string? rule, AxisAlignedBox candidate,
        ImmutableArray<AxisAlignedBox> surfaces, int supporterCount) => rule switch
    {
        "covered" when !TouchesCorners(candidate, surfaces) => ConstraintResult.Reject("ground_contact_violation",
            $"covered: {supporterCount} supporter(s), not all four corners touched"),
        "single" when supporterCount != 1 => ConstraintResult.Reject("ground_contact_violation",
            $"single: rests on {supporterCount} item(s)"),
        "multiple" when supporterCount < 2 => ConstraintResult.Reject("ground_contact_violation",
            $"multiple: rests on {supporterCount} item(s)"),
        _ => null,
    };

    /// <summary>Whether all four base corners rest on some surface, which no ratio can express.</summary>
    private static bool TouchesCorners(AxisAlignedBox candidate, ImmutableArray<AxisAlignedBox> surfaces)
    {
        (long X, long Y)[] corners =
        [
            (candidate.Origin.X, candidate.Origin.Y), (candidate.X2, candidate.Origin.Y),
            (candidate.Origin.X, candidate.Y2), (candidate.X2, candidate.Y2),
        ];
        return corners.All(corner => surfaces.Any(surface =>
            surface.Origin.X <= corner.X && corner.X <= surface.X2 && surface.Origin.Y <= corner.Y && corner.Y <= surface.Y2));
    }
}

/// <summary>
/// Refuses a placement that would rest on something unable to carry it, checked against the
/// cumulative load of the whole stack. The support graph over the placed boxes is cached for
/// one placement list (compared by identity) and each candidate is appended to it.
/// </summary>
public sealed class TopLoadConstraint : IPlacementConstraint
{
    private ImmutableArray<Placement> cachedPlacements;
    private LoadSupportGraph? cachedBase;
    private ImmutableArray<LoadUnit> cachedUnits = [];
    private long hint = 1;

    public ConstraintResult Evaluate(ConstraintContext context)
    {
        if (!context.StackSensitive)
        {
            return ConstraintResult.Allow();
        }
        var candidate = context.EnvelopeBox;
        var unit = LoadUnit.Candidate(candidate, context.Item);
        var footprint = Math.Max(candidate.X2 - candidate.Origin.X, candidate.Y2 - candidate.Origin.Y);
        var (baseGraph, baseUnits) = BaseFor(context.Placements, footprint);
        var units = baseUnits.Add(unit);
        var graph = baseGraph.WithUnit(unit, hint);
        if (LoadPropagation.NonStackableFailure(context.Placements, context.Item, graph, units.Length - 1) is { } failure)
        {
            return failure;
        }
        var density = context.Container.MaxStackDensity?.Ticks;
        var overload = LoadPropagation.Overloaded(units, graph) ?? LoadPropagation.Crushed(units, graph)
            ?? LoadPropagation.StackLimitExceeded(units, graph)
            ?? LoadPropagation.StackDensityExceeded(units, density, graph);
        return overload is { } reason ? ConstraintResult.Reject(reason) : ConstraintResult.Allow();
    }

    /// <summary>The graph over the placements alone, rebuilt only when a wider candidate needs a coarser cell.</summary>
    private (LoadSupportGraph, ImmutableArray<LoadUnit>) BaseFor(ImmutableArray<Placement> placements, long footprint)
    {
        var same = !cachedPlacements.IsDefault && cachedPlacements == placements;
        if (same && footprint <= hint && cachedBase is not null)
        {
            return (cachedBase, cachedUnits);
        }
        hint = Math.Max(same ? hint : 1, footprint);
        cachedUnits = LoadUnit.From(placements);
        cachedPlacements = placements;
        cachedBase = new LoadSupportGraph(cachedUnits, hint);
        return (cachedBase, cachedUnits);
    }
}

/// <summary>Refuses burying an item due at an earlier stop; a no-op unless some item declares a stop.</summary>
public sealed class RouteOrderConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        if (!context.RouteSensitive)
        {
            return ConstraintResult.Allow();
        }
        var units = LoadUnit.From(context.Placements, LoadUnit.Hypothetical(context.EnvelopeBox, context.Item));
        var stops = context.Placements.Select(placement => Stops.Of(placement.Instance.Item))
            .Append(Stops.Of(context.Item.Item)).ToList();
        return LoadPropagation.RouteOrderViolated(units, stops) is { } failure
            ? ConstraintResult.Reject(failure)
            : ConstraintResult.Allow();
    }
}

/// <summary>Refuses a placement that would push either axle of a two-axle container over its limit.</summary>
public sealed class AxleLoadConstraint : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context)
    {
        if (context.Container.Axles is not { } axles)
        {
            return ConstraintResult.Allow();
        }
        var units = LoadUnit.From(context.Placements, LoadUnit.Hypothetical(context.EnvelopeBox, context.Item));
        return AxleLoad.Exceeded(axles, units, context.Container.TareWeight.Ticks,
                context.Container.InnerDimensions.Length.Ticks) is { } failure
            ? ConstraintResult.Reject(failure)
            : ConstraintResult.Allow();
    }
}
