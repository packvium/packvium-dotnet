using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Artifacts;
using Packvium.Revisions;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Replan a half-loaded job without losing what was already done, or the record of why.
///
/// Run it:
///
///     dotnet run --project examples -- revisions
///
/// A plan is approved, and the dock starts loading. Then something changes: a slab is missing
/// from the shelf, and a cube that is already in the tote must stay exactly where it is. The next
/// plan has to keep the cube in place and pack around it, and anyone auditing the job later has
/// to see what changed, in what order, and which approved plan each change was recorded against.
///
/// A plan revision is that record. It is append-only and hash-chained: each revision names its
/// parent and the artifact it replaced by SHA-256, and carries the request the events produce.
/// The replan is an ordinary solve of that request, and every Packvium engine computes the same
/// revision bytes from the same inputs.
/// </summary>
public static class Revisions
{
    /// <summary>
    /// Counted work decides where the search stops, not a clock: a plan the clock stopped could not
    /// be replayed, and its revision would say <c>replay: not_guaranteed</c>. The time limit is only
    /// a fuse, far above what this needs even on a slow or emulated host.
    /// </summary>
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {"effort_budget": {"max_search_nodes": 20000}, "time_limit_ms": 60000},
          "items": [
            {"id": "cube", "quantity": 4, "weight": "1 kg", "dimensions": {"length": "100", "width": "100", "height": "100"}},
            {"id": "slab", "quantity": 2, "weight": "2 kg", "dimensions": {"length": "200", "width": "100", "height": "50"}}
          ],
          "containers": [{"id": "tote", "quantity": 2, "inner_dimensions": {"length": "200", "width": "200", "height": "150"}}]
        }
        """;

    private static readonly string Rule = new('=', 78);

    private static readonly string[] Axes = ["x", "y", "z"];

    public static void Run()
    {
        Section("1. The approved plan, and the revision that records it");
        var root = PlanRevision.Root(JsonReader.Parse(Request));
        var approved = Approve(root.Require("request"));
        var firstStep = Get(Objects(Objects(Get(approved, "plan"), "containers")[0], "steps")[0], "placement");
        Console.WriteLine($"  revision {Number(root.Require("revision"))}, parent {Describe(root.Require("parent"))}");
        Console.WriteLine($"  first step: a {Text(firstStep.Require("item_type"))} at x={Number(Get(firstStep, "position_ticks").Require("x"))} in container 0");
        Say("",
            "The root records nothing against the request. It exists so the first change has a",
            "parent to name.");

        Section("2. What happened on the dock, recorded against that plan");
        var loaded = Objects(Objects(JsonApi.PackFromJson(root.Require("request")), "containers")[0], "placements")[0];
        var position = Get(loaded, "position");
        var events = JsonReader.Parse($$$"""
            [{"sequence": 1, "type": "placement_locked", "placement": {
                "item_type": "cube", "container_type": "tote", "container_instance": 1,
                "position": {{{Positions(position)}}}, "orientation": "{{{Text(loaded.Require("orientation"))}}}"}},
             {"sequence": 2, "type": "item_missing", "item_type": "slab", "quantity": 1}]
            """);
        var revision = PlanRevision.Derive(root, approved, events);
        var record = Get(revision, "approved");
        var request = Get(revision, "request");
        Console.WriteLine($"  revision {Number(revision.Require("revision"))}, parent {Text(revision.Require("parent"))[..23]}...");
        Console.WriteLine($"  approved artifact {Text(record.Require("artifact"))[..23]}..., replay {Text(Get(record, "replay").Require("level"))}");
        Console.WriteLine($"  slabs still to pack: {Number(Objects(request, "items")[1].Require("quantity"))}");
        Console.WriteLine($"  fixed placements:    {Objects(request, "fixed_placements").Count}");
        Say("",
            "The events are applied to the request, not to the result: one slab fewer, and the",
            "cube becomes a fixed placement. The revision names its parent and the artifact it",
            "replaces by SHA-256 over their RFC 8785 bytes, so every engine computes the same",
            "digest.");

        Section("3. The replan keeps the cube where it is");
        var replanned = Objects(JsonApi.PackFromJson(request), "containers");
        var fixedItems = replanned.SelectMany(container => Objects(container, "placements")
            .Where(placement => placement.Get("fixed") is JsonBool { Value: true })
            .Select(placement => $"{Text(placement.Require("item_id"))} in {Text(container.Require("id"))}"));
        Console.WriteLine($"  fixed in the answer: {string.Join(", ", fixedItems)}");
        Console.WriteLine($"  items placed:        {replanned.Sum(container => Objects(container, "placements").Count)}");
        Say("",
            "An ordinary solve of the derived request, with nothing remembered from the first",
            "one. The fixed cube is marked `fixed: true`, and the validator refuses any answer",
            "that moves it.");

        Section("4. An audit that notices tampering, and a refusal instead of a guess");
        var chain = new JsonArray([root, revision]);
        Console.WriteLine($"  intact chain: {Codes(PlanRevision.VerifyChain(chain, new JsonArray([JsonNull.Instance, approved])))}");
        var slabs = Objects(request, "items")[1].With("quantity", new JsonInteger(2));
        var edited = revision.With("request", request.With("items", new JsonArray([Objects(request, "items")[0], slabs])));
        Console.WriteLine($"  edited chain: {Codes(PlanRevision.VerifyChain(new JsonArray([root, edited])))}");
        try
        {
            PlanRevision.Derive(revision, Approve(request),
                JsonReader.Parse("""[{"sequence": 3, "type": "item_missing", "item_type": "pallet", "quantity": 1}]"""));
        }
        catch (PlanRevisionException error)
        {
            Console.WriteLine($"  a pallet that was never requested: refused with {error.Code}");
        }
        Say("",
            "An edited request no longer equals what its parent's request and its own events",
            "produce, and any later revision would stop naming it as a parent. An event that",
            "contradicts the request is refused by name rather than applied as a best guess.");
    }

    /// <summary>Solves a request and wraps the answer as the artifact the dock works from.</summary>
    private static JsonObject Approve(JsonValue request) => OperationalArtifact.Build(request, JsonApi.PackFromJson(request));

    /// <summary>The placement's position as its rendered values, the form a dock reads off the result.</summary>
    private static string Positions(JsonObject position) => CanonicalJsonWriter.Write(new JsonObject(
        Axes.Select(axis => new KeyValuePair<string, JsonValue>(axis, Get(position, axis).Require("value")))));

    private static string Codes(List<RevisionIssue> issues) =>
        issues.Count == 0 ? "no issues" : string.Join(", ", issues.Select(issue => issue.Code));

    private static string Describe(JsonValue value) => value is JsonString text ? text.Value : "null";

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(Rule);
        Console.WriteLine(title);
        Console.WriteLine(Rule);
    }

    private static void Say(params string[] lines)
    {
        foreach (var line in lines)
        {
            Console.WriteLine(line.Length == 0 ? string.Empty : $"  {line}");
        }
    }

    private static JsonObject Get(JsonObject value, string key) => (JsonObject)value.Require(key);

    private static List<JsonObject> Objects(JsonObject value, string key) =>
        [.. ((JsonArray)value.Require(key)).Items.Cast<JsonObject>()];

    private static string Text(JsonValue value) => ((JsonString)value).Value;

    private static string Number(JsonValue value) => ((JsonInteger)value).Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
