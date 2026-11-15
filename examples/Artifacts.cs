using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Packvium.Artifacts;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Validation;

namespace Packvium.Examples;

/// <summary>
/// Hand a packing result to a warehouse system that has no engine.
///
/// Run it:
///
///     dotnet run --project examples -- artifacts
///
/// An execution plan says what to lift first. A warehouse or transport system needs more than
/// that before it can act on its own: how big each box is, what the load weighs, a sheet to print
/// for the dock, and a record of which request and solver produced all of it. Without those, it
/// has to call the engine again.
///
/// The operational artifact is that one document. It wraps the plan unchanged and adds geometry,
/// display values and provenance. It exports to JSON, CSV and a printable HTML work order, and
/// none of that calls a solver, a renderer or a clock. Every engine builds the same bytes from it.
/// </summary>
public static class Artifacts
{
    /// <summary>A fuse far above what this solve needs, so the answer never depends on machine load.</summary>
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {"time_limit_ms": 60000},
          "items": [
            {"id": "printer", "dimensions": {"length": 420, "width": 300, "height": 250}, "weight": "12 kg",
             "metadata": {"sales_order": "SO-1042"}},
            {"id": "toner", "quantity": 3, "dimensions": {"length": 300, "width": 100, "height": 100},
             "weight": "1.5 kg", "minimum_support_ratio": 0.5}
          ],
          "containers": [{"id": "crate", "inner_dimensions": {"length": 800, "width": 400, "height": 400}}]
        }
        """;

    private static readonly string Rule = new('=', 78);

    private static readonly string[] Sizes = ["length", "width", "height"];

    public static void Run()
    {
        var request = (JsonObject)JsonReader.Parse(Request);
        var result = JsonApi.PackFromJson(request);

        Section("1. One document that carries everything a consumer needs");
        var crate = Objects(result, "containers")[0];
        var inside = Size(Get(crate, "inner_dimensions"));
        // The order boxes can go in without lifting one over another comes from the engine's own
        // sequence API. The artifact is handed that order; it never invents one.
        var order = RouteSequence.SafeLoadingOrder([.. Objects(crate, "placements").Select(Box)], inside);
        var artifact = OperationalArtifact.Build(request, result, new Dictionary<int, IReadOnlyList<int>> { [0] = order });
        var provenance = Get(artifact, "provenance");
        var steps = ((JsonArray)Objects(Get(artifact, "plan"), "containers")[0].Require("steps")).Items.Length;
        var crateLength = Get(Objects(Get(artifact, "geometry"), "containers")[0], "inner_dimensions").Require("length");
        Console.WriteLine($"  format:          {Text(artifact.Require("format"))}");
        Console.WriteLine($"  plan steps:      {steps}");
        Console.WriteLine($"  crate inside:    {Text(crateLength)} ticks long");
        Console.WriteLine($"  replay:          {Text(Get(provenance, "replay").Require("level"))}");
        var printer = Objects(Get(provenance, "request"), "items")[0];
        Console.WriteLine($"  sales order:     {Text(Get(printer, "metadata").Require("sales_order"))}");
        Say("",
            "The request is inside the artifact, not a hash of it: a digest names a request,",
            "and only the request itself lets someone replay the artifact without a lookup.",
            "`replay` is `exact` because the search finished inside its fuse; a search the",
            "clock stopped would say `not_guaranteed` and name the field that decided it.");

        Section("2. A CSV a warehouse system can import");
        foreach (var row in ArtifactExports.Csv(artifact).Split("\r\n").Take(3))
        {
            Console.WriteLine($"  {row}");
        }
        Say("",
            "One row per step, in the plan's order, then one row per item that was not",
            "packed. The tick columns are the identifiers a system matches on; the rendered",
            "columns are for people. Nothing is re-rendered: every value is the result's own.");

        Section("3. A work order to print");
        var html = ArtifactExports.WorkOrderHtml(artifact);
        Console.WriteLine($"  {Encoding.UTF8.GetByteCount(html)} bytes of HTML, one section per container, one checkbox per step");
        var external = html.Contains("<script", StringComparison.Ordinal) || html.Contains("http", StringComparison.Ordinal);
        Console.WriteLine($"  contains a script or an external resource: {external}");
        Say("",
            "Self-contained on purpose: a work order that loads a stylesheet from the network",
            "is a work order that prints blank on a dock with no signal.");

        Section("4. The same bytes in every engine, and a refusal instead of a guess");
        var canonical = ArtifactExports.Json(artifact);
        Console.WriteLine($"  canonical JSON: {Encoding.UTF8.GetByteCount(canonical)} bytes, starting {canonical[..40]}...");
        try
        {
            ArtifactExports.Csv(new JsonObject(artifact.Members.Append(
                new KeyValuePair<string, JsonValue>("format", new JsonString("packvium-operational-artifact/v2")))));
        }
        catch (OperationalArtifactException error)
        {
            Console.WriteLine($"  a v2 document: refused with {error.Code}");
        }
        Say("",
            "The canonical form is RFC 8785, so PHP, Rust and JavaScript build these exact",
            "bytes from the same request and result. A reader that meets a format it does not",
            "know refuses it by name rather than drawing half of it.");
    }

    private static AxisAlignedBox Box(JsonObject placement)
    {
        var position = Get(placement, "position");
        return new AxisAlignedBox(new Point(Ticks(position, "x"), Ticks(position, "y"), Ticks(position, "z")),
            Size(Get(placement, "dimensions")));
    }

    private static Dimensions Size(JsonObject size) =>
        Dimensions.OfTicks(Ticks(size, Sizes[0]), Ticks(size, Sizes[1]), Ticks(size, Sizes[2]));

    private static long Ticks(JsonObject scalars, string axis) => (long)((JsonInteger)Get(scalars, axis).Require("ticks")).Value;

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
}
