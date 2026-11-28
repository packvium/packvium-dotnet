using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Validation.ResultChecks;

/// <summary>The request's item and container types, read independently of the engine's own model. O(request size).</summary>
internal static class RequestSpecs
{
    /// <summary>The six axis-aligned exit directions a placement could be slid out through.</summary>
    public static readonly ImmutableArray<string> ExitDirections = ["+x", "-x", "+y", "-y", "+z", "-z"];

    private static readonly ImmutableArray<JsonValue> UprightRotations = [new JsonString("LWH"), new JsonString("WLH")];

    private static readonly ImmutableArray<JsonValue> EveryRotation = [.. Geometry.Orientations.Select(code => (JsonValue)new JsonString(code))];

    public static Ordered<ItemSpec> Items(JsonValue request, string unit)
    {
        var items = new Ordered<ItemSpec>();
        foreach (var raw in Json.Items(Json.At(request, "items"), "request.items"))
        {
            var rotations = Json.Get(raw, "allowed_rotations");
            var spec = new ItemSpec
            {
                AllowedRotations = rotations is JsonNull
                    ? Json.Truthy(Json.Get(raw, "keep_upright")) ? UprightRotations : EveryRotation
                    : Json.Items(rotations, "allowed_rotations"),
                Id = Json.At(raw, "id"),
                Dimensions = TickUnits.Dimensions(Json.At(raw, "dimensions"), unit),
                Weight = TickUnits.ToTicks(Json.Get(raw, "weight", new JsonInteger(0)), "g", TickUnits.Weight),
                Quantity = Json.Get(raw, "quantity", new JsonInteger(1)),
                Stackable = Json.Truthy(Json.Get(raw, "stackable", new JsonBool(true))),
                MustBeOnFloor = Json.Truthy(Json.Get(raw, "must_be_on_floor")),
                MaxTopLoad = Measured(raw, "max_top_load", "g", TickUnits.Weight),
                MinimumSupportRatio = TickUnits.RatioOf(Json.Get(raw, "minimum_support_ratio", new JsonInteger(0))),
                Tags = TagSet.Of(Json.Get(raw, "tags", Json.EmptyList), "tags"),
                EligibleContainerTags = TagSet.Of(Json.Get(raw, "eligible_container_tags", Json.EmptyList), "eligible_container_tags"),
                MaxStackedItems = Json.Get(raw, "max_stacked_items"),
                GroundContactRule = Json.Get(raw, "ground_contact_rule"),
                NestingHeight = Measured(raw, "nesting_height", unit, TickUnits.Length),
                IncompatibleTags = TagSet.Of(Json.Get(raw, "incompatible_tags", Json.EmptyList), "incompatible_tags"),
                StopIndex = Json.Get(raw, "stop_index"),
                Value = Json.Get(raw, "value"),
                ShapeType = Json.Get(raw, "shape_type", new JsonString("rigid_cuboid")),
                HullVertices = Hull(raw, unit),
                CompressionRatioPpm = Json.Get(raw, "compression_ratio") is JsonNull ? null
                    // The solvers' ratio rule, applied once: floor(ratio * 1000000 + 0.5) on the double.
                    : PartsPerMillion.Of(TickUnits.RatioOf(Json.Get(raw, "compression_ratio"))),
                MaxCompressionPressureKpa = Json.Get(raw, "max_compression_pressure_kpa"),
                StackingGroup = Json.Get(raw, "stacking_group"),
                AcceptsStackingGroups = Json.Truthy(Json.Get(raw, "accepts_stacking_groups"))
                    ? TagSet.Of(Json.Get(raw, "accepts_stacking_groups"), "accepts_stacking_groups")
                    : TagSet.Empty,
            };
            items.Set(spec.Id, spec);
        }
        return items;
    }

    public static Ordered<ContainerSpec> Containers(JsonValue request, string unit)
    {
        var containers = new Ordered<ContainerSpec>();
        foreach (var raw in Json.Items(Json.At(request, "containers"), "request.containers"))
        {
            var outer = Json.Get(raw, "outer_dimensions");
            var machine = Json.Get(raw, "made_to_measure");
            var flatBag = Json.Get(raw, "deformable_boundary") is not JsonNull;
            var spec = new ContainerSpec
            {
                Obstacles = Obstacles(raw, unit),
                Axles = Axles(raw, unit),
                Id = Json.At(raw, "id"),
                Inner = TickUnits.Dimensions(Json.At(raw, "inner_dimensions"), unit),
                AccessDirections = AccessDirections(raw),
                Outer = outer is JsonNull ? null : TickUnits.Dimensions(outer, unit),
                CostMinor = Json.Get(raw, "cost_minor", new JsonInteger(0)),
                MaxPayload = Measured(raw, "max_payload", "g", TickUnits.Weight),
                MaxItems = Json.Get(raw, "max_items"),
                Quantity = Json.Get(raw, "quantity"),
                Tags = TagSet.Of(Json.Get(raw, "tags", Json.EmptyList), "tags"),
                TagLimits = TagLimits(raw),
                VoidFillReserveRatio = TickUnits.RatioOf(Json.Get(raw, "void_fill_reserve_ratio", new JsonInteger(0))),
                TareWeight = TickUnits.ToTicks(Json.Get(raw, "tare_weight", new JsonInteger(0)), "g", TickUnits.Weight),
                MaxStackDensity = Measured(raw, "max_stack_density", "g", TickUnits.Weight),
                RateTable = Json.Get(raw, "rate_table"),
                FlatBag = flatBag,
                ReportsAchievedExtent = flatBag || machine is not JsonNull,
                MadeToMeasure = machine is JsonNull ? null : Machine(machine, unit),
            };
            containers.Set(spec.Id, spec);
        }
        return containers;
    }

