using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Units;

namespace Packvium.Serialization;

/// <summary><c>PackingResult.to_dict</c>: the result contract, in the reference engine's field order.</summary>
public static class ResultWriter
{
    public static JsonObject Write(PackingResult result, string lengthUnit = "mm", string weightUnit = "g",
        bool includeAlternatives = true) => Object(
        ("status", Text(result.Status.Value())),
        ("feasibility", Fact(result.Feasibility)),
        ("termination", Fact(result.Termination)),
        ("optimality", Fact(result.Optimality)),
        ("complete", new JsonBool(result.Complete)),
        ("objective", Text(result.Objective)),
        ("algorithm", Algorithm(result.Algorithm)),
        ("summary", Object(
            ("container_count", Integer(result.Containers.Length)),
            ("packed_item_count", Integer(result.PackedItemCount)),
            ("unpacked_item_count", Integer(result.Unpacked.Length)))),
        ("score", new JsonArray([.. result.Score.Select(key => (JsonValue)Integer(key))])),
        ("containers", new JsonArray([.. result.Containers.Select(c => (JsonValue)Container(c, lengthUnit, weightUnit))])),
        ("unpacked_items", new JsonArray([.. result.Unpacked.Select(u => (JsonValue)Unpacked(u))])),
        ("catalog_versions_used", new JsonArray([.. result.CatalogVersionsUsed.Cast<JsonValue>()])),
        ("warnings", new JsonArray([.. result.Warnings.Select(warning => (JsonValue)Text(warning))])),
        ("alternatives", new JsonArray(includeAlternatives
            ? [.. result.Alternatives.Select(a => (JsonValue)Write(a, lengthUnit, weightUnit, false))]
            : [])));

    public static JsonObject Length(Length length, string unit) =>
        Object(("ticks", Integer(length.Ticks)), ("value", Text(length.DecimalText(unit))), ("unit", Text(unit)));

    public static JsonObject Weight(Weight weight, string unit) =>
        Object(("ticks", Integer(weight.Ticks)), ("value", Text(weight.DecimalText(unit))), ("unit", Text(unit)));

    public static JsonObject Dimensions(Dimensions dimensions, string unit) => Object(
        ("length", Length(dimensions.Length, unit)), ("width", Length(dimensions.Width, unit)),
        ("height", Length(dimensions.Height, unit)));

    public static JsonObject Point(Point point, string unit) => Object(
        ("x", Length(new Length(point.X), unit)), ("y", Length(new Length(point.Y), unit)),
        ("z", Length(new Length(point.Z), unit)));

    private static JsonObject Fact(ResultFact fact) =>
        Object([("code", Text(fact.Code)), .. fact.Attributes.Select(attribute => (attribute.Key, Attribute(attribute.Value)))]);

    private static JsonValue Attribute(object value) => value switch
    {
        bool flag => new JsonBool(flag),
        string text => Text(text),
        long number => Integer(number),
        ImmutableArray<StartRecord> starts => new JsonArray([.. starts.Select(start => (JsonValue)Start(start))]),
        JsonValue json => json,
        _ => throw new ArgumentException($"a result fact cannot carry a {value.GetType().Name}"),
    };

    private static JsonObject Start(StartRecord start) => Object(
        ("id", Text(start.Id)), ("started", new JsonBool(start.Started)), ("completed", new JsonBool(start.Completed)),
        ("truncated", new JsonBool(start.Truncated)), ("selected", new JsonBool(start.Selected)),
        ("global_deadline_reached", new JsonBool(start.GlobalDeadlineReached)));

    private static JsonObject Algorithm(AlgorithmReport algorithm) => Object(
        ("profile", Text(algorithm.Profile)), ("solver", Text(algorithm.Solver)),
        ("duration_ms", Integer(algorithm.DurationMs)), ("seed", Integer(algorithm.Seed)),
        ("time_limit_reached", new JsonBool(algorithm.TimeLimitReached)),
        ("effort_limit_reached", new JsonBool(algorithm.EffortLimitReached)),
        ("candidates_evaluated", Integer(algorithm.CandidatesEvaluated)),
        ("placements_attempted", Integer(algorithm.PlacementsAttempted)),
        ("metrics", Object(
            ("candidate_points_considered", Integer(algorithm.Metrics.CandidatePointsConsidered)),
            ("orientations_considered", Integer(algorithm.Metrics.OrientationsConsidered)),
            ("feasible_candidates", Integer(algorithm.Metrics.FeasibleCandidates)),
            ("collision_checks", Integer(algorithm.Metrics.CollisionChecks)),
            ("support_checks", Integer(algorithm.Metrics.SupportChecks)),
            ("space_partitions", Integer(algorithm.Metrics.SpacePartitions)),
            ("search_nodes_expanded", Integer(algorithm.Metrics.SearchNodesExpanded)))));

