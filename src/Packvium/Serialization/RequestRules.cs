using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>
/// The request schema's rules over the raw JSON, before any model is built: the first violation wins, in the order every engine
/// follows -- units, configuration, items, containers -- and is refused with an
/// <see cref="InvalidRequestException"/> naming its reason, pointer and fixed detail. Every schema
/// <c>minimum</c> is a floor and no length or weight is negative. O(size of the request).
/// </summary>
public static class RequestRules
{
    private static readonly string[] SolverProfileNames = ["fast", "balanced", "quality", "exact_small"];

    private static readonly string[] ObjectiveNames =
        ["default", "lowest_cost", "shipping_cost", "lowest_landed_cost", "open_dimension_height", "maximum_value",
        "maximum_volume"];

    private static readonly string[] AccessDirectionNames = ["+x", "-x", "+y", "-y", "+z", "-z"];

    /// <summary>Every key the request schema's <c>configuration</c> declares; it sets <c>additionalProperties: false</c>.</summary>
    private static readonly HashSet<string> ConfigurationFields = new(
    [
        "alternatives", "clearance", "container_plan_beam_width", "container_plan_node_limit",
        "dimensional_weight_divisor", "dimensional_weight_length_unit", "dimensional_weight_weight_unit",
        "effort_budget", "exact_item_limit", "max_candidate_points", "max_candidates_per_item", "max_containers",
        "minimum_support_ratio", "multi_start_orders", "objective", "require_placement_coordinates", "seed",
        "solver_profile", "solvers", "time_limit_ms",
    ], StringComparer.Ordinal);

    private static readonly (string Name, int Minimum)[] ConfigurationIntegers =
    [
        ("time_limit_ms", 1), ("alternatives", 1), ("max_containers", 1), ("exact_item_limit", 1),
        ("multi_start_orders", 1), ("max_candidates_per_item", 1), ("max_candidate_points", 16),
        ("container_plan_beam_width", 1), ("container_plan_node_limit", 1), ("dimensional_weight_divisor", 1),
    ];

    private static readonly string[] EffortLimits =
        ["max_candidates_evaluated", "max_placement_attempts", "max_search_nodes", "max_restarts"];

    private static readonly HashSet<string> EffortFields = new(EffortLimits, StringComparer.Ordinal);

    private static readonly string[] Sides = ["length", "width", "height"];

    private enum Kind
    {
        Length,
        Weight,
    }

    /// <summary>Refuses the request with its first violation, or returns.</summary>
    public static void Check(JsonValue data)
    {
        var request = RequireObject(data, string.Empty);
        var unit = CheckUnits(request.GetPresent("units"));
        CheckConfiguration(request.GetPresent("configuration"), unit);
        var items = RequiredList(request, "items", string.Empty);
        for (var index = 0; index < items.Items.Length; index++)
        {
            CheckItem(items.Items[index], JsonPointer.Of("items", index), unit);
        }
        RequireUniqueIds(items, "items");
        var containers = RequiredList(request, "containers", string.Empty);
        for (var index = 0; index < containers.Items.Length; index++)
        {
            CheckContainer(containers.Items[index], JsonPointer.Of("containers", index), unit);
        }
        RequireUniqueIds(containers, "containers");
    }

    private static string CheckUnits(JsonValue? raw)
    {
        if (raw is null)
        {
            return "mm";
        }
        var length = RequireObject(raw, "/units").GetPresent("length");
        if (length is null)
        {
            return "mm";
        }
        if (length is not JsonString { Value: var text } || !Units.Length.Knows(text))
        {
            throw new InvalidRequestException("invalid_unit", "/units/length", $"has an unknown unit {JsonValues.Spelling(length)}");
        }
        return text;
    }

