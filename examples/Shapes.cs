using System;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Shapes: items that do not fill their box.
///
/// Run it:
///
///     dotnet run --project examples -- shapes
///
/// <c>convex_hull</c> says how much of an item's box is solid, and the collision test is an
/// exact integer separating-axis test on the vertices. <c>compressible</c> says how much of
/// its height an item may give up under the load above it, and where it is crushed instead.
/// Both are opt-in, and refused rather than approximated wherever an engine cannot honour them.
/// </summary>
public static class Shapes
{
    private const string LowerWedge = """
        [{"x": "0", "y": "0", "z": "0"}, {"x": "100", "y": "0", "z": "0"}, {"x": "0", "y": "100", "z": "0"},
         {"x": "0", "y": "0", "z": "100"}, {"x": "100", "y": "0", "z": "100"}, {"x": "0", "y": "100", "z": "100"}]
        """;

    private const string UpperWedge = """
        [{"x": "100", "y": "100", "z": "0"}, {"x": "100", "y": "0", "z": "0"}, {"x": "0", "y": "100", "z": "0"},
         {"x": "100", "y": "100", "z": "100"}, {"x": "100", "y": "0", "z": "100"}, {"x": "0", "y": "100", "z": "100"}]
        """;

    public static void Run()
    {
        // Two triangular prisms cut from one 100 mm cube along the diagonal. As cuboids the
        // second has nowhere to go; as hulls they are complementary halves of one crate.
        Console.WriteLine("convex_hull -- two complementary wedges cut from one cube");
        Summarise("as cuboids", Request($"[{Wedge("wedge-lower", null)}, {Wedge("wedge-upper", null)}]", Crate("100", "100", "100")));
        Summarise("as hulls", Request($"[{Wedge("wedge-lower", LowerWedge)}, {Wedge("wedge-upper", UpperWedge)}]", Crate("100", "100", "100")));

        // compression_ratio is how much of its own height an item may lose under load; past
        // max_compression_pressure_kpa it is crushed and the load is refused. must_be_on_floor
        // keeps the cushion underneath, or the feature never engages.
        Console.WriteLine("\ncompressible -- a cushion that yields to the load above it");
        Load("brick 101 kg", 101);
        Load("brick 102 kg", 102);
    }

    private static void Summarise(string label, string request)
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse(request));
        var containers = ((JsonArray)result.Require("containers")).Items.Cast<JsonObject>().ToList();
        var placed = containers.Sum(c => ((JsonArray)c.Require("placements")).Items.Length);
        var refused = ((JsonArray)result.Require("unpacked_items")).Items.Length;
        Console.WriteLine($"  {label,-22} {((JsonString)result.Require("status")).Value,-10} {containers.Count} container(s), {placed} placed, {refused} refused");
    }

    private static void Load(string label, int kilograms)
    {
        var cushion = """
            {"id": "cushion", "quantity": 1, "dimensions": {"length": "100", "width": "100", "height": "100"},
             "weight": {"value": "2", "unit": "kg"}, "must_be_on_floor": true, "shape_type": "compressible",
             "compression_ratio": 0.25, "max_compression_pressure_kpa": 100}
            """;
        var brick = "{\"id\": \"brick\", \"quantity\": 1, \"dimensions\": " + Cube
                    + ", \"weight\": {\"value\": \"" + kilograms + "\", \"unit\": \"kg\"}}";
        var result = JsonApi.PackFromJson(JsonReader.Parse(Request($"[{cushion}, {brick}]", Crate("100", "100", "200"))));
        var unused = ((JsonInteger)((JsonArray)result.Require("score")).Items[3]).Value;
        Console.WriteLine($"  {label,-22} {((JsonArray)result.Require("containers")).Items.Length} container(s), unused volume {unused} ppm");
    }

    private const string Cube = "{\"length\": \"100\", \"width\": \"100\", \"height\": \"100\"}";

    private static string Wedge(string id, string? vertices) =>
        "{\"id\": \"" + id + "\", \"quantity\": 1, \"dimensions\": " + Cube + ", \"weight\": {\"value\": \"1\", \"unit\": \"kg\"}"
        + (vertices is null ? "" : ", \"shape_type\": \"convex_hull\", \"hull_vertices\": " + vertices) + "}";

    private static string Crate(string length, string width, string height) =>
        "[{\"id\": \"crate\", \"inner_dimensions\": {\"length\": \"" + length + "\", \"width\": \"" + width
        + "\", \"height\": \"" + height + "\"}}]";

    /// <summary>
    /// An example must not change answer merely because the host was busy. These solves need a
    /// fraction of the budget; the generous wall-clock value is only a safety fuse, so a loaded
    /// machine cannot cut the multi-start portfolio short and let a different start win.
    /// </summary>
    private const string SafetyFuse = "\"configuration\": {\"time_limit_ms\": 60000}";

    private static string Request(string items, string containers) =>
        "{\"units\": {\"length\": \"mm\"}, " + SafetyFuse + ", \"items\": " + items + ", \"containers\": " + containers + "}";
}
