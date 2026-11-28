using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// The reference validator's checks in the reference's order. It shares no code with the engine:
/// units, geometry, support and load are recomputed here from the request and the reported
/// placements, and only the JSON value types and Python's printing rules are shared.
/// </summary>
internal static class Validator
{
    private static readonly ImmutableArray<string> RequiredKeys =
        ["status", "feasibility", "termination", "optimality", "complete", "algorithm", "summary", "containers", "unpacked_items", "score"];

    public static List<ResultIssue> Validate(JsonValue request, JsonValue result)
    {
        foreach (var key in RequiredKeys)
        {
            if (!Json.Contains(result, key))
            {
                throw MalformedResultException.ContractBreak($"result is missing {PyFormat.StringRepr(key)}");
            }
        }
        var unit = Json.Text(Json.Get(Json.Get(request, "units", Json.EmptyObject), "length", new JsonString("mm")), "units.length");
        var configuration = Json.Get(request, "configuration", Json.EmptyObject);
        var clearance = TickUnits.ToTicks(Json.Get(configuration, "clearance", new JsonInteger(0)), unit, TickUnits.Length);
        var globalSupport = TickUnits.RatioOf(Json.Get(configuration, "minimum_support_ratio", new JsonInteger(0)));
        var items = RequestSpecs.Items(request, unit);
        var containers = RequestSpecs.Containers(request, unit);

        var issues = new List<ResultIssue>();
        var lattice = new RequestContext(items, containers, clearance, []);
        var validated = Lattice.Materialize(request, result, lattice, issues);
        Accounting.CheckStructure(validated, issues);
        Facts.CheckAlgorithmMetrics(validated, issues);
        Facts.CheckEffortBudget(request, validated, issues);
        Facts.CheckResultFacts(validated, issues);
        var context = lattice with { ContainerTagRules = Accounting.ContainerTagRules(request) };
        Accounting.CheckAccounting(validated, context, issues);
        CheckInfeasibleProof(validated, issues);

        var fabricated = CheckContainers(validated, result, context, globalSupport, issues);
        CheckContainerCounts(validated, configuration, containers, issues);

        RequestRules.CheckFixedPlacements(request, validated, unit, issues);
        RequestRules.CheckGroups(request, validated, issues);
        RequestRules.CheckSolverSelection(request, validated, issues);
        ObjectiveCheck.Check(request, validated, context, fabricated, issues);
        return issues;
    }

    /// <summary>An infeasible status needs a proven rejection for every unpacked item.</summary>
    private static void CheckInfeasibleProof(JsonValue result, List<ResultIssue> issues)
    {
        if (!Json.IsText(Json.At(result, "status"), "infeasible"))
        {
            return;
        }
        foreach (var unpacked in Json.Items(Json.At(result, "unpacked_items"), "result.unpacked_items"))
        {
            if (!Json.IsText(Json.Get(Json.Get(unpacked, "proof", Json.EmptyObject), "level"), "proven"))
            {
                issues.Add(Issues.Of("unproven_infeasible", $"{"infeasible status requires a proven rejection for every unpacked item"}"));
                return;
            }
        }
    }

    /// <summary>
    /// Each packed container against its type, recomputing a made-to-measure box first; returns
    /// those recomputed boxes by container id, for the objective.
    /// </summary>
    private static Ordered<ContainerSpec> CheckContainers(JsonValue validated, JsonValue reported, RequestContext context, Ratio globalSupport,
        List<ResultIssue> issues)
    {
        var fabricated = new Ordered<ContainerSpec>();
        var originals = Json.Items(Json.At(reported, "containers"), "result.containers");
        var packedContainers = Json.Items(Json.At(validated, "containers"), "result.containers");
        for (var index = 0; index < packedContainers.Length && index < originals.Length; index++)
        {
            var packed = packedContainers[index];
            var containerType = Json.At(packed, "container_type");
            if (context.Containers.GetOrDefault(containerType) is not { } declared)
            {
                issues.Add(Issues.Of("unknown_container_type", $"{PyFormat.Str(containerType)}"));
                continue;
            }
            var spec = MadeToMeasure.Check(packed, Json.Contains(originals[index], "lattice_summary"), declared, context.Items, context.Clearance, issues);
            if (spec.MadeToMeasure is not null)
            {
                fabricated.Set(Json.At(packed, "id"), spec);
            }
            ContainerCheck.Check(packed, spec, context, globalSupport, issues);
            Extents.CheckAchievedExtent(packed, spec, context.Clearance, issues);
        }
        return fabricated;
    }

    /// <summary>Container inventory per type, and the request's cap on containers opened.</summary>
    private static void CheckContainerCounts(JsonValue validated, JsonValue configuration, Ordered<ContainerSpec> containers, List<ResultIssue> issues)
    {
        var opened = Json.Items(Json.At(validated, "containers"), "result.containers");
        var inventory = new Ordered<BigInteger>();
        foreach (var packed in opened)
        {
            var containerType = Json.At(packed, "container_type");
            if (containers.Has(containerType))
            {
                inventory.Set(containerType, (inventory.TryGet(containerType, out var count) ? count : 0) + 1);
            }
        }
        foreach (var (containerType, count) in inventory.Entries)
        {
            var quantity = containers.GetOrDefault(containerType)!.Quantity;
            if (quantity is not JsonNull && Json.CompareInt(quantity, count) < 0)
            {
                issues.Add(Issues.Of("container_inventory_exceeded", $"{PyFormat.Str(containerType)}: used {count}, available {PyFormat.Str(quantity)}"));
            }
        }
        var limit = Json.Get(configuration, "max_containers");
        if (limit is not JsonNull && Json.CompareInt(limit, opened.Length) < 0)
        {
            issues.Add(Issues.Of("max_containers_exceeded", $"{opened.Length} containers opened, limit {PyFormat.Str(limit)}"));
        }
    }
}