    private static JsonObject Container(PackedContainer packed, string lengthUnit, string weightUnit)
    {
        var container = packed.Container;
        var fields = new List<(string, JsonValue)>
        {
            ("id", Text(packed.Id)),
            ("container_type", Text(container.Id)),
            ("inner_dimensions", Dimensions(container.InnerDimensions, lengthUnit)),
            ("outer_dimensions", Dimensions(container.OuterDimensions ?? container.InnerDimensions, lengthUnit)),
            ("payload_weight", Weight(packed.PayloadWeight, weightUnit)),
            ("gross_weight", Weight(packed.GrossWeight, weightUnit)),
            ("used_volume_ticks3", Text(packed.UsedVolume.ToString(CultureInfo.InvariantCulture))),
            ("volume_utilization", Text(DecimalRendering.FormatFixed(packed.Utilization, 6))),
            ("centre_of_mass_offset_ppm", Integer(packed.CentreOfMassOffsetPpm)),
        };
        if (container.Axles is { } axles)
        {
            var (denominator, front, rear) = AxleLoad.Reactions(axles, LoadUnit.From(packed.Placements),
                container.TareWeight.Ticks, container.InnerDimensions.Length.Ticks);
            fields.Add(("axle_reactions", Object(
                ("basis", Text("gross")),
                ("denominator", Text(denominator.ToString(CultureInfo.InvariantCulture))),
                ("front_numerator", Text(front.ToString(CultureInfo.InvariantCulture))),
                ("rear_numerator", Text(rear.ToString(CultureInfo.InvariantCulture))))));
        }
        fields.Add(("void_fill_reserve_ticks3",
            Text(SupportRatio.ReservedVolume(container).ToString(CultureInfo.InvariantCulture))));
        if (packed.LatticeSummary is { } summary)
        {
            fields.Add(("lattice_summary", Object(
                ("item_type", Text(summary.ItemType)),
                ("orientation", Text(summary.Rotation.Value())),
                ("physical_dimensions", Dimensions(summary.Physical, lengthUnit)),
                ("envelope_dimensions", Dimensions(summary.Envelope, lengthUnit)),
                ("nx", Integer(summary.Nx)),
                ("ny", Integer(summary.Ny)),
                ("layers_used", Integer(summary.LayersUsed)),
                ("layer_step", Length(new Length(summary.LayerStep), lengthUnit)),
                ("count", Integer(summary.Count)))));
        }
        fields.Add(("placements", new JsonArray([.. packed.Placements.Select(p => (JsonValue)Placement(p, lengthUnit, weightUnit))])));
        return Object([.. fields]);
    }

    private static JsonObject Placement(Placement placement, string lengthUnit, string weightUnit) => Object(
        ("item_id", Text(placement.Instance.Id)),
        ("item_type", Text(placement.Instance.Item.Id)),
        ("position", Point(placement.Position, lengthUnit)),
        ("dimensions", Dimensions(placement.Dimensions, lengthUnit)),
        ("orientation", Text(placement.Rotation.Value())),
        ("support_ratio", Text(DecimalRendering.FormatFixed(placement.SupportRatio, 6))),
        ("top_load", Weight(placement.TopLoad, weightUnit)));

    private static JsonObject Unpacked(UnpackedItem item) => Object(
        ("item_id", Text(item.Instance.Id)),
        ("item_type", Text(item.Instance.Item.Id)),
        ("reason", Text(item.Reason)),
        ("details", new JsonArray([.. item.Details.Select(detail => (JsonValue)Text(detail))])),
        ("proof", Object(
            ("level", Text(item.Proof.Level)),
            ("observations", new JsonArray([.. item.Proof.Observations.Select(observation => (JsonValue)Object(
                ("code", Text(observation.Code)),
                ("count", Integer(observation.Count)),
                ("details", new JsonArray([.. observation.Details.Select(detail => (JsonValue)Text(detail))]))))])))));

    private static JsonObject Object(params (string Key, JsonValue Value)[] members) =>
        new(members.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)));

    private static JsonString Text(string value) => new(value);

    private static JsonInteger Integer(long value) => new(value);

    private static JsonInteger Integer(Int128 value) => new(value);
}