    /// <summary><c>None if raw.get(key) is None else to_ticks(...)</c>.</summary>
    private static BigInteger? Measured(JsonValue raw, string key, string unit, UnitTable table) =>
        Json.Get(raw, key) is var value && value is JsonNull ? null : TickUnits.ToTicks(value, unit, table);


    private static ImmutableArray<TickVector>? Hull(JsonValue raw, string unit)
    {
        var declared = Json.Get(raw, "hull_vertices");
        if (declared is JsonNull)
        {
            return null;
        }
        return [.. Json.Items(declared, "hull_vertices").Select(vertex => new TickVector(
            TickUnits.ToTicks(Json.At(vertex, "x"), unit, TickUnits.Length),
            TickUnits.ToTicks(Json.At(vertex, "y"), unit, TickUnits.Length),
            TickUnits.ToTicks(Json.At(vertex, "z"), unit, TickUnits.Length)))];
    }

    /// <summary>The declared exit directions, kept in <see cref="ExitDirections"/> order.</summary>
    private static ImmutableArray<string> AccessDirections(JsonValue raw)
    {
        var declared = Json.Items(Json.Get(raw, "access_directions", Json.EmptyList), "access_directions");
        foreach (var direction in declared)
        {
            Json.HashKey(direction);
        }
        return [.. ExitDirections.Where(direction => declared.Any(named => Json.IsText(named, direction)))];
    }

    private static ImmutableArray<Cuboid> Obstacles(JsonValue raw, string unit)
    {
        var boxes = ImmutableArray.CreateBuilder<Cuboid>();
        foreach (var obstacle in Json.Items(Json.Get(raw, "obstacles", Json.EmptyList), "obstacles"))
        {
            var additional = Json.Items(Json.Get(obstacle, "additional_boxes", Json.EmptyList), "additional_boxes");
            foreach (var component in additional.Insert(0, obstacle))
            {
                var origin = Json.Get(component, "origin", Json.EmptyObject);
                var sides = TickUnits.Dimensions(Json.At(component, "dimensions"), unit);
                boxes.Add(new Cuboid(
                    TickUnits.ToTicks(Json.Get(origin, "x", new JsonInteger(0)), unit, TickUnits.Length),
                    TickUnits.ToTicks(Json.Get(origin, "y", new JsonInteger(0)), unit, TickUnits.Length),
                    TickUnits.ToTicks(Json.Get(origin, "z", new JsonInteger(0)), unit, TickUnits.Length),
                    sides[0], sides[1], sides[2]));
            }
        }
        return boxes.ToImmutable();
    }

    /// <summary><c>{str(tag): int(limit)}</c>, sorted by tag as the reference iterates it.</summary>
    private static ImmutableArray<KeyValuePair<string, BigInteger>> TagLimits(JsonValue raw)
    {
        if (Json.Get(raw, "tag_limits", Json.EmptyObject) is not JsonObject declared)
        {
            throw MalformedResultException.ShapeBreak("tag_limits must be an object");
        }
        return [.. declared.Members
            .Select(member => new KeyValuePair<string, BigInteger>(member.Key, Json.PyInt(member.Value, "tag_limits")))
            .OrderBy(limit => limit.Key, CodePointComparer.Instance)];
    }

    private static ImmutableArray<Axle>? Axles(JsonValue raw, string unit)
    {
        var declared = Json.Get(raw, "axles");
        if (declared is JsonNull)
        {
            return null;
        }
        return [.. Json.Items(declared, "axles").Select(axle => new Axle(
            TickUnits.ToTicks(Json.At(axle, "position"), unit, TickUnits.Length),
            Measured(axle, "max_load", "g", TickUnits.Weight)))];
    }

    private static MachineSpec Machine(JsonValue raw, string unit)
    {
        ImmutableArray<BigInteger> Sides(string name) =>
            Json.Get(raw, name) is var value && value is JsonNull ? [0, 0, 0] : TickUnits.Dimensions(value, unit);

        var price = Json.Get(raw, "cost_per_square_metre_minor");
        var tare = Json.Get(raw, "tare_per_square_metre");
        return new MachineSpec(
            Sides("minimum"),
            Sides("increment"),
            Json.Truthy(price) ? price : new JsonInteger(0),
            tare is JsonNull ? BigInteger.Zero : TickUnits.ToTicks(tare, "g", TickUnits.Weight),
            Sides("outer_allowance"));
    }
}
