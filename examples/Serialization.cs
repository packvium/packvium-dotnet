using System;
using System.Linq;
using Packvium.Objectives;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Serialization: the JSON contract every engine shares, and what it refuses.
///
/// Run it:
///
///     dotnet run --project examples -- serialization
///
/// A request is a JSON document and so is the result. <c>JsonApi.PackFromJson</c> is the
/// same entry point the command line uses, which is how the cross-language conformance
/// harness talks to every engine.
/// </summary>
public static class Serialization
{
    // The whole vocabulary in miniature: units, solver configuration, items with rules and
    // a container with a carrier rate card.
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {
            "objective": "lowest_landed_cost",
            "dimensional_weight_divisor": 5000,
            "dimensional_weight_length_unit": "cm",
            "dimensional_weight_weight_unit": "kg",
            "profile": "balanced",
            "seed": 42,
            "top_k": 2
          },
          "items": [
            {"id": "book", "quantity": 6, "dimensions": {"length": "210", "width": "140", "height": "30"}, "weight": "450 g"},
            {"id": "mug", "quantity": 2, "dimensions": {"length": "100", "width": "100", "height": "120"}, "weight": "380 g",
             "keep_upright": true, "max_top_load": "1 kg"}
          ],
          "containers": [
            {"id": "box-m", "inner_dimensions": {"length": "400", "width": "300", "height": "250"}, "max_payload": "20 kg",
             "cost_minor": 180,
             "rate_table": {"weight_brackets_g": [5000, 10000, 30000], "prices_minor": [890, 1240, 2050],
                            "minimum_charge_minor": 650, "fuel_surcharge_permille": 78}}
          ]
        }
        """;

    public static void Run()
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse(Request));

        // The result is deliberately verbose: exact coordinates for every placement, a
        // structured reason for every item left behind, and which solver won.
        var algorithm = (JsonObject)result.Require("algorithm");
        var containers = ((JsonArray)result.Require("containers")).Items.Cast<JsonObject>().ToList();
        Console.WriteLine($"status      {Text(result, "status")}");
        Console.WriteLine($"score       {Python.IntegerList(Integers(result.Require("score")))}   <- lexicographic, exact integers, cheapest first");
        Console.WriteLine($"solver      {Text(algorithm, "solver")} in {((JsonInteger)algorithm.Require("duration_ms")).Value} ms");
        Console.WriteLine($"containers  {Python.List(containers.Select(c => Text(c, "container_type")))}");
        Console.WriteLine($"placed      {containers.Sum(c => ((JsonArray)c.Require("placements")).Items.Length)}");
        Console.WriteLine($"unplaced    [{string.Join(", ", ((JsonArray)result.Require("unpacked_items")).Items.Cast<JsonObject>()
            .Select(u => $"('{Text(u, "item_id")}', '{Text(u, "reason")}')"))}]");

        var first = ((JsonArray)containers[0].Require("placements")).Items[0];
        Console.WriteLine();
        Console.WriteLine("one placement, in full:");
        Console.WriteLine($"{PythonJson.Dumps(first, sortKeys: false, indent: 2)[..400]} ...");

        // Runners-up are real alternative arrangements, already scored and validated. Two can
        // share a score and still differ in geometry, so compare placements, not scores.
        var alternatives = ((JsonArray)result.Require("alternatives")).Items.Cast<JsonObject>().ToList();
        Console.WriteLine();
        Console.WriteLine($"alternatives: {alternatives.Count}");
        foreach (var alternative in alternatives)
        {
            var positions = ((JsonArray)alternative.Require("containers")).Items.Cast<JsonObject>()
                .SelectMany(c => ((JsonArray)c.Require("placements")).Items.Cast<JsonObject>())
                .Select(p => $"('{Text(p, "item_id")}', '{Text((JsonObject)((JsonObject)p.Require("position")).Require("x"), "value")}')")
                .Take(2);
            Console.WriteLine($"   score {Python.IntegerList(Integers(alternative.Require("score")))} first two placements [{string.Join(", ", positions)}]");
        }

        // A key the reader does not recognise is ignored -- misspell keep_upright and the mug is
        // silently unrotated. The strictness lives in the request JSON Schema, which ships with
        // the project; validate against it if you want typo protection.
        Console.WriteLine();
        var typo = Request.Replace("\"keep_upright\": true", "\"keep_upright\": true, \"keep_uprght\": true", StringComparison.Ordinal);
        Console.WriteLine($"misspelled field: {Text(JsonApi.PackFromJson(JsonReader.Parse(typo)), "status")} -- accepted; the misspelling is invisible to the parser,");
        Console.WriteLine("                  so `keep_upright` was never applied to the mug");

        // An unknown value where the engine has to choose a behaviour is refused: there is no
        // sensible default for "rank by something I have never heard of".
        var badObjective = Request.Replace("\"lowest_landed_cost\"", "\"cheapest\"", StringComparison.Ordinal);
        try
        {
            JsonApi.PackFromJson(JsonReader.Parse(badObjective));
        }
        catch (UnknownObjectiveException refusal)
        {
            Console.WriteLine($"unknown objective: {refusal.Message[..100]}");
        }
        Console.WriteLine("fields this engine refuses by name: none");

        // The same document drives the command line, which reads a request on stdin and
        // writes a result on stdout.
        Console.WriteLine();
        Console.WriteLine("the CLI takes exactly the document above:  echo '...' | Packvium.Cli");
    }

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;

    private static System.Collections.Generic.IEnumerable<long> Integers(JsonValue value) =>
        ((JsonArray)value).Items.Select(item => (long)((JsonInteger)item).Value);
}