    private static void CheckConfiguration(JsonValue? raw, string unit)
    {
        if (raw is null)
        {
            return;
        }
        var configuration = RequireObject(raw, "/configuration");
        KnownFields(configuration, "/configuration", ConfigurationFields);
        Optional(configuration, "solver_profile", "/configuration", (value, field) => OneOf(value, field, SolverProfileNames));
        Optional(configuration, "objective", "/configuration", (value, field) => OneOf(value, field, ObjectiveNames));
        foreach (var (name, minimum) in ConfigurationIntegers)
        {
            OptionalInteger(configuration, name, "/configuration", minimum);
        }
        OptionalRatio(configuration, "minimum_support_ratio", "/configuration", 1);
        OptionalMeasure(configuration, "clearance", "/configuration", Kind.Length, unit);
        if (configuration.GetPresent("effort_budget") is { } effort)
        {
            var budget = RequireObject(effort, "/configuration/effort_budget");
            KnownFields(budget, "/configuration/effort_budget", EffortFields);
            foreach (var name in EffortLimits)
            {
                OptionalInteger(budget, name, "/configuration/effort_budget", 1);
            }
        }
    }

    private static void CheckItem(JsonValue raw, string where, string unit)
    {
        var item = RequireObject(raw, where);
        RequiredString(item, "id", where);
        OptionalInteger(item, "quantity", where, 1);
        Dimensions(Required(item, "dimensions", where), where + "/dimensions", unit);
        OptionalMeasure(item, "weight", where, Kind.Weight, "g");
        OptionalMeasure(item, "max_top_load", where, Kind.Weight, "g");
        OptionalMeasure(item, "nesting_height", where, Kind.Length, unit);
        OptionalInteger(item, "max_stacked_items", where, 1);
        OptionalInteger(item, "stop_index", where, 0);
        OptionalInteger(item, "value", where, 0);
        OptionalInteger(item, "max_compression_pressure_kpa", where, 0);
        OptionalRatio(item, "minimum_support_ratio", where, 1);
        OptionalRatio(item, "compression_ratio", where, null);
        Optional(item, "stacking_group", where, StackingGroupName);
        Optional(item, "accepts_stacking_groups", where, StackingGroupNames);
    }

    private static void StackingGroupName(JsonValue value, string field)
    {
        if (value is not JsonString { Value: var name })
        {
            throw new InvalidRequestException("wrong_type", field, "must be a string");
        }
        if (name.Length == 0)
        {
            throw new InvalidRequestException("invalid_value", field, "must not be empty");
        }
    }

    private static void StackingGroupNames(JsonValue raw, string where)
    {
        var names = RequireList(raw, where);
        for (var index = 0; index < names.Items.Length; index++)
        {
            StackingGroupName(names.Items[index], where + JsonPointer.Of(index));
        }
    }

    private static void CheckContainer(JsonValue raw, string where, string unit)
    {
        var container = RequireObject(raw, where);
        RequiredString(container, "id", where);
        OptionalInteger(container, "quantity", where, 1);
        Dimensions(Required(container, "inner_dimensions", where), where + "/inner_dimensions", unit);
        Optional(container, "outer_dimensions", where, (value, field) => Dimensions(value, field, unit));
        foreach (var name in (ReadOnlySpan<string>)["tare_weight", "max_payload", "max_stack_density"])
        {
            OptionalMeasure(container, name, where, Kind.Weight, "g");
        }
        OptionalInteger(container, "max_items", where, 1);
        OptionalInteger(container, "cost_minor", where, 0);
        OptionalRatio(container, "void_fill_reserve_ratio", where, 1);
        Optional(container, "access_directions", where, AccessDirections);
        Optional(container, "tag_limits", where, TagLimits);
        Optional(container, "rate_table", where, RateTable);
        Optional(container, "obstacles", where, (value, field) => Obstacles(value, field, unit));
        Optional(container, "deformable_boundary", where, (value, field) => DeformableBoundary(container, value, field, where));
        Optional(container, "made_to_measure", where, (value, field) => MadeToMeasureRules(container, value, field, where, unit));
    }

    /// <summary>Container fields a flat bag refuses, in reporting order.</summary>
    private static readonly string[] BagExclusions =
    [
        "obstacles", "axles", "access_directions", "max_stack_density", "outer_dimensions", "void_fill_reserve_ratio",
        "made_to_measure",
    ];

    private static readonly HashSet<string> DeformableBoundaryFields = new(["kind"], StringComparer.Ordinal);

