using System;
using System.Linq;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Units;
using Node = System.Text.Json.Nodes.JsonObject;
using NodeArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;

namespace Packvium.Examples;

/// <summary>
/// Pack a trailer that is already partly loaded.
///
/// Run it:
///
///     dotnet run --project examples -- fixed_placements
///
/// A trailer arrives at the depot with four pallets of returns already on board. They are staying
/// on for the next leg, and nobody is going to unload them to make the plan tidier. The depot has
/// fourteen store pallets to add.
///
/// <c>fixed_placements</c> describes what is already there: which item, in which container, at
/// which position and orientation -- exactly the way a result reports a placement, so a placement
/// from an earlier plan can be fixed by quoting it. Fixed items are real cargo: they carry weight,
/// count against payload and can support what is stacked on them. The engine packs around them,
/// never moves them, and marks them <c>fixed: true</c> in the result.
///
/// A fixed item is also an instance of its item type: <c>quantity</c> counts the whole order, and
/// the fixed entries take its first instances.
/// </summary>
public static class FixedPlacements
{
    // Counted work decides the answer, not the clock (see Reproducibility.cs); the time limit is a
    // fuse far above what this solve needs. Three returns pallets across the nose, and one turned
    // behind them.
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {"time_limit_ms": 60000, "effort_budget": {"max_restarts": 8}},
          "items": [
            {"id": "returns-pallet", "quantity": 4, "weight": "350 kg", "keep_upright": true, "stackable": false,
             "dimensions": {"length": "1200", "width": "800", "height": "1500"}},
            {"id": "store-pallet", "quantity": 14, "weight": "500 kg", "keep_upright": true,
             "dimensions": {"length": "1200", "width": "800", "height": "1100"}}
          ],
          "containers": [{"id": "trailer", "max_payload": "9000 kg",
                          "inner_dimensions": {"length": "7200", "width": "2400", "height": "2400"}}],
          "fixed_placements": [
            {"item_type": "returns-pallet", "container_type": "trailer", "orientation": "LWH", "position": {"x": "0", "y": "0", "z": "0"}},
            {"item_type": "returns-pallet", "container_type": "trailer", "orientation": "LWH", "position": {"x": "0", "y": "800", "z": "0"}},
            {"item_type": "returns-pallet", "container_type": "trailer", "orientation": "LWH", "position": {"x": "0", "y": "1600", "z": "0"}},
            {"item_type": "returns-pallet", "container_type": "trailer", "orientation": "WLH", "position": {"x": "1200", "y": "0", "z": "0"}}
          ]
        }
        """;

    public static void Run()
    {
        Python.Section("1. Packed around the pallets already on board");
        Show(Solve());
        Python.Say("",
            "The returns pallets keep their ids, positions and orientations. They took the",
            "first four instances of `returns-pallet`, so none are left for the engine to",
            "place; ask for five and it would place one more.");

        Python.Section("2. One pallet was left in the middle of the floor");
        Show(Solve(request => Fixed(request, 3)["position"] = Position("1200", "400")));
        Python.Say("",
            "The fourth returns pallet stands 400 mm off the side wall, and no pallet fits in",
            "the strip it leaves. The plan still takes all fourteen store pallets, but it has",
            "to put one on top of another to do it. Whether that is acceptable is",
            "your call; the plan shows you the price of not moving that pallet.");

        Python.Section("3. What is on board cannot be where the record says");
        foreach (var (label, change) in new (string, Action<Node>)[]
                 {
                     ("two pallets recorded in the same place", request => Fixed(request, 3)["position"] = Position("600", "0")),
                     ("a 1000 kg trailer limit, already exceeded by the returns",
                         request => request["containers"]![0]!["max_payload"] = "1000 kg"),
                 })
        {
            try
            {
                Solve(change);
            }
            catch (FixedPlacementException refusal)
            {
                Console.WriteLine($"  {label}:");
                Console.WriteLine($"    {refusal.Reason}, {refusal.Field}: {refusal.Message}");
            }
        }
        Python.Say("",
            "Nothing is solved when the starting point is impossible: a plan built on a",
            "collision or an overweight trailer would be wrong before the first pallet moved.",
            "`cannot_hold` names the fixed set as a whole; the detail says what is wrong.");
    }

    private static JsonObject Solve(Action<Node>? change = null)
    {
        var request = (Node)JsonNode.Parse(Request)!;
        change?.Invoke(request);
        return JsonApi.PackFromJson(JsonReader.Parse(request.ToJsonString()));
    }

    private static JsonNode Fixed(Node request, int index) => ((NodeArray)request["fixed_placements"]!)[index]!;

    private static Node Position(string x, string y) => new() { ["x"] = x, ["y"] = y, ["z"] = "0" };

    private static void Show(JsonObject result)
    {
        var trailer = (JsonObject)((JsonArray)result.Require("containers")).Items[0];
        var placements = ((JsonArray)trailer.Require("placements")).Items.Cast<JsonObject>().ToList();
        var isFixed = placements.ToLookup(placement => placement.Get("fixed") is JsonBool { Value: true });
        var payload = new Weight((long)((JsonInteger)((JsonObject)trailer.Require("payload_weight")).Require("ticks")).Value);
        var unpacked = result.Get("unpacked_items") is JsonArray list ? list.Items.Length : 0;
        Console.WriteLine($"  status {Text(result, "status")}, payload {payload.DecimalText("kg", 1)} kg, left out {unpacked}");
        Console.WriteLine($"  already on board, unmoved: {string.Join(", ", isFixed[true].Select(p => Text(p, "item_id")))}");
        Console.WriteLine($"  added: {isFixed[false].Count()} store pallets");
        foreach (var placement in isFixed[false])
        {
            var z = Text((JsonObject)((JsonObject)placement.Require("position")).Require("z"), "value");
            if (z != "0")
            {
                Console.WriteLine($"    {Text(placement, "item_id")} is stacked, at z = {z} mm");
            }
        }
    }

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;
}
