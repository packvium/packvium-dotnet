using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Packing;

/// <summary>
/// The request schema's rules, applied to a request built from objects. The JSON rules walk a JSON
/// request; <see cref="Packer.Pack(IEnumerable{Item}, IEnumerable{Container}, IEnumerable{FixedPlacement})"/>
/// takes objects, whose own constructors already refuse most of the same values but cannot know
/// where in a request they will sit. This walks the objects in the JSON rules' order --
/// configuration, items, item ids, containers, container ids, fixed placements -- so a value the
/// JSON path refuses is refused here with the same reason, RFC 6901 field and message. Only rules
/// the JSON path also applies are checked, so a request that came through the JSON path is never
/// refused here.
///
/// <para>Unordered sets (accepted stacking groups, tag limits) are walked in code-point order: a set
/// has no order of its own, and hash order must not decide which value an error names. O(size of
/// the request).</para>
/// </summary>
internal static class RequestObjects
{
    private const long MaxExactMagnitude = MadeToMeasure.MaxExactInteger;

    private const string Configuration = "/configuration";

    /// <summary>Refuses the request with its first violation, or returns.</summary>
    public static void Check(IReadOnlyList<Item> items, IReadOnlyList<Container> containers, PackingConfig config,
        IReadOnlyList<FixedPlacement> fixedPlacements)
    {
        CheckConfiguration(config);
        for (var index = 0; index < items.Count; index++)
        {
            CheckItem(items[index], RequestPointer.Of("items", index));
        }
        RequireUniqueIds(items.Select(item => item.Id), "items");
        for (var index = 0; index < containers.Count; index++)
        {
            CheckContainer(containers[index], RequestPointer.Of("containers", index));
        }
        RequireUniqueIds(containers.Select(container => container.Id), "containers");
        for (var index = 0; index < fixedPlacements.Count; index++)
        {
            CheckFixedPlacement(fixedPlacements[index], index);
        }
    }

    /// <summary>
    /// An unknown objective is not checked here: it keeps its own error on this path, raised when
    /// the objective is resolved.
    /// </summary>
    private static void CheckConfiguration(PackingConfig config)
    {
        (string Name, long? Value, long Minimum)[] integers =
        [
            ("time_limit_ms", config.TimeLimitMs, 1), ("alternatives", config.TopK, 1),
            ("max_containers", config.MaxContainers, 1), ("exact_item_limit", config.ExactItemLimit, 1),
            ("multi_start_orders", config.MultiStartOrders, 1),
            ("max_candidates_per_item", config.MaxCandidatesPerItem, 1),
            ("max_candidate_points", config.MaxCandidatePoints, 16),
            ("container_plan_beam_width", config.ContainerPlanBeamWidth, 1),
            ("container_plan_node_limit", config.ContainerPlanNodeLimit, 1),
            ("dimensional_weight_divisor", config.DimensionalWeightDivisor, 1),
        ];
        foreach (var (name, value, minimum) in integers)
        {
            Optional(value, Configuration + RequestPointer.Of(name), minimum);
        }
        Ratio(config.MinimumSupportRatio, Configuration + RequestPointer.Of("minimum_support_ratio"));
        if (config.EffortBudget is not { } budget)
        {
            return;
        }
        var where = Configuration + RequestPointer.Of("effort_budget");
        (string Name, long? Value)[] limits =
        [
            ("max_candidates_evaluated", budget.MaxCandidatesEvaluated),
            ("max_placement_attempts", budget.MaxPlacementAttempts), ("max_search_nodes", budget.MaxSearchNodes),
            ("max_restarts", budget.MaxRestarts),
        ];
        foreach (var (name, value) in limits)
        {
            Optional(value, where + RequestPointer.Of(name), 1);
        }
    }

    private static void CheckItem(Item item, string where)
    {
        Integer(item.Quantity, where + RequestPointer.Of("quantity"), 1);
        Optional(item.MaxStackedItems, where + RequestPointer.Of("max_stacked_items"), 1);
        Optional(item.StopIndex, where + RequestPointer.Of("stop_index"), 0);
        Optional(item.Value, where + RequestPointer.Of("value"), 0);
        Optional(item.MaxCompressionPressureKpa, where + RequestPointer.Of("max_compression_pressure_kpa"), 0);
        Ratio(item.MinimumSupportRatio, where + RequestPointer.Of("minimum_support_ratio"));
        if (item.StackingGroup is { } group)
        {
            StackingGroupName(group, where + RequestPointer.Of("stacking_group"));
        }
        var accepted = where + RequestPointer.Of("accepts_stacking_groups");
        var index = 0;
        foreach (var name in item.AcceptsStackingGroups.Order(CodePointComparer.Instance))
        {
            StackingGroupName(name, accepted + RequestPointer.Of(index++));
        }
    }

    private static void CheckContainer(Container container, string where)
    {
        Optional(container.Quantity, where + RequestPointer.Of("quantity"), 1);
        Optional(container.MaxItems, where + RequestPointer.Of("max_items"), 1);
        Integer(container.CostMinor, where + RequestPointer.Of("cost_minor"), 0);
        Ratio(container.VoidFillReserveRatio, where + RequestPointer.Of("void_fill_reserve_ratio"));
        var limits = where + RequestPointer.Of("tag_limits");
        foreach (var (tag, limit) in container.TagLimits.OrderBy(entry => entry.Key, CodePointComparer.Instance))
        {
            Integer(limit, limits + RequestPointer.Of(tag), 1);
        }
        if (container.RateTable is { } table)
        {
            CheckRateTable(table, where + RequestPointer.Of("rate_table"));
        }
        if (container.IsFlatBag)
        {
            CheckFlatBag(container, where);
        }
        if (container.MadeToMeasure is { } machine)
        {
            CheckMadeToMeasure(container, machine, where);
        }
    }