    /// <summary>Every key the request schema's <c>made_to_measure</c> declares; it sets <c>additionalProperties: false</c>.</summary>
    private static readonly HashSet<string> MadeToMeasureFields = new(
        ["cost_per_square_metre_minor", "increment", "minimum", "outer_allowance", "tare_per_square_metre"],
        StringComparer.Ordinal);

    private static void DeformableBoundary(JsonObject container, JsonValue raw, string field, string where)
    {
        var boundary = RequireObject(raw, field);
        OneOf(Required(boundary, "kind", field), field + JsonPointer.Of("kind"), FlatBag.Kinds);
        KnownFields(boundary, field, DeformableBoundaryFields);
        foreach (var name in BagExclusions)
        {
            if (SetBesideABag(name, container.GetPresent(name)))
            {
                throw new InvalidRequestException("not_allowed", where + JsonPointer.Of(name),
                    "cannot be combined with deformable_boundary");
            }
        }
    }

    /// <summary>An empty obstacle list and a zero reserve change nothing, so only they are let through.</summary>
    private static bool SetBesideABag(string name, JsonValue? value) => name switch
    {
        "obstacles" => value is JsonArray { Items.Length: > 0 },
        "void_fill_reserve_ratio" => value switch
        {
            JsonInteger integer => integer.Value.Sign > 0,
            JsonFloat number => number.Value > 0,
            _ => false,
        },
        _ => value is not null,
    };

