using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary>A request read into domain objects, plus what the result writer needs from it.</summary>
public sealed record PackingCall(
    PackingConfig Configuration,
    ImmutableArray<IPlacementConstraint> PlacementConstraints,
    ImmutableArray<Item> Items,
    ImmutableArray<Container> Containers,
    ImmutableArray<JsonObject> CatalogVersionsUsed,
    string OutputLengthUnit,
    string OutputWeightUnit);

/// <summary>
/// The JSON adapter: <c>serialization.pack_from_dict</c> up to the point it hands domain objects
/// to the packer, reading every field in the reference engine's order so the first refusal is
/// the same one.
/// </summary>
public static class RequestReader
{
    private static readonly JsonObject Empty = new([]);

    public static PackingCall Read(JsonValue document)
    {
        var data = PythonCoercion.Object(document);
        var unit = PythonCoercion.Str(PythonCoercion.Object(data.Get("units") ?? Empty).Get("length")
                                      ?? new JsonString("mm"));
        var configuration = ReadConfiguration(PythonCoercion.Object(data.Get("configuration") ?? Empty), unit);
        var references = CatalogVersionsUsed(data.Get("catalog_versions_used") ?? new JsonArray([]));
        // Rules compile into the constraint pipeline, so an illegal candidate is refused during
        // search rather than filtered out of a finished answer.
        var policy = PolicyReader.Read(data.Get("policy"));
        var items = PythonCoercion.Iterate(data.Require("items"))
            .Select(raw => ReadItem(PythonCoercion.Object(raw), unit)).ToImmutableArray();
        var containers = PythonCoercion.Iterate(data.Require("containers"))
            .Select(raw => ReadContainer(PythonCoercion.Object(raw), unit)).ToImmutableArray();
        var output = PythonCoercion.Object(data.Get("output") ?? Empty);
        return new PackingCall(configuration, policy.Constraints(), items, containers, references,
            output.Get("length_unit") is { } lengthUnit ? PythonCoercion.Str(lengthUnit) : unit,
            output.Get("weight_unit") is { } weightUnit ? PythonCoercion.Str(weightUnit) : "g");
    }

    private static PackingConfig ReadConfiguration(JsonObject cfg, string unit)
    {
        var profile = SolverProfiles.Parse(StrictString(cfg.Get("solver_profile") ?? new JsonString("balanced")));
        var quality = profile == SolverProfile.Quality;
        return new PackingConfig(
            profile,
            timeLimitMs: IntOr(cfg, "time_limit_ms", 1000),
            topK: IntOr(cfg, "alternatives", 3),
            seed: IntOr(cfg, "seed", 42),
            maxContainers: PythonCoercion.RawInt(cfg.Get("max_containers")),
            clearance: Measures.Length(cfg.Get("clearance") ?? new JsonInteger(0), unit),
            minimumSupportRatio: PythonCoercion.Float(cfg.Get("minimum_support_ratio") ?? new JsonInteger(0)),
            exactItemLimit: IntOr(cfg, "exact_item_limit", 7),
            multiStartOrders: IntOr(cfg, "multi_start_orders", 8),
            maxCandidatesPerItem: IntOr(cfg, "max_candidates_per_item", quality ? 16 : 1),
            maxCandidatePoints: IntOr(cfg, "max_candidate_points", 4096),
            solvers: PythonCoercion.StringMembers(cfg.Get("solvers") ?? new JsonArray([])).ToImmutableArray(),
            objective: PythonCoercion.Str(cfg.Get("objective") ?? new JsonString("default")),
            effortBudget: ReadEffortBudget(cfg.GetPresent("effort_budget")),
            dimensionalWeightDivisor: PythonCoercion.RawInt(cfg.Get("dimensional_weight_divisor")),
            dimensionalWeightLengthUnit: PythonCoercion.Str(cfg.Get("dimensional_weight_length_unit") ?? new JsonString("in")),
            dimensionalWeightWeightUnit: PythonCoercion.Str(cfg.Get("dimensional_weight_weight_unit") ?? new JsonString("lb")),
            requirePlacementCoordinates: PythonCoercion.Bool(cfg.Get("require_placement_coordinates") ?? new JsonBool(true)),
            containerPlanBeamWidth: IntOr(cfg, "container_plan_beam_width", quality ? 16 : 1),
            containerPlanNodeLimit: IntOr(cfg, "container_plan_node_limit", quality ? 100_000 : 1));
    }

    private static EffortBudget? ReadEffortBudget(JsonValue? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var budget = PythonCoercion.Object(raw);
        return new EffortBudget(
            PythonCoercion.RawInt(budget.Get("max_candidates_evaluated")),
            PythonCoercion.RawInt(budget.Get("max_placement_attempts")),
            PythonCoercion.RawInt(budget.Get("max_search_nodes")),
            PythonCoercion.RawInt(budget.Get("max_restarts")));
    }

