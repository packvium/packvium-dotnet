using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Validation;

public sealed record ValidationIssue(string Code, string Detail);

public sealed record ValidationReport(bool Valid, ImmutableArray<ValidationIssue> Issues);

/// <summary>
/// Certificate validation: re-derives every guarantee from the placements alone and shares no
/// state with the solvers, so a placement a solver never verified is caught here. Collision
/// pairs come from a z-bucketed x-sweep, near-linear on a layered lattice.
/// </summary>
public static class IndependentSolutionValidator
{
    public static ValidationReport Validate(PackingRequest request, IReadOnlyList<PackedContainer> containers,
        double minimumSupportRatio = 0.0, Length? clearance = null, IReadOnlyList<UnpackedItem>? unpacked = null)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expected = request.Instances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var inventory = new Dictionary<string, int>(StringComparer.Ordinal);
        var clearanceTicks = clearance?.Ticks ?? 0;
        foreach (var packed in containers)
        {
            inventory[packed.Container.Id] = inventory.GetValueOrDefault(packed.Container.Id) + 1;
            ValidateContainer(packed, minimumSupportRatio, clearanceTicks, seen, issues);
        }
        foreach (var container in request.Containers)
        {
            if (container.Quantity is { } quantity && inventory.GetValueOrDefault(container.Id) > quantity)
            {
                issues.Add(new ValidationIssue("container_inventory_exceeded", container.Id));
            }
        }
        if (unpacked is not null)
        {
            foreach (var item in unpacked)
            {
                var itemId = item.Instance.Id;
                if (!seen.Add(itemId))
                {
                    issues.Add(new ValidationIssue("duplicate_item", $"{itemId} is both packed and unpacked"));
                }
                if (item.Reason.Length == 0)
                {
                    issues.Add(new ValidationIssue("missing_reason", itemId));
                }
            }
            issues.AddRange(expected.Except(seen).Order(CodePointComparer.Instance)
                .Select(itemId => new ValidationIssue("missing_item", itemId)));
        }
        issues.AddRange(seen.Except(expected).Order(CodePointComparer.Instance)
            .Select(itemId => new ValidationIssue("unknown_item", itemId)));
        CheckGroups(containers, issues);
        if (unpacked is not null)
        {
            CheckGroupAccounting(request, containers, issues);
        }
        return new ValidationReport(issues.Count == 0, [.. issues]);
    }

    private static void ValidateContainer(PackedContainer packed, double minimumSupportRatio, long clearanceTicks,
        HashSet<string> seen, List<ValidationIssue> issues)
    {
        var boundary = new AxisAlignedBox(Point.Origin, packed.Container.InnerDimensions);
        var placements = packed.Placements;
        var compatibilitySensitive = placements.Any(p => p.Instance.Item.Tags.Count > 0 || p.Instance.Item.IncompatibleTags.Count > 0);
        var supportSensitive = minimumSupportRatio > 0 || placements.Any(p => p.Instance.Item.MinimumSupportRatio > 0
            || p.Instance.Item.GroundContactRule is not (null or "free"));
        var stackSensitive = placements.Any(p => p.Instance.Item.IsStackSensitive);
        var stackGraph = stackSensitive ? new LoadSupportGraph(LoadUnit.From(placements)) : null;
        foreach (var (left, right) in CollisionPairs(placements))
        {
            issues.Add(new ValidationIssue("collision", $"{placements[left].Instance.Id} with {placements[right].Instance.Id}"));
        }
        long payload = 0;
        for (var index = 0; index < placements.Length; index++)
        {
            var placement = placements[index];
            var itemId = placement.Instance.Id;
            if (!seen.Add(itemId))
            {
                issues.Add(new ValidationIssue("duplicate_item", itemId));
            }
            payload = checked(payload + placement.Instance.Weight.Ticks);
            CheckPlacement(packed, placement, clearanceTicks, boundary, issues);
            var context = compatibilitySensitive || supportSensitive
                ? new ConstraintContext(packed.Container, placements.RemoveAt(index), placement.Instance,
                    placement.EnvelopeOrigin, placement.Rotation, placement.Dimensions, placement.EnvelopeDimensions)
                : null;
            var constraints = new List<IPlacementConstraint>();
            if (compatibilitySensitive)
            {
                constraints.Add(new CompatibilityConstraint());
                constraints.Add(new TagCountConstraint());
            }
            if (supportSensitive)
            {
                constraints.Add(new SupportConstraint(minimumSupportRatio));
            }
            foreach (var constraint in constraints)
            {
                var result = constraint.Evaluate(context!);
                if (!result.Allowed)
                {
                    issues.Add(new ValidationIssue(result.Code, $"{itemId}: {result.Detail}"));
                }
            }
            if (stackGraph is not null
                && LoadPropagation.NonStackableFailure(placements, placement.Instance, stackGraph, index) is { } failure)
            {
                issues.Add(new ValidationIssue(failure.Code, $"{itemId}: {failure.Detail}"));
            }
        }
        CheckWholeContainer(packed, payload, issues);
    }

    private static void CheckPlacement(PackedContainer packed, Placement placement, long clearanceTicks,
        AxisAlignedBox boundary, List<ValidationIssue> issues)
    {
        var itemId = placement.Instance.Id;
        var item = placement.Instance.Item;
        if (!boundary.Contains(placement.EnvelopeBox))
        {
            issues.Add(new ValidationIssue("outside_container", itemId));
        }
        if (!item.AllowedRotations.Contains(placement.Rotation))
        {
            issues.Add(new ValidationIssue("forbidden_rotation", itemId));
        }
        if (placement.Dimensions != item.Dimensions.Rotated(placement.Rotation))
        {
            issues.Add(new ValidationIssue("dimension_mismatch", itemId));
        }
        if (!EnvelopeMatches(placement, clearanceTicks))
        {
            issues.Add(new ValidationIssue("clearance_mismatch", itemId));
        }
        if (packed.Container.Obstacles.SelectMany(obstacle => obstacle.Boxes).Any(box => Collision.PlacementHitsBox(placement, box)))
        {
            issues.Add(new ValidationIssue("obstacle_collision", itemId));
        }
        if (item.MustBeOnFloor && placement.EnvelopeOrigin.Z != 0)
        {
            issues.Add(new ValidationIssue("must_be_on_floor", $"{itemId}: "));
        }
        if (item.EligibleContainerTags.Count > 0 && !item.EligibleContainerTags.Overlaps(packed.Container.Tags))
        {
            issues.Add(new ValidationIssue("container_ineligible", itemId));
        }
    }

    /// <summary>The whole-container bearing, axle, route, payload and item-count passes.</summary>
    private static void CheckWholeContainer(PackedContainer packed, long payload, List<ValidationIssue> issues)
    {
        var container = packed.Container;
        var units = LoadUnit.From(packed.Placements);
        var failure = LoadPropagation.Overloaded(units) ?? LoadPropagation.Crushed(units)
            ?? LoadPropagation.StackLimitExceeded(units)
            ?? LoadPropagation.StackDensityExceeded(units, container.MaxStackDensity?.Ticks);
        if (failure is { } bearing)
        {
            issues.Add(new ValidationIssue(bearing.Item1, $"{packed.Id}: {bearing.Item2}"));
        }
        if (container.Axles is { } axles
            && AxleLoad.Exceeded(axles, units, container.TareWeight.Ticks, container.InnerDimensions.Length.Ticks) is { } axle)
        {
            issues.Add(new ValidationIssue(axle.Code, $"{packed.Id}: {axle.Detail}"));
        }
        if (packed.Placements.Any(p => p.Instance.Item.StopIndex is not null) && UnloadingOrderViolation(packed) is { } route)
        {
            issues.Add(route);
        }
        if (container.MaxPayload is { } maxPayload && payload > maxPayload.Ticks)
        {
            issues.Add(new ValidationIssue("payload_exceeded", packed.Id));
        }
        if (container.MaxItems is { } maxItems && packed.Placements.Length > maxItems)
        {
            issues.Add(new ValidationIssue("max_items_exceeded", packed.Id));
        }
    }

    /// <summary>Intersecting pairs by an x-sweep within z buckets; a valid nested pair is not a collision.</summary>
    private static List<(int, int)> CollisionPairs(ImmutableArray<Placement> placements)
    {
        if (placements.IsEmpty)
        {
            return [];
        }
        var cell = placements.Max(p => p.EnvelopeBox.Z2 - p.EnvelopeBox.Origin.Z);
        if (cell == 0)
        {
            cell = 1;
        }
        var buckets = new Dictionary<long, List<int>>();
        for (var index = 0; index < placements.Length; index++)
        {
            var box = placements[index].EnvelopeBox;
            foreach (var zCell in new HashSet<long> { IntMath.FloorDiv(box.Origin.Z, cell), IntMath.FloorDiv(box.Z2 - 1, cell) })
            {
                if (!buckets.TryGetValue(zCell, out var bucket))
                {
                    buckets[zCell] = bucket = [];
                }
                bucket.Add(index);
            }
        }
        var pairs = new HashSet<(int, int)>();
        foreach (var indices in buckets.Values)
        {
            var ordered = indices.Select(i => (X1: placements[i].EnvelopeBox.Origin.X, X2: placements[i].EnvelopeBox.X2, Index: i))
                .OrderBy(entry => entry);
            var active = new List<(long Right, int Index)>();
            foreach (var (x1, x2, index) in ordered)
            {
                active = [.. active.Where(entry => entry.Right > x1)];
                foreach (var (_, other) in active)
                {
                    if (Collision.PlacementsCollide(placements[index], placements[other])
                        && !Nesting.IsValidNesting(placements[index], placements[other]))
                    {
                        pairs.Add((Math.Min(index, other), Math.Max(index, other)));
                    }
                }
                active.Add((x2, index));
            }
        }
        return [.. pairs.Order()];
    }

    private static ValidationIssue? UnloadingOrderViolation(PackedContainer packed)
    {
        var placements = packed.Placements;
        try
        {
            RouteSequence.SafeRouteRemovalOrder([.. placements.Select(p => p.Box)],
                [.. placements.Select(p => p.Instance.Item.StopIndex)], packed.Container.InnerDimensions);
        }
        catch (RouteSequenceException error)
        {
            var stuck = string.Join(", ", error.Stuck.Order().Select(index => placements[index].Instance.Id));
            return new ValidationIssue("unloading_order_violation",
                $"{packed.Id}: stop {error.Stop} cannot be fully unloaded ({stuck} still blocked)");
        }
        return null;
    }

    private static bool EnvelopeMatches(Placement placement, long clearanceTicks)
    {
        var expected = clearanceTicks != 0 ? placement.Dimensions.Expand(new Length(clearanceTicks)) : placement.Dimensions;
        return placement.EnvelopeDimensions == expected
               && placement.Position.X == placement.EnvelopeOrigin.X + clearanceTicks
               && placement.Position.Y == placement.EnvelopeOrigin.Y + clearanceTicks
               && placement.Position.Z == placement.EnvelopeOrigin.Z + clearanceTicks;
    }

    private static void CheckGroups(IReadOnlyList<PackedContainer> containers, List<ValidationIssue> issues)
    {
        var located = new SortedDictionary<string, SortedSet<string>>(CodePointComparer.Instance);
        foreach (var packed in containers)
        {
            foreach (var group in packed.Placements.Select(p => p.Instance.Item.Group).OfType<string>())
            {
                if (!located.TryGetValue(group, out var where))
                {
                    located[group] = where = new SortedSet<string>(CodePointComparer.Instance);
                }
                where.Add(packed.Id);
            }
        }
        foreach (var (group, where) in located.Where(entry => entry.Value.Count > 1))
        {
            issues.Add(new ValidationIssue("group_split", $"{group}: {string.Join(", ", where)}"));
        }
    }

    private static void CheckGroupAccounting(PackingRequest request, IReadOnlyList<PackedContainer> containers,
        List<ValidationIssue> issues)
    {
        var expected = new SortedDictionary<string, HashSet<string>>(CodePointComparer.Instance);
        foreach (var instance in request.Instances.Where(instance => instance.Item.Group is not null))
        {
            if (!expected.TryGetValue(instance.Item.Group!, out var members))
            {
                expected[instance.Item.Group!] = members = new HashSet<string>(StringComparer.Ordinal);
            }
            members.Add(instance.Id);
        }
        var placed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var placement in containers.SelectMany(packed => packed.Placements).Where(p => p.Instance.Item.Group is not null))
        {
            if (!placed.TryGetValue(placement.Instance.Item.Group!, out var members))
            {
                placed[placement.Instance.Item.Group!] = members = new HashSet<string>(StringComparer.Ordinal);
            }
            members.Add(placement.Instance.Id);
        }
        foreach (var (group, members) in expected)
        {
            var count = placed.GetValueOrDefault(group)?.Count ?? 0;
            if (count > 0 && count < members.Count)
            {
                issues.Add(new ValidationIssue("group_partial", $"{group}: {count}/{members.Count} packed"));
            }
        }
    }
}