    private static void CheckRateTable(RateTable table, string where)
    {
        for (var index = 0; index < table.WeightBracketsG.Length; index++)
        {
            Integer(table.WeightBracketsG[index], where + RequestPointer.Of("weight_brackets_g", index), 1);
        }
        for (var index = 0; index < table.PricesMinor.Length; index++)
        {
            Integer(table.PricesMinor[index], where + RequestPointer.Of("prices_minor", index), 0);
        }
        Integer(table.MinimumChargeMinor, where + RequestPointer.Of("minimum_charge_minor"), 0);
        Integer(table.FuelSurchargePermille, where + RequestPointer.Of("fuel_surcharge_permille"), 0);
    }

    /// <summary>The fields a flat bag refuses, in the JSON rules' order; an empty list and a zero reserve change nothing.</summary>
    private static void CheckFlatBag(Container container, string where)
    {
        if (!FlatBag.Kinds.Contains(container.DeformableBoundary, StringComparer.Ordinal))
        {
            throw new InvalidRequestException("not_allowed", where + RequestPointer.Of("deformable_boundary", "kind"),
                $"must be one of [{string.Join(",", FlatBag.Kinds.Select(CanonicalText.Spelling))}]");
        }
        (string Name, bool Present)[] exclusions =
        [
            ("obstacles", container.Obstacles.Length > 0), ("axles", container.Axles is not null),
            ("access_directions", container.AccessDirections.Length > 0),
            ("max_stack_density", container.MaxStackDensity is not null),
            ("outer_dimensions", container.OuterDimensions is not null),
            ("void_fill_reserve_ratio", container.VoidFillReserveRatio > 0),
            ("made_to_measure", container.MadeToMeasure is not null),
        ];
        if (exclusions.FirstOrDefault(field => field.Present) is { Name: { } name })
        {
            throw new InvalidRequestException("not_allowed", where + RequestPointer.Of(name),
                "cannot be combined with deformable_boundary");
        }
    }

    private static void CheckMadeToMeasure(Container container, MadeToMeasure machine, string where)
    {
        var field = where + RequestPointer.Of("made_to_measure");
        var price = field + RequestPointer.Of("cost_per_square_metre_minor");
        Integer(machine.CostPerSquareMetreMinor, price, 0);
        if (container.OuterDimensions is not null)
        {
            throw new InvalidRequestException("not_allowed", where + RequestPointer.Of("outer_dimensions"),
                "cannot be combined with made_to_measure");
        }
        var largest = MadeToMeasure.BoxCostMinorFor(machine.CostPerSquareMetreMinor, container.CostMinor,
            Triple.Of(container.InnerDimensions));
        if (largest > MaxExactMagnitude)
        {
            throw new InvalidRequestException("invalid_value", price,
                $"prices the largest box above {MaxExactMagnitude.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>
    /// The fixed-placement shape rules an object can break, in the JSON path's order and with its
    /// refusal; whether the set can hold is the admission's to say. A point holds non-negative ticks,
    /// so its coordinates need no rule here.
    /// </summary>
    private static void CheckFixedPlacement(FixedPlacement placement, int index)
    {
        var where = $"fixed_placements[{index.ToString(CultureInfo.InvariantCulture)}]";
        var field = RequestPointer.Of("fixed_placements", index);
        foreach (var (name, value) in (ReadOnlySpan<(string, string?)>)
                 [("item_type", placement.ItemId), ("container_type", placement.ContainerId)])
        {
            if (string.IsNullOrEmpty(value))
            {
                throw Malformed($"{where}.{name} is a non-empty string", field + RequestPointer.Of(name));
            }
        }
        if (!Enum.IsDefined(placement.Rotation))
        {
            throw Malformed($"{where}.orientation {Rotations.VocabularyRule}", field + RequestPointer.Of("orientation"));
        }
        if (placement.ContainerInstance is < 1 or > MaxExactMagnitude)
        {
            throw Malformed($"{where}.container_instance counts from 1", field + RequestPointer.Of("container_instance"));
        }
    }

    private static FixedPlacementException Malformed(string detail, string field) => new(detail, "malformed", field);

    /// <summary>The later of two equal ids is named, as on the JSON path.</summary>
    private static void RequireUniqueIds(IEnumerable<string> ids, string key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidRequestException("duplicate_id", RequestPointer.Of(key, index, "id"),
                    $"repeats the id {CanonicalText.Spelling(id)}");
            }
            index++;
        }
    }

    private static void StackingGroupName(string name, string field)
    {
        if (name.Length == 0)
        {
            throw new InvalidRequestException("invalid_value", field, "must not be empty");
        }
    }

    private static void Optional(long? value, string field, long minimum)
    {
        if (value is { } number)
        {
            Integer(number, field, minimum);
        }
    }

    /// <summary>The JSON rule for an integer: below its floor, or past 2^53 - 1, which not every engine holds exactly.</summary>
    private static void Integer(long value, string field, long minimum)
    {
        if (value < minimum)
        {
            throw new InvalidRequestException("below_minimum", field,
                $"must be at least {minimum.ToString(CultureInfo.InvariantCulture)}");
        }
        if (value > MaxExactMagnitude)
        {
            throw new InvalidRequestException("above_maximum", field,
                $"must be at most {MaxExactMagnitude.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void Ratio(double value, string field)
    {
        if (value < 0)
        {
            throw new InvalidRequestException("below_minimum", field, "must be at least 0");
        }
        if (value > 1)
        {
            throw new InvalidRequestException("above_maximum", field, "must be at most 1");
        }
    }
}
