using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>One placement as the container checks see it: its id, envelope, the placement and its item type.</summary>
internal sealed record Entry(JsonValue Id, Cuboid Envelope, JsonValue Placement, ItemSpec? Spec)
{
    public string Label => PyFormat.Str(Id);
}

/// <summary>
/// One packed container: containment, obstacles, rotation, eligibility and floor per placement;
/// collisions and incompatibility per pair; then support, loads and the reported figures.
/// </summary>
internal static class ContainerCheck
{
    private static readonly ImmutableArray<string> Sides = ["length", "width", "height"];

    /// <summary>
    /// O(p² · h) for p placements, h the cost of one hull overlap test (constant for boxes), plus
    /// the support and unloading checks of <see cref="Stacking"/>.
    /// </summary>
    public static void Check(JsonValue packed, ContainerSpec spec, RequestContext context, Ratio globalSupport, List<ResultIssue> issues)
    {
        var placements = Json.Items(Json.At(packed, "placements"), "placements");
        var boundary = new Cuboid(0, 0, 0, spec.Inner[0], spec.Inner[1], spec.Inner[2]);
        var byId = new Ordered<Entry>();
        var payload = BigInteger.Zero;
        foreach (var placement in placements)
        {
            var itemId = Json.At(placement, "item_id");
            var itemSpec = context.Items.GetOrDefault(Json.At(placement, "item_type"));
            var envelope = Extents.Envelope(placement, context.Clearance);
            var entry = new Entry(itemId, envelope, placement, itemSpec);
            byId.Set(itemId, entry);
            if (!boundary.Contains(envelope))
            {
                issues.Add(Issues.Of("outside_container", $"{entry.Label} in {Extents.PackedId(packed)}"));
            }
            foreach (var obstacle in spec.Obstacles)
            {
                if (envelope.Intersects(obstacle) && Geometry.SolidsOverlap(Solids.PlacedHull(entry), obstacle.Vertices()))
                {
                    issues.Add(Issues.Of("obstacle_collision", $"{entry.Label} in {Extents.PackedId(packed)}"));
                }
            }
            if (itemSpec is null)
            {
                continue;
            }
            payload += itemSpec.Weight;
            CheckRotation(placement, itemSpec, entry.Label, issues);
            if (!itemSpec.EligibleContainerTags.IsEmpty && !itemSpec.EligibleContainerTags.Overlaps(spec.Tags))
            {
                var tags = itemSpec.EligibleContainerTags.Sorted().Select(PyFormat.Repr);
                issues.Add(Issues.Of("container_ineligible",
                    $"{entry.Label}: {PyFormat.Str(Json.At(packed, "container_type"))} has none of {PyFormat.ListOf(tags)}"));
            }
            if (itemSpec.MustBeOnFloor && !envelope.Z.IsZero)
            {
                issues.Add(Issues.Of("floor_violation", $"{entry.Label} at z={envelope.Z}"));
            }
        }
        // A repeated item id keeps its first spelling and its last placement, as the reference's dict does.
        var entries = byId.Entries.Select(entry => entry.Value with { Id = entry.Key }).ToImmutableArray();
        CheckPairs(packed, entries, issues);
        var graph = SupportGraph.Build(entries);
        Stacking.CheckSupportAndLoad(entries, graph, globalSupport, issues);
        var loads = Stacking.PropagateLoad(entries, graph);
        if (spec.MaxStackDensity is { } density)
        {
            Stacking.CheckStackDensity(packed, entries, loads, density, issues);
        }
        Stacking.CheckCrush(packed, entries, loads, issues);
        Unloading.Check(packed, entries, graph, spec.Inner, spec.AccessDirections, issues);
        ContainerFigures.CheckLimits(packed, placements, spec, context.Items, payload, issues);
        ContainerFigures.CheckVolumes(packed, placements, spec, context.Items, byId, loads, issues);
        var totalWeight = MadeToMeasure.Payload(packed, context.Items);
        ContainerFigures.CheckCentreOfMass(packed, placements, spec, context.Items, totalWeight, issues);
        ContainerFigures.CheckAxles(packed, placements, spec, context.Items, totalWeight, issues);
    }

    private static void CheckRotation(JsonValue placement, ItemSpec spec, string itemId, List<ResultIssue> issues)
    {
        var orientation = Json.At(placement, "orientation");
        if (!Geometry.IsRotation(orientation))
        {
            issues.Add(Issues.Of("unknown_orientation", $"{itemId}: {PyFormat.Str(orientation)}"));
            return;
        }
        var code = ((JsonString)orientation).Value;
        if (!spec.Allows(orientation))
        {
            issues.Add(Issues.Of("forbidden_rotation", $"{itemId}: {code}"));
        }
        var expected = spec.Rotated(code);
        var dimensions = Json.At(placement, "dimensions");
        var actual = Sides.Select(side => Json.At(Json.At(dimensions, side), "ticks")).ToImmutableArray();
        if (!Extents.Matches(actual, expected))
        {
            issues.Add(Issues.Of("dimension_mismatch",
                $"{itemId}: {code} of {PyFormat.IntTuple(spec.Dimensions)} is {PyFormat.IntTuple(expected)}, got {PyFormat.ValueTuple(actual)}"));
        }
    }

    /// <summary>Every pair once, in placement order: a collision unless they nest, and incompatible tags. O(p² · h).</summary>
    private static void CheckPairs(JsonValue packed, ImmutableArray<Entry> entries, List<ResultIssue> issues)
    {
        for (var first = 0; first < entries.Length; first++)
        {
            var left = entries[first];
            for (var second = first + 1; second < entries.Length; second++)
            {
                var right = entries[second];
                if (left.Envelope.Intersects(right.Envelope)
                    && Geometry.SolidsOverlap(Solids.PlacedHull(left), Solids.PlacedHull(right))
                    && !Solids.ValidNesting(left, right))
                {
                    issues.Add(Issues.Of("collision", $"{left.Label} with {right.Label} in {Extents.PackedId(packed)}"));
                }
                if (left.Spec is { } leftSpec && right.Spec is { } rightSpec
                    && (leftSpec.IncompatibleTags.Overlaps(rightSpec.Tags) || rightSpec.IncompatibleTags.Overlaps(leftSpec.Tags)))
                {
                    issues.Add(Issues.Of("incompatible_items", $"{left.Label} is incompatible with {right.Label} in {Extents.PackedId(packed)}"));
                }
            }
        }
    }
}