    private static Item ReadItem(JsonObject raw, string unit)
    {
        var rotations = raw.Get("allowed_rotations") is { } authored
            ? PythonCoercion.Iterate(authored).Select(value => Rotations.Parse(StrictString(value))).ToImmutableArray()
            : Rotations.All;
        return new Item(
            PythonCoercion.Str(raw.Require("id")),
            Measures.Dimensions(raw.Require("dimensions"), unit),
            Measures.Weight(raw.Get("weight") ?? new JsonInteger(0)),
            quantity: checked((int)PythonCoercion.Int(raw.Get("quantity") ?? new JsonInteger(1))),
            allowedRotations: rotations,
            keepUpright: PythonCoercion.Bool(raw.Get("keep_upright") ?? new JsonBool(false)),
            stackable: PythonCoercion.Bool(raw.Get("stackable") ?? new JsonBool(true)),
            mustBeOnFloor: PythonCoercion.Bool(raw.Get("must_be_on_floor") ?? new JsonBool(false)),
            maxTopLoad: raw.GetPresent("max_top_load") is { } topLoad ? Measures.Weight(topLoad) : null,
            maxStackedItems: PythonCoercion.RawInt(raw.Get("max_stacked_items")),
            minimumSupportRatio: PythonCoercion.Float(raw.Get("minimum_support_ratio") ?? new JsonInteger(0)),
            group: OptionalString(raw.Get("group")),
            tags: PythonCoercion.StringMembers(raw.Get("tags") ?? new JsonArray([])),
            incompatibleTags: PythonCoercion.StringMembers(raw.Get("incompatible_tags") ?? new JsonArray([])),
            priority: PythonCoercion.Int(raw.Get("priority") ?? new JsonInteger(0)),
            eligibleContainerTags: PythonCoercion.StringMembers(raw.Get("eligible_container_tags") ?? new JsonArray([])),
            groundContactRule: OptionalString(raw.Get("ground_contact_rule")),
            metadata: Metadata(raw.Get("metadata")),
            nestingHeight: raw.GetPresent("nesting_height") is { } nesting ? Measures.Length(nesting, unit) : null,
            stopIndex: PythonCoercion.RawInt(raw.Get("stop_index")),
            value: PythonCoercion.RawInt(raw.Get("value")),
            shapeType: ShapeTypes.Parse(StrictString(raw.Get("shape_type") ?? new JsonString("rigid_cuboid"))),
            hullVertices: raw.GetPresent("hull_vertices") is { } vertices ? HullVertices(vertices, unit) : null,
            compressionRatioPpm: raw.GetPresent("compression_ratio") is { } ratio
                ? Compression.RatioToPpm(PythonCoercion.Float(ratio))
                : null,
            maxCompressionPressureKpa: PythonCoercion.RawInt(raw.Get("max_compression_pressure_kpa")));
    }

    /// <summary>Vertices cross the wire as non-negative offsets, read through <see cref="Units.Length"/>.</summary>
    private static ImmutableArray<Vertex> HullVertices(JsonValue raw, string unit) =>
        PythonCoercion.Iterate(raw).Select(value =>
        {
            var vertex = PythonCoercion.Object(value);
            return new Vertex(Measures.Length(vertex.Require("x"), unit).Ticks,
                Measures.Length(vertex.Require("y"), unit).Ticks, Measures.Length(vertex.Require("z"), unit).Ticks);
        }).ToImmutableArray();

    private static AxisAlignedBox Box(JsonObject raw, string unit)
    {
        var origin = PythonCoercion.Object(raw.Get("origin") ?? Empty);
        var zero = new JsonInteger(0);
        return new AxisAlignedBox(
            new Point(Measures.Length(origin.Get("x") ?? zero, unit).Ticks, Measures.Length(origin.Get("y") ?? zero, unit).Ticks,
                Measures.Length(origin.Get("z") ?? zero, unit).Ticks),
            Measures.Dimensions(raw.Require("dimensions"), unit));
    }

    private static Container ReadContainer(JsonObject raw, string unit)
    {
        var obstacles = PythonCoercion.Iterate(raw.Get("obstacles") ?? new JsonArray([])).Select(value =>
        {
            var obstacle = PythonCoercion.Object(value);
            return new Obstacle(PythonCoercion.Str(obstacle.Require("id")), Box(obstacle, unit),
                PythonCoercion.Iterate(obstacle.Get("additional_boxes") ?? new JsonArray([]))
                    .Select(box => Box(PythonCoercion.Object(box), unit)).ToImmutableArray());
        }).ToImmutableArray();
        return new Container(
            PythonCoercion.Str(raw.Require("id")),
            Measures.Dimensions(raw.Require("inner_dimensions"), unit),
            outerDimensions: raw.GetPresent("outer_dimensions") is { } outer ? Measures.Dimensions(outer, unit) : null,
            tareWeight: Measures.Weight(raw.Get("tare_weight") ?? new JsonInteger(0)),
            maxPayload: raw.GetPresent("max_payload") is { } payload ? Measures.Weight(payload) : null,
            costMinor: PythonCoercion.Int(raw.Get("cost_minor") ?? new JsonInteger(0)),
            rateTable: raw.GetPresent("rate_table") is { } table ? ReadRateTable(PythonCoercion.Object(table)) : null,
            quantity: raw.GetPresent("quantity") is { } quantity ? checked((int)PythonCoercion.Int(quantity)) : null,
            obstacles: obstacles,
            tags: PythonCoercion.StringMembers(raw.Get("tags") ?? new JsonArray([])),
            maxItems: raw.GetPresent("max_items") is { } maxItems ? PythonCoercion.Int(maxItems) : null,
            voidFillReserveRatio: PythonCoercion.Float(raw.Get("void_fill_reserve_ratio") ?? new JsonInteger(0)),
            tagLimits: PythonCoercion.Object(raw.Get("tag_limits") ?? Empty).Members
                .Select(member => new KeyValuePair<string, long>(member.Key, PythonCoercion.Int(member.Value)))
                .ToImmutableArray(),
            metadata: Metadata(raw.Get("metadata")),
            maxStackDensity: raw.GetPresent("max_stack_density") is { } density ? Measures.Weight(density) : null,
            axles: raw.GetPresent("axles") is { } axles ? ReadAxles(axles, unit) : null);
    }

