using System;
using System.Linq;
using Packvium.Serialization;
using Node = System.Text.Json.Nodes.JsonObject;
using NodeArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;

namespace Packvium.Examples;

/// <summary>
/// Fleet and vehicle limits: what may go where, and how much of it.
///
/// Run it:
///
///     dotnet run --project examples -- limits
///
/// A same-day courier has one refrigerated van and a few cargo bikes. Every rule below is a
/// request field, so it holds in every Packvium engine and travels with the request:
///
/// - <c>obstacles</c> on a container: space inside it that cargo may not use -- here the fridge
///   unit under the roof, and the two wheel arches as one obstacle made of two boxes
///   (<c>additional_boxes</c>), since an arch on each side is one thing to the person loading.
/// - <c>max_items</c> on a container: a cap on how many items it takes, whatever their size -- a
///   bike's rack holds four parcels however small they are.
/// - <c>tags</c> and <c>tag_limits</c>: at most one item tagged <c>dangerous_goods</c> in the van.
/// - <c>eligible_container_tags</c> on an item: frozen goods only go in a container tagged
///   <c>refrigerated</c>.
/// - <c>max_containers</c> in the configuration: how many vehicles the whole plan may use.
/// </summary>
public static class Limits
{
    // Counted work decides the answer, not the clock (see Reproducibility.cs); the time limit is a
    // fuse far above what this solve needs.
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {"time_limit_ms": 60000, "effort_budget": {"max_restarts": 8}},
          "items": [
            {"id": "frozen-tote", "quantity": 3, "weight": "15 kg",
             "dimensions": {"length": "600", "width": "400", "height": "300"},
             "eligible_container_tags": ["refrigerated"]},
            {"id": "lithium-battery", "quantity": 3, "weight": "12 kg",
             "dimensions": {"length": "400", "width": "300", "height": "250"},
             "tags": ["dangerous_goods"]},
            {"id": "parcel", "quantity": 24, "weight": "8 kg",
             "dimensions": {"length": "500", "width": "400", "height": "400"}}
          ],
          "containers": [
            {"id": "cargo-bike", "quantity": 3, "cost_minor": 900, "max_items": 4, "max_payload": "100 kg",
             "inner_dimensions": {"length": "1000", "width": "800", "height": "900"}},
            {"id": "reefer-van", "quantity": 1, "cost_minor": 4000,
             "inner_dimensions": {"length": "1800", "width": "1400", "height": "1200"},
             "tags": ["refrigerated"],
             "tag_limits": {"dangerous_goods": 1},
             "obstacles": [
               {"id": "fridge-unit", "origin": {"x": "0", "y": "0", "z": "800"},
                "dimensions": {"length": "400", "width": "1400", "height": "400"}},
               {"id": "wheel-arches", "origin": {"x": "700", "y": "0", "z": "0"},
                "dimensions": {"length": "800", "width": "200", "height": "350"},
                "additional_boxes": [{"origin": {"x": "700", "y": "1200", "z": "0"},
                                      "dimensions": {"length": "800", "width": "200", "height": "350"}}]}
             ]}
          ]
        }
        """;

    public static void Run()
    {
        Python.Section("1. The plan");
        var plan = Solve();
        Show(plan);
        Python.Say("",
            "All three frozen totes are in the van, because nothing else is refrigerated. Only",
            "one battery rides in the van; the other two go by bike. The bike takes four items,",
            "its `max_items`, although there is room and payload for more.");

        Python.Section("2. What the obstacles cost");
        var openVan = Solve(request => ((Node)request["containers"]![1]!).Remove("obstacles"));
        Console.WriteLine($"  parcels in the van with the fridge unit and arches: {Count(plan, "reefer-van#1", "parcel")}");
        Console.WriteLine($"  parcels in the van if it were an empty box:        {Count(openVan, "reefer-van#1", "parcel")}");
        Python.Say("",
            "An obstacle is exact space, not a hint. Model what is really there, or the plan",
            "will put a parcel where the wheel arch is.");

        Python.Section("3. `max_containers`: the plan may use one vehicle");
        Show(Solve(request => request["configuration"]!["max_containers"] = 1));
        Python.Say("",
            "The cap is honoured, and what does not fit is listed rather than silently sent in",
            "a vehicle you did not allow. The reason is `observed`, not `proven`: the search",
            "found no room for these, which is not a claim that no arrangement has room.");

        Python.Section("4. `eligible_container_tags`: the van is off the road today");
        Show(Solve(request => ((NodeArray)request["containers"]!).RemoveAt(1)));
        Python.Say("",
            "The frozen totes cannot go anywhere, and their reason is `proven`: no container in",
            "the request carries the tag they need, which is a fact about the request rather",
            "than about how far the search got. The rest is `observed`: three bikes of four",
            "items each were all the request offered, and the search ran out of them.");
    }

    private static JsonObject Solve(Action<Node>? change = null)
    {
        var request = (Node)JsonNode.Parse(Request)!;
        change?.Invoke(request);
        return JsonApi.PackFromJson(JsonReader.Parse(request.ToJsonString()));
    }

    private static void Show(JsonObject result)
    {
        Console.WriteLine($"  status: {Text(result, "status")}");
        foreach (var container in Objects(result, "containers"))
        {
            var contents = Objects(container, "placements").GroupBy(placement => Text(placement, "item_type"))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Count()} {group.Key}");
            Console.WriteLine($"    {Text(container, "id"),-14} {string.Join(", ", contents)}");
        }
        // One line per kind of refusal: a reader needs "five parcels, no room", not five lines.
        var refusals = (result.Get("unpacked_items") is JsonArray ? Objects(result, "unpacked_items") : [])
            .GroupBy(unpacked => (Name: Text(unpacked, "item_type"), Reason: Text(unpacked, "reason"),
                Level: Text((JsonObject)unpacked.Require("proof"), "level")))
            .OrderBy(group => group.Key.Name, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Level, StringComparer.Ordinal);
        foreach (var group in refusals)
        {
            Console.WriteLine($"    left out: {group.Count()} {group.Key.Name}, {group.Key.Reason} ({group.Key.Level})");
        }
    }

    private static int Count(JsonObject result, string containerId, string itemType) =>
        Objects(result, "containers").Where(container => Text(container, "id") == containerId)
            .SelectMany(container => Objects(container, "placements"))
            .Count(placement => Text(placement, "item_type") == itemType);

    private static JsonObject[] Objects(JsonObject value, string key) =>
        [.. ((JsonArray)value.Require(key)).Items.Cast<JsonObject>()];

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;
}