    /// <summary>
    /// The made-to-measure rules: shape top to bottom, then each axis, then the price.
    /// Run after every other container field, so an existing error always wins. O(1).
    /// </summary>
    private static void MadeToMeasureRules(JsonObject container, JsonValue raw, string field, string where, string unit)
    {
        var machine = RequireObject(raw, field);
        KnownFields(machine, field, MadeToMeasureFields);
        Required(machine, "minimum", field);
        Required(machine, "increment", field);
        foreach (var name in (ReadOnlySpan<string>)["minimum", "increment", "outer_allowance"])
        {
            Optional(machine, name, field, (value, at) => Dimensions(value, at, unit));
        }
        OptionalInteger(machine, "cost_per_square_metre_minor", field, 0);
        OptionalMeasure(machine, "tare_per_square_metre", field, Kind.Weight, "g");
        if (container.GetPresent("outer_dimensions") is not null)
        {
            throw new InvalidRequestException("not_allowed", where + JsonPointer.Of("outer_dimensions"),
                "cannot be combined with made_to_measure");
        }
        var inner = Ticks(container.Require("inner_dimensions"), unit);
        var minimum = Ticks(machine.Require("minimum"), unit);
        var increment = Ticks(machine.Require("increment"), unit);
        for (var axis = 0; axis < 3; axis++)
        {
            var side = Sides[axis];
            if (minimum[axis] == 0)
            {
                throw new InvalidRequestException("invalid_value", field + JsonPointer.Of("minimum", side), "must be positive");
            }
            if (increment[axis] == 0)
            {
                throw new InvalidRequestException("invalid_value", field + JsonPointer.Of("increment", side), "must be positive");
            }
            if (minimum[axis] > inner[axis])
            {
                throw new InvalidRequestException("invalid_value", field + JsonPointer.Of("minimum", side),
                    "exceeds inner_dimensions");
            }
            if ((inner[axis] - minimum[axis]) % increment[axis] != 0)
            {
                throw new InvalidRequestException("invalid_value", field + JsonPointer.Of("increment", side),
                    "does not divide inner_dimensions minus minimum");
            }
        }
        var price = JsonValues.WholeNumber(machine.GetPresent("cost_per_square_metre_minor")) ?? 0;
        var fee = JsonValues.WholeNumber(container.GetPresent("cost_minor")) ?? 0;
        if (Domain.MadeToMeasure.BoxCostMinorFor(price, fee, inner) > CanonicalJsonWriter.MaxExactMagnitude)
        {
            throw new InvalidRequestException("invalid_value", field + JsonPointer.Of("cost_per_square_metre_minor"),
                $"prices the largest box above {CanonicalJsonWriter.MaxExactMagnitude.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static Triple Ticks(JsonValue raw, string unit)
    {
        var sides = (JsonObject)raw;
        return new Triple(Measures.Length(sides.Require("length"), unit).Ticks,
            Measures.Length(sides.Require("width"), unit).Ticks, Measures.Length(sides.Require("height"), unit).Ticks);
    }

    private static void Dimensions(JsonValue raw, string where, string unit)
    {
        var sides = RequireObject(raw, where);
        foreach (var side in Sides)
        {
            Measure(Required(sides, side, where), new Field(where, side), Kind.Length, unit);
        }
    }

    /// <summary>
    /// Code-point order, as every engine walks them: a JavaScript object puts integer-like keys
    /// first, so insertion order would name a different bad tag there.
    /// </summary>
    private static void AccessDirections(JsonValue raw, string where)
    {
        var directions = RequireList(raw, where);
        for (var index = 0; index < directions.Items.Length; index++)
        {
            OneOf(directions.Items[index], where + JsonPointer.Of(index), AccessDirectionNames);
        }
    }

    /// <summary>
    /// The schema closes this object: a key it does not name is refused, never ignored. The first
    /// unknown key in code-point order is named, the order every engine can share. O(k log k).
    /// </summary>
    private static void KnownFields(JsonObject value, string where, HashSet<string> known)
    {
        var unknown = value.Keys.Where(key => !known.Contains(key)).Order(CodePointComparer.Instance).FirstOrDefault();
        if (unknown is not null)
        {
            throw new InvalidRequestException("not_allowed", where + JsonPointer.Of(unknown), "is not a known field");
        }
    }

    private static void TagLimits(JsonValue raw, string where)
    {
        var limits = RequireObject(raw, where);
        foreach (var tag in limits.Keys.Order(CodePointComparer.Instance))
        {
            Integer(limits.Require(tag), new Field(where, tag), 1);
        }
    }

    private static void RateTable(JsonValue raw, string where)
    {
        var table = RequireObject(raw, where);
        foreach (var (name, minimum) in (ReadOnlySpan<(string, int)>)[("weight_brackets_g", 1), ("prices_minor", 0)])
        {
            if (table.GetPresent(name) is not { } values)
            {
                continue;
            }
            var list = RequireList(values, where + JsonPointer.Of(name));
            for (var index = 0; index < list.Items.Length; index++)
            {
                Integer(list.Items[index], where + JsonPointer.Of(name, index), minimum);
            }
        }
        OptionalInteger(table, "minimum_charge_minor", where, 0);
        OptionalInteger(table, "fuel_surcharge_permille", where, 0);
    }

    private static void Obstacles(JsonValue raw, string where, string unit)
    {
        var entries = RequireList(raw, where);
        for (var index = 0; index < entries.Items.Length; index++)
        {
            var at = where + JsonPointer.Of(index);
            var obstacle = RequireObject(entries.Items[index], at);
            if (obstacle.GetPresent("origin") is { } origin)
            {
                var point = RequireObject(origin, at + "/origin");
                foreach (var axis in (ReadOnlySpan<string>)["x", "y", "z"])
                {
                    OptionalMeasure(point, axis, at + "/origin", Kind.Length, unit);
                }
            }
            Dimensions(Required(obstacle, "dimensions", at), at + "/dimensions", unit);
        }
    }

    // ------------------------------------------------------------------------- primitives

    private static void Integer(JsonValue value, Field field, int minimum)
    {
        if (JsonValues.WholeNumber(value) is not { } number)
        {
            RefuseNonInteger(value, field, minimum);
            return;
        }
        if (number < minimum)
        {
            throw new InvalidRequestException("below_minimum", field.ToString(), $"must be at least {minimum}");
        }
    }

    /// <summary>A whole number past 2^53 - 1 is out of range, not mistyped: say which way.</summary>
    private static void RefuseNonInteger(JsonValue value, Field field, int minimum)
    {
        BigInteger? whole = value switch
        {
            JsonInteger integer => integer.Value,
            JsonFloat number when double.IsInteger(number.Value) => new BigInteger(number.Value),
            _ => null,
        };
        if (whole is { } wholeValue && wholeValue < minimum)
        {
            throw new InvalidRequestException("below_minimum", field.ToString(), $"must be at least {minimum}");
        }
        if (whole is not null)
        {
            throw new InvalidRequestException("above_maximum", field.ToString(),
                $"must be at most {CanonicalJsonWriter.MaxExactMagnitude.ToString(CultureInfo.InvariantCulture)}");
        }
        throw new InvalidRequestException("wrong_type", field.ToString(), "must be an integer");
    }

    private static void Ratio(JsonValue value, Field field, int? maximum)
    {
        var (negative, aboveBound) = value switch
        {
            JsonInteger integer => (integer.Value.Sign < 0, maximum is { } bound && integer.Value > bound),
            JsonFloat number => (number.Value < 0, maximum is { } bound && number.Value > bound),
            _ => throw new InvalidRequestException("wrong_type", field.ToString(), "must be a number"),
        };
        if (negative)
        {
            throw new InvalidRequestException("below_minimum", field.ToString(), "must be at least 0");
        }
        if (aboveBound)
        {
            throw new InvalidRequestException("above_maximum", field.ToString(), $"must be at most {maximum}");
        }
    }

    private static void OneOf(JsonValue value, Field field, string[] allowed)
    {
        if (value is JsonString { Value: var text } && allowed.Contains(text, StringComparer.Ordinal))
        {
            return;
        }
        throw new InvalidRequestException("not_allowed", field.ToString(),
            $"must be one of {CanonicalJsonWriter.Write(new JsonArray([.. allowed.Select(name => (JsonValue)new JsonString(name))]))}");
    }

    private static JsonObject RequireObject(JsonValue value, Field field) =>
        value as JsonObject ?? throw new InvalidRequestException("wrong_type", field.ToString(), "must be an object");

    private static JsonArray RequireList(JsonValue value, Field field) =>
        value as JsonArray ?? throw new InvalidRequestException("wrong_type", field.ToString(), "must be a list");

    /// <summary>A measure is an integer, a string or <c>{value, unit}</c>, and never negative.</summary>
    private static void Measure(JsonValue value, Field field, Kind kind, string unit)
    {
        var shaped = value switch
        {
            JsonInteger or JsonString or JsonObject => true,
            JsonFloat => JsonValues.WholeNumber(value) is not null,
            _ => false,
        };
        if (!shaped)
        {
            throw new InvalidRequestException("wrong_type", field.ToString(), "must be a measure");
        }
        if (value is JsonObject measure)
        {
            if (!measure.Contains("value"))
            {
                throw new InvalidRequestException("wrong_type", field.ToString(), "must be a measure");
            }
            var stated = measure.Get("unit") ?? new JsonString(unit);
            if (stated is not JsonString { Value: var text } || !(kind == Kind.Length ? Units.Length.Knows(text) : Units.Weight.Knows(text)))
            {
                throw new InvalidRequestException("invalid_unit", field.ToString(), $"has an unknown unit {JsonValues.Spelling(stated)}");
            }
        }
        try
        {
            _ = kind == Kind.Length ? Measures.Length(value, unit).Ticks : Measures.Weight(value, unit).Ticks;
        }
        catch (Exception error) when (error is ArgumentException or ArithmeticException or FormatException
                                          or KeyNotFoundException)
        {
            if (error.Message.Contains("cannot be negative", StringComparison.Ordinal))
            {
                throw new InvalidRequestException("negative_measure", field.ToString(), "cannot be negative");
            }
            throw new InvalidRequestException("wrong_type", field.ToString(), "must be a measure");
        }
    }

    // ------------------------------------------------------------------------- plumbing

    /// <summary>
    /// The pointer of a value being checked, spelled only when a rule refuses it: a valid request
    /// pays for no pointer strings.
    /// </summary>
    private readonly record struct Field(string Where, string? Name = null)
    {
        public static implicit operator Field(string pointer) => new(pointer);

        public override string ToString() => Name is null ? Where : Where + JsonPointer.Of(Name);
    }

    private static void OptionalInteger(JsonObject container, string name, string where, int minimum)
    {
        if (container.GetPresent(name) is { } value)
        {
            Integer(value, new Field(where, name), minimum);
        }
    }

    private static void OptionalRatio(JsonObject container, string name, string where, int? maximum)
    {
        if (container.GetPresent(name) is { } value)
        {
            Ratio(value, new Field(where, name), maximum);
        }
    }

    private static void OptionalMeasure(JsonObject container, string name, string where, Kind kind, string unit)
    {
        if (container.GetPresent(name) is { } value)
        {
            Measure(value, new Field(where, name), kind, unit);
        }
    }

    /// <summary>An optional field: absent and null are the default; anything else must pass.</summary>
    private static void Optional(JsonObject container, string name, string where, Action<JsonValue, string> check)
    {
        if (container.GetPresent(name) is { } value)
        {
            check(value, where + JsonPointer.Of(name));
        }
    }

    private static JsonValue Required(JsonObject container, string name, string where) =>
        container.GetPresent(name)
        ?? throw new InvalidRequestException("missing_field", where + JsonPointer.Of(name), "is required");

    private static JsonArray RequiredList(JsonObject container, string name, string where) =>
        RequireList(Required(container, name, where), where + JsonPointer.Of(name));

    private static void RequiredString(JsonObject container, string name, string where)
    {
        if (Required(container, name, where) is not JsonString)
        {
            throw new InvalidRequestException("wrong_type", where + JsonPointer.Of(name), "must be a string");
        }
    }

    /// <summary>Checked once every entry is well formed, so the later of two equal ids is named.</summary>
    private static void RequireUniqueIds(JsonArray entries, string key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Items.Length; index++)
        {
            var identifier = ((JsonString)((JsonObject)entries.Items[index]).Require("id")).Value;
            if (!seen.Add(identifier))
            {
                throw new InvalidRequestException("duplicate_id", JsonPointer.Of(key, index, "id"),
                    $"repeats the id {CanonicalText.Spelling(identifier)}");
            }
        }
    }
}

/// <summary>RFC 6901 pointers: each part prefixed by <c>/</c>, with <c>~</c> and <c>/</c> in keys escaped.</summary>
public static class JsonPointer
{
    public static string Of(params object[] parts) => string.Concat(parts.Select(Part));

    /// <summary>One reference token: an array index, or a key with <c>~</c> and <c>/</c> escaped.</summary>
    public static string Of(string key) => Part(key);

    public static string Of(int index) => "/" + index.ToString(CultureInfo.InvariantCulture);

    private static string Part(object part)
    {
        var text = Convert.ToString(part, CultureInfo.InvariantCulture)!;
        return "/" + (text.AsSpan().IndexOfAny('~', '/') < 0
            ? text
            : text.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
    }
}

/// <summary>JSON values judged as every engine can judge them: which numbers are integers, and how a refusal quotes a value.</summary>
public static class JsonValues
{
    private static readonly BigInteger MaxExact = CanonicalJsonWriter.MaxExactMagnitude;

    /// <summary>
    /// The integer a JSON value is: <c>1.0</c> is <c>1</c>, because JavaScript cannot tell them
    /// apart once the text is parsed; <c>true</c>, <c>"1"</c>, <c>1.5</c> and anything past
    /// 2^53 - 1 are not integers.
    /// </summary>
    public static long? WholeNumber(JsonValue? value) => value switch
    {
        JsonInteger integer when BigInteger.Abs(integer.Value) <= MaxExact => (long)integer.Value,
        JsonFloat number when double.IsInteger(number.Value) && Math.Abs(number.Value) <= CanonicalJsonWriter.MaxExactMagnitude
            => (long)number.Value,
        _ => null,
    };

    /// <summary>How a refusal quotes a value: its canonical JSON, the one spelling every engine shares.</summary>
    public static string Spelling(JsonValue value)
    {
        try
        {
            return CanonicalJsonWriter.Write(value);
        }
        catch (CanonicalJsonException error)
        {
            return error.Code == "number_out_of_range" ? "an out-of-range number" : "an unspellable value";
        }
    }
}