    private static RateTable ReadRateTable(JsonObject raw) => new(
        PythonCoercion.Iterate(raw.Require("weight_brackets_g")).Select(PythonCoercion.Int),
        PythonCoercion.Iterate(raw.Require("prices_minor")).Select(PythonCoercion.Int),
        PythonCoercion.Int(raw.Get("minimum_charge_minor") ?? new JsonInteger(0)),
        PythonCoercion.Int(raw.Get("fuel_surcharge_permille") ?? new JsonInteger(0)));

    private static (Axle Front, Axle Rear) ReadAxles(JsonValue raw, string unit)
    {
        var pair = PythonCoercion.Iterate(raw).ToList();
        if (pair.Count != 2)
        {
            throw new ArgumentException(pair.Count < 2
                ? $"not enough values to unpack (expected 2, got {pair.Count})"
                : "too many values to unpack (expected 2)");
        }
        return (ReadAxle(PythonCoercion.Object(pair[0]), unit), ReadAxle(PythonCoercion.Object(pair[1]), unit));
    }

    private static Axle ReadAxle(JsonObject raw, string unit) => new(
        Measures.Length(raw.Require("position"), unit),
        raw.GetPresent("max_load") is { } maxLoad ? Measures.Weight(maxLoad) : null);

    /// <summary>The reference validates the shape of every catalog reference and keeps it verbatim.</summary>
    private static ImmutableArray<JsonObject> CatalogVersionsUsed(JsonValue raw)
    {
        if (raw is not JsonArray array)
        {
            throw new ArgumentException("catalog_versions_used must be an array");
        }
        string[] required = ["catalog_id", "effective_at", "resolved_at", "version"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var references = ImmutableArray.CreateBuilder<JsonObject>();
        for (var index = 0; index < array.Items.Length; index++)
        {
            if (array.Items[index] is not JsonObject reference
                || !reference.Keys.Order(StringComparer.Ordinal).SequenceEqual(required))
            {
                throw new ArgumentException(
                    $"catalog_versions_used[{index}] must contain exactly ['catalog_id', 'effective_at', 'resolved_at', 'version']");
            }
            if (reference.Require("catalog_id") is not JsonString { Value.Length: > 0 } catalogId)
            {
                throw new ArgumentException($"catalog_versions_used[{index}].catalog_id must be non-empty");
            }
            if (!seen.Add(catalogId.Value))
            {
                throw new ArgumentException(
                    $"catalog_versions_used contains ambiguous duplicate '{catalogId.Value}'");
            }
            foreach (var (field, minimum) in (ReadOnlySpan<(string, int)>)[("version", 1), ("effective_at", 0), ("resolved_at", 0)])
            {
                if (reference.Require(field) is not JsonInteger value || value.Value < minimum)
                {
                    throw new ArgumentException($"catalog_versions_used[{index}].{field} must be >= {minimum}");
                }
            }
            references.Add(reference);
        }
        return references.ToImmutable();
    }

    private static long IntOr(JsonObject raw, string key, long fallback) =>
        raw.Get(key) is { } value ? PythonCoercion.Int(value) : fallback;

    /// <summary>A value an enum constructor reads: only a string equal to a member's value is one.</summary>
    private static string StrictString(JsonValue value) =>
        value is JsonString text
            ? text.Value
            : throw new ArgumentException($"{PythonJson.Dumps(value, sortKeys: false)} is not a valid value");

    private static string? OptionalString(JsonValue? value) => value switch
    {
        null or JsonNull => null,
        JsonString text => text.Value,
        _ => throw new ArgumentException($"a {PythonCoercion.TypeName(value)} is not accepted where a string is expected"),
    };

    private static FrozenDictionary<string, object?>? Metadata(JsonValue? value) => value switch
    {
        null => null,
        JsonObject obj => obj.Members.ToFrozenDictionary(member => member.Key, member => (object?)member.Value, StringComparer.Ordinal),
        _ => throw new ArgumentException("metadata must be an object"),
    };
}
