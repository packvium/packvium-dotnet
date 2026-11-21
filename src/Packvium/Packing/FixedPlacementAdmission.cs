using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;
using Packvium.Validation;

namespace Packvium.Packing;

/// <summary>
/// What the solve starts from: the containers fixed placements name, in opening order (request
/// container order, then instance), each holding only its fixed placements; and every instance a
/// fixed placement did not take, in request order.
/// </summary>
public sealed record FixedLoad(ImmutableArray<PackedContainer> Containers, ImmutableArray<ItemInstance> Free);

/// <summary>
/// Admission of a request's fixed placements. A fixed placement enters the solve as a real
/// <see cref="Placement"/> -- weight, support, top load and all -- seeded into the one container
/// instance it names, so every rule the engine enforces holds for it with no rule of its own.
/// A fixed set that is not a valid packing on its own is refused before any search with
/// <see cref="FixedPlacementException"/>, rather than exempted: the check is the ordinary
/// <see cref="IndependentSolutionValidator"/> over the fixed set alone, with item accounting left
/// out because the free items have not been placed yet.
/// </summary>
public static class FixedPlacementAdmission
{
    /// <summary>
    /// Resolves the request's fixed placements, or refuses the request. O(f log f) to resolve f
    /// entries, plus one validator pass over them, O(f^2) in the worst case of its pairwise sweep.
    /// </summary>
    public static FixedLoad Admit(PackingRequest request, double minimumSupportRatio, Length clearance, long? maxContainers)
    {
        if (request.FixedPlacements.IsEmpty)
        {
            return new FixedLoad([], request.Instances);
        }
        var items = request.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var containers = request.Containers.ToDictionary(container => container.Id, StringComparer.Ordinal);
        foreach (var entry in request.FixedPlacements)
        {
            RequireKnown(entry, items, containers);
        }
        var instances = AssignInstances(request.FixedPlacements, items);
        var grouped = GroupByContainer(request, instances, clearance);
        RequireContiguousInstances(grouped, containers, maxContainers);
        ImmutableArray<PackedContainer> packed =
        [
            .. grouped.Select(group => new PackedContainer(containers[group.ContainerId], checked((int)group.Sequence),
                WithSupportAndLoads(group.Placements))),
        ];
        RequireValidAlone(request, packed, minimumSupportRatio, clearance);
        var taken = packed.SelectMany(container => container.Placements).Select(placement => placement.Instance.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new FixedLoad(packed, [.. request.Instances.Where(instance => !taken.Contains(instance.Id))]);
    }

    private sealed record Group(string ContainerId, long Sequence, List<Placement> Placements);

    private static void RequireKnown(FixedPlacement entry, Dictionary<string, Item> items,
        Dictionary<string, Container> containers)
    {
        if (!items.TryGetValue(entry.ItemId, out var item))
        {
            throw new FixedPlacementException($"unknown item type {CanonicalText.Spelling(entry.ItemId)}");
        }
        if (!containers.ContainsKey(entry.ContainerId))
        {
            throw new FixedPlacementException($"unknown container type {CanonicalText.Spelling(entry.ContainerId)}");
        }
        if (!item.AllowedRotations.Contains(entry.Rotation))
        {
            throw new FixedPlacementException($"{entry.ItemId} may not be placed in orientation {entry.Rotation.Value()}");
        }
    }

    /// <summary>Fixed items take the first instances of their type, in the order they are listed.</summary>
    private static List<ItemInstance> AssignInstances(ImmutableArray<FixedPlacement> entries, Dictionary<string, Item> items)
    {
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        var instances = new List<ItemInstance>(entries.Length);
        foreach (var entry in entries)
        {
            var item = items[entry.ItemId];
            var count = taken.GetValueOrDefault(item.Id) + 1;
            taken[item.Id] = count;
            if (count > item.Quantity)
            {
                throw new FixedPlacementException($"{count} {item.Id} fixed, {item.Quantity} requested");
            }
            instances.Add(new ItemInstance(item, count));
        }
        return instances;
    }

    private static List<Group> GroupByContainer(PackingRequest request, List<ItemInstance> instances, Length clearance)
    {
        var groups = new Dictionary<(string, long), Group>();
        var firstSeen = new List<Group>();
        for (var index = 0; index < instances.Count; index++)
        {
            var entry = request.FixedPlacements[index];
            if (!groups.TryGetValue((entry.ContainerId, entry.ContainerInstance), out var group))
            {
                group = new Group(entry.ContainerId, entry.ContainerInstance, []);
                groups[(entry.ContainerId, entry.ContainerInstance)] = group;
                firstSeen.Add(group);
            }
            group.Placements.Add(PlacementOf(entry, instances[index], clearance));
        }
        var order = request.Containers.Select((container, index) => (container.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        return [.. firstSeen.OrderBy(group => order[group.ContainerId]).ThenBy(group => group.Sequence)];
    }

    private static Placement PlacementOf(FixedPlacement entry, ItemInstance instance, Length clearance)
    {
        var margin = clearance.Ticks;
        var position = entry.Position;
        // The clearance envelope must be inside the container, as it must for any placement; an
        // item flush against a wall has an envelope that starts before it.
        if (Math.Min(position.X, Math.Min(position.Y, position.Z)) < margin)
        {
            throw new FixedPlacementException($"outside_container: {instance.Id}");
        }
        var dimensions = instance.Dimensions.Rotated(entry.Rotation);
        var envelope = margin > 0 ? dimensions.Expand(clearance) : dimensions;
        var envelopeOrigin = new Point(position.X - margin, position.Y - margin, position.Z - margin);
        // A far end past what 64 bits hold is beyond every container's wall; the reference's
        // unbounded integers reach the same verdict in the validator.
        if (FarEndOverflows(envelopeOrigin, envelope))
        {
            throw new FixedPlacementException($"outside_container: {instance.Id}");
        }
        return new Placement(instance, position, entry.Rotation, dimensions, envelopeOrigin, envelope, Fixed: true);
    }

    private static bool FarEndOverflows(Point origin, Dimensions extent) =>
        long.MaxValue - origin.X < extent.Length.Ticks || long.MaxValue - origin.Y < extent.Width.Ticks
        || long.MaxValue - origin.Z < extent.Height.Ticks;

    private static void RequireContiguousInstances(List<Group> grouped, Dictionary<string, Container> containers,
        long? maxContainers)
    {
        var named = new List<(string ContainerId, List<long> Sequences)>();
        foreach (var group in grouped)
        {
            if (named.Count == 0 || named[^1].ContainerId != group.ContainerId)
            {
                named.Add((group.ContainerId, []));
            }
            named[^1].Sequences.Add(group.Sequence);
        }
        foreach (var (containerId, sequences) in named)
        {
            if (!sequences.Select((sequence, index) => sequence == index + 1).All(matches => matches))
            {
                var spelled = "[" + string.Join(",", sequences.Select(Spell)) + "]";
                throw new FixedPlacementException(
                    $"{containerId} instances {spelled} are not numbered 1..{sequences.Count}");
            }
            if (containers[containerId].Quantity is { } quantity && sequences.Count > quantity)
            {
                throw new FixedPlacementException($"{sequences.Count} {containerId} named, {quantity} available");
            }
        }
        if (maxContainers is { } maximum && grouped.Count > maximum)
        {
            throw new FixedPlacementException(
                $"{grouped.Count} containers hold fixed items, max_containers is {maximum}");
        }
    }

    private static string Spell(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Each fixed item's support ratio from the fixed items under it, and its top load. Support is
    /// measured against every other fixed item in the container, not only the ones listed before
    /// it: the set is one arrangement, and listing order is not physics. O(k^2) for k fixed items
    /// in one container.
    /// </summary>
    private static ImmutableArray<Placement> WithSupportAndLoads(List<Placement> placements)
    {
        var supported = placements.Select((placement, index) =>
            placement with { SupportRatio = SupportRatio([.. placements.Where((_, other) => other != index)], placement) })
            .ToList();
        var loads = LoadPropagation.TopLoads(LoadUnit.From(supported));
        return [.. supported.Select((placement, index) => placement with { TopLoad = new Weight(loads[index]) })];
    }

    private static double SupportRatio(List<Placement> others, Placement placement)
    {
        if (placement.EnvelopeOrigin.Z == 0)
        {
            return 1.0;
        }
        var support = DirectSupportView.For(others, placement.Instance, placement.EnvelopeBox);
        return DecimalRendering.TrueDivide(support.SupportingArea, placement.EnvelopeDimensions.BaseArea);
    }

    private static void RequireValidAlone(PackingRequest request, ImmutableArray<PackedContainer> packed,
        double minimumSupportRatio, Length clearance)
    {
        var report = IndependentSolutionValidator.Validate(request, packed, minimumSupportRatio, clearance);
        if (!report.Valid)
        {
            var issue = report.Issues[0];
            throw new FixedPlacementException($"{issue.Code}: {issue.Detail}");
        }
    }
}
