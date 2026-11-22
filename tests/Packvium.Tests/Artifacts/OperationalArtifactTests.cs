using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Packvium.Artifacts;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Artifacts;

/// <summary>
/// The builder's refusals and the exports' rules, each on a constructed result. The expected
/// CSV and HTML of <see cref="SmallResult"/> are the tag's Python <c>export_csv</c> and
/// <c>export_work_order_html</c> of the same inputs.
/// </summary>
public sealed partial class OperationalArtifactTests
{
    /// <summary><c>_small_result()</c> of the tag's <c>conformance/artifacts/run.py</c>, with an item type a spreadsheet and a browser would both misread, and an unpacked item.</summary>
    private const string SmallResult = """
        {"status": "feasible", "objective": "default", "score": [1, 0], "feasibility": {"code": "all_items_packed"}, "optimality": null, "containers": [{"id": "crate#1", "container_type": "crate", "inner_dimensions": {"length": {"ticks": 1600000, "unit": "mm", "value": "100"}, "width": {"ticks": 1600000, "unit": "mm", "value": "100"}, "height": {"ticks": 1600000, "unit": "mm", "value": "100"}}, "payload_weight": {"ticks": 8000, "unit": "g", "value": "1"}, "gross_weight": {"ticks": 16000, "unit": "g", "value": "2"}, "volume_utilization": "0.001000", "placements": [{"item_id": "box#1", "item_type": "=sum(\"a\",b)<&>'", "orientation": "LWH", "position": {"x": {"ticks": 0, "unit": "mm", "value": "0"}, "y": {"ticks": 0, "unit": "mm", "value": "0"}, "z": {"ticks": 0, "unit": "mm", "value": "0"}}, "dimensions": {"length": {"ticks": 160000, "unit": "mm", "value": "10"}, "width": {"ticks": 160000, "unit": "mm", "value": "10"}, "height": {"ticks": 160000, "unit": "mm", "value": "10"}}, "support_ratio": "1.000000", "top_load": {"ticks": 0, "unit": "g", "value": "0"}}]}], "unpacked_items": [{"item_id": "ladder#1", "item_type": "ladder", "reason": "no_container_fits", "details": [], "proof": {"level": "observed"}}], "algorithm": {"profile": "balanced", "solver": "extreme_point", "seed": 3, "duration_ms": 12, "time_limit_reached": false, "effort_limit_reached": false}}
        """;

    private const string ExpectedCsv = "record,container_index,container_type,sequence,item_type,orientation,x_ticks,y_ticks,z_ticks,x,y,z,length,width,height,length_unit,reason,proof_level\r\n" + "step,0,crate,1,\"=sum(\"\"a\"\",b)<&>'\",LWH,0,0,0,0,0,0,10,10,10,mm,,\r\n" + "unplaced,,,,ladder,,,,,,,,,,,,no_container_fits,observed\r\n";

    private static readonly string ExpectedHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Packing work order</title>
        <style>body{font-family:system-ui,sans-serif;margin:24px;color:#111}h1{font-size:20px}h2{font-size:16px;margin-top:24px}table{border-collapse:collapse;width:100%;margin:8px 0 16px}th,td{border:1px solid #999;padding:4px 6px;text-align:left;font-size:12px;vertical-align:top}th{background:#eee}.facts td:first-child{width:28%;font-weight:600}@media print{body{margin:0}section.container{break-after:page}tr{break-inside:avoid}}</style>
        </head>
        <body>
        <h1>Packing work order</h1>
        <table class="facts">
        <tr><td>Status</td><td>feasible</td></tr>
        <tr><td>Objective</td><td>default</td></tr>
        <tr><td>Containers</td><td>1</td></tr>
        <tr><td>Score</td><td>[1, 0]</td></tr>
        <tr><td>Feasibility</td><td>all_items_packed</td></tr>
        <tr><td>Replay</td><td>exact</td></tr>
        <tr><td>Solver</td><td>balanced / extreme_point / seed 3</td></tr>
        <tr><td>Catalogs</td><td>&mdash;</td></tr>
        <tr><td>Packvium</td><td>1.5.0</td></tr>
        </table>
        <section class="container">
        <h2>Container 1: crate</h2>
        <p>Payload 1 g &middot; Gross 2 g &middot; Utilization 0.001000</p>
        <p>Order: loading. Follow the steps in sequence.</p>
        <table>
        <thead><tr><th>Step</th><th>Item</th><th>Orientation</th><th>Position x, y, z (mm)</th><th>Size l &times; w &times; h (mm)</th><th>Done</th></tr></thead>
        <tbody>
        <tr><td>1</td><td>=sum(&quot;a&quot;,b)&lt;&amp;&gt;&#39;</td><td>LWH</td><td>0, 0, 0</td><td>10 &times; 10 &times; 10</td><td>&#9744;</td></tr>
        </tbody>
        </table>
        </section>
        <section>
        <h2>Not packed</h2>
        <table>
        <thead><tr><th>Item</th><th>Reason</th><th>Proof</th></tr></thead>
        <tbody>
        <tr><td>ladder</td><td>no_container_fits</td><td>observed</td></tr>
        </tbody>
        </table>
        </section>
        </body>
        </html>
        """ + "\n";

    private static readonly JsonObject Request = Json("""{"items":[],"containers":[],"metadata":{"whole":1.0}}""");

    private static readonly Dictionary<int, IReadOnlyList<int>> FirstOrder = new() { [0] = [0] };

    private static JsonObject Json(string text) => (JsonObject)JsonReader.Parse(text);

    private static JsonObject Result(Func<string, string>? edit = null) => Json(edit is null ? SmallResult : edit(SmallResult));

    private static JsonObject Build(JsonObject? result = null, IReadOnlyDictionary<int, IReadOnlyList<int>>? orders = null) =>
        OperationalArtifact.Build(Request, result ?? Result(), orders ?? FirstOrder);

    private static string Refusal(Action action) => Assert.Throws<OperationalArtifactException>(action).Code;

    // ---------------------------------------------------------------------------- exports

    [Fact]
    public void TheCsvIsTheReferencesByteForByte() => Assert.Equal(ExpectedCsv, ArtifactExports.Csv(Build()));

    [Fact]
    public void TheWorkOrderIsTheReferencesByteForByte() => Assert.Equal(ExpectedHtml, ArtifactExports.WorkOrderHtml(Build()));

    /// <summary>Quoted only for a comma, quote, CR or LF; a leading <c>=</c> is copied, never prefixed.</summary>
    [Fact]
    public void ACsvFieldIsQuotedOnlyWhenItMustBe()
    {
        var rows = ArtifactExports.Csv(Build()).Split("\r\n");
        Assert.Equal(string.Join(",", ArtifactExports.CsvColumns), rows[0]);
        Assert.Equal(18, ArtifactExports.CsvColumns.Length);
        Assert.Contains(",\"=sum(\"\"a\"\",b)<&>'\",", rows[1], StringComparison.Ordinal);
        Assert.Equal("", rows[^1]);
        var lineBreak = ArtifactExports.Csv(Build(Result(text => text.Replace("=sum(\\\"a\\\",b)<&>'", "two\\nlines", StringComparison.Ordinal))));
        Assert.Contains(",\"two\nlines\",", lineBreak, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkOrderHasNoScriptAndNoExternalResource()
    {
        var html = ArtifactExports.WorkOrderHtml(Build());
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html, StringComparison.Ordinal);
        Assert.Matches(Ascii(), html);
    }

    [Fact]
    public void WithoutAnOrderTheStepsAreNotNumbered()
    {
        var artifact = OperationalArtifact.Build(Request, Result());
        Assert.Contains("<p>Order: unavailable.", ArtifactExports.WorkOrderHtml(artifact), StringComparison.Ordinal);
        Assert.Contains("step,0,crate,,", ArtifactExports.Csv(artifact), StringComparison.Ordinal);
    }

    /// <summary>An export reads the canonical bytes, so a float <c>1.0</c> held in memory is the integer <c>1</c>.</summary>
    [Fact]
    public void AnExportReadsTheArtifactThroughItsCanonicalForm()
    {
        var artifact = Build();
        Assert.Contains("\"metadata\":{\"whole\":1}", ArtifactExports.Json(artifact), StringComparison.Ordinal);
        Assert.Equal(OperationalArtifact.CanonicalJson(artifact), ArtifactExports.Json(artifact));
        var edited = Edit(artifact, "work_order", workOrder => Edit(workOrder, "containers", containers =>
            new JsonArray([Edit((JsonObject)((JsonArray)containers).Items[0], "container_index", _ => new JsonFloat(1.0))])));
        Assert.Contains("<h2>Container 2: crate</h2>", ArtifactExports.WorkOrderHtml(edited), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fraction")]
    [InlineData("boolean")]
    public void AContainerIndexThatIsNotAnIntegerIsRefused(string kind)
    {
        JsonValue index = kind == "fraction" ? new JsonFloat(0.5) : new JsonBool(true);
        var edited = Edit(Build(), "work_order", workOrder => Edit(workOrder, "containers", containers =>
            new JsonArray([Edit((JsonObject)((JsonArray)containers).Items[0], "container_index", _ => index)])));
        Assert.Equal("invalid_value", Refusal(() => ArtifactExports.WorkOrderHtml(edited)));
    }

    [Fact]
    public void ABooleanHasNoSingleRendering()
    {
        var artifact = Build(Result(text => text.Replace("\"code\": \"all_items_packed\"", "\"code\": true", StringComparison.Ordinal)));
        Assert.Equal("invalid_value", Refusal(() => ArtifactExports.WorkOrderHtml(artifact)));
    }

    [Fact]
    public void AnUnknownFormatIsRefusedByName()
    {
        var foreign = Json("""{"format":"packvium-operational-artifact/v2"}""");
        Assert.Equal("unknown_format", Refusal(() => ArtifactExports.Json(foreign)));
        Assert.Equal("unknown_format", Refusal(() => ArtifactExports.Csv(new JsonArray([]))));
        Assert.Equal("invalid_json", Refusal(() => OperationalArtifact.Parse("{")));
    }

    /// <summary>
    /// A document that names the v1 format but is not shaped like one is refused by code from
    /// every export, never by a cast or lookup failure that escapes the exporter.
    /// </summary>
    [Theory]
    [InlineData("provenance", null)]
    [InlineData("provenance", "1")]
    [InlineData("plan", "[]")]
    [InlineData("work_order", "\"x\"")]
    [InlineData("suite_version", null)]
    public void AMalformedV1DocumentIsRefusedAsAnUnknownFormat(string field, string? replacement)
    {
        var artifact = Build();
        var fields = artifact.Keys.Where(key => key != field || replacement is not null)
            .ToDictionary(key => key, key => key == field ? JsonReader.Parse(replacement!) : artifact.Require(key));
        var edited = new JsonObject(fields);
        Assert.Equal("unknown_format", Refusal(() => ArtifactExports.WorkOrderHtml(edited)));
        // The CSV reads only the plan and the work order, as the reference's does.
        if (field is "plan" or "work_order")
        {
            Assert.Equal("unknown_format", Refusal(() => ArtifactExports.Csv(edited)));
        }
    }

    // ---------------------------------------------------------------------------- builder

    [Fact]
    public void TheArtifactCarriesThePlanTheRequestAndTheSolver()
    {
        var artifact = Build();
        Assert.Equal(OperationalArtifact.Format, ((JsonString)artifact.Require("format")).Value);
        Assert.Equal("1.5.0", ((JsonString)artifact.Require("suite_version")).Value);
        var provenance = (JsonObject)artifact.Require("provenance");
        Assert.Same(Request, provenance.Require("request"));
        var solver = (JsonObject)provenance.Require("solver");
        Assert.Equal(OperationalArtifact.SolverFields.Order(StringComparer.Ordinal), solver.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("exact", Replay(artifact).Level);
    }

    [Fact]
    public void AStoppedSearchOrAMissingAlgorithmCannotPromiseAnExactReplay()
    {
        var stopped = Build(Result(text => text.Replace("\"time_limit_reached\": false", "\"time_limit_reached\": true", StringComparison.Ordinal)));
        Assert.Equal(("not_guaranteed", "provenance.solver.time_limit_reached"), Replay(stopped));
        var unknown = Build(Without(Result(), "algorithm"));
        Assert.Equal(("not_guaranteed", "provenance.solver"), Replay(unknown));
        Assert.Contains("<tr><td>Solver</td><td>&mdash;</td></tr>", ArtifactExports.WorkOrderHtml(unknown), StringComparison.Ordinal);
    }

    /// <summary><c>ReplayLevel</c> answers without building the artifact, by the same rule.</summary>
    [Fact]
    public void ReplayLevelIsThePromiseTheArtifactWouldMake()
    {
        var stopped = Result(text => text.Replace("\"time_limit_reached\": false", "\"time_limit_reached\": true", StringComparison.Ordinal));
        foreach (var result in new[] { Result(), stopped, Without(Result(), "algorithm") })
        {
            Assert.Equal(Replay(Build(result)).Level, OperationalArtifact.ReplayLevel(result));
        }
        Assert.Equal("exact", OperationalArtifact.ReplayLevel(Result()));
    }

    [Fact]
    public void RequestAndResultMustBeObjects()
    {
        Assert.Equal("invalid_request", Refusal(() => OperationalArtifact.Build(new JsonArray([]), Result())));
        Assert.Equal("invalid_result", Refusal(() => OperationalArtifact.Build(Request, JsonNull.Instance)));
        Assert.Equal("invalid_result", Refusal(() => Build(Result(text => text.Replace("\"containers\": [{", "\"containers\": [\"crate\", {", StringComparison.Ordinal)))));
    }

    [Theory]
    [InlineData("\"seed\": 3, ", "")]
    [InlineData("\"effort_limit_reached\": false", "\"effort_limit_reached\": 0")]
    [InlineData("\"unpacked_items\": [", "\"catalog_versions_used\": [{\"catalog_id\": \"cartons\", \"version\": 1.5, \"effective_at\": 1, \"resolved_at\": 2}], \"unpacked_items\": [")]
    [InlineData("\"ticks\": 160000, \"unit\": \"mm\", \"value\": \"10\"}, \"width\"", "\"ticks\": \"160000\", \"unit\": \"mm\", \"value\": \"10\"}, \"width\"")]
    public void AMalformedResultIsRefusedAsInvalidResult(string from, string to)
    {
        var source = SmallResult.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(SmallResult, source);
        Assert.Equal("invalid_result", Refusal(() => Build(Json(source))));
    }

    [Fact]
    public void TwoPlacementsAtOneSlotAreRefused()
    {
        var result = Result(text => text.Replace("\"placements\": [{", "\"placements\": [{\"item_id\": \"box#0\", \"item_type\": \"=sum(\\\"a\\\",b)<&>'\", \"orientation\": \"LWH\", \"position\": {\"x\": {\"ticks\": 0, \"unit\": \"mm\", \"value\": \"0\"}, \"y\": {\"ticks\": 0, \"unit\": \"mm\", \"value\": \"0\"}, \"z\": {\"ticks\": 0, \"unit\": \"mm\", \"value\": \"0\"}}, \"dimensions\": {\"length\": {\"ticks\": 1, \"unit\": \"mm\", \"value\": \"0\"}, \"width\": {\"ticks\": 1, \"unit\": \"mm\", \"value\": \"0\"}, \"height\": {\"ticks\": 1, \"unit\": \"mm\", \"value\": \"0\"}}}, {", StringComparison.Ordinal));
        Assert.Equal("invalid_result", Refusal(() => Build(result, new Dictionary<int, IReadOnlyList<int>> { [0] = [0, 1] })));
    }

    [Fact]
    public void AValueInAnotherUnitIsRefusedAsMixedUnits()
    {
        var result = Result(text => text.Replace("\"gross_weight\": {\"ticks\": 16000, \"unit\": \"g\"", "\"gross_weight\": {\"ticks\": 16000, \"unit\": \"kg\"", StringComparison.Ordinal));
        Assert.Equal("mixed_units", Refusal(() => Build(result)));
    }

    [Fact]
    public void APlanTheAdapterRefusesIsRefusedAsInvalidPlanInput() =>
        Assert.Equal("invalid_plan_input", Refusal(() => Build(orders: new Dictionary<int, IReadOnlyList<int>> { [0] = [0, 0] })));

    [Fact]
    public void NumbersBeyondTwoToTheFiftyThirdAreRefusedWhereverTheyAre()
    {
        var ticks = Result(text => text.Replace("\"ticks\": 160000, \"unit\": \"mm\", \"value\": \"10\"}, \"width\"", "\"ticks\": 9007199254740992, \"unit\": \"mm\", \"value\": \"10\"}, \"width\"", StringComparison.Ordinal));
        Assert.Equal("number_out_of_range", Refusal(() => Build(ticks)));
        var request = Json("""{"items":[],"containers":[],"metadata":{"n":9007199254740992}}""");
        Assert.Equal("number_out_of_range", Refusal(() => OperationalArtifact.Build(request, Result(), FirstOrder)));
        var surrogate = OperationalArtifact.Parse("""{"items":[],"metadata":{"bad":"\ud800"}}""");
        Assert.Equal("invalid_string", Refusal(() => OperationalArtifact.Build(surrogate, Result(), FirstOrder)));
    }

    /// <summary>Like the plan, the artifact layer reads JSON only: no solver, validator or packer.</summary>
    [Fact]
    public void TheArtifactLayerImportsNoSolverAndNoValidator()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "Packvium", "Artifacts");
        var sources = Directory.GetFiles(directory, "*.cs");
        Assert.NotEmpty(sources);
        foreach (var source in sources.Select(File.ReadAllText))
        {
            Assert.DoesNotMatch(Forbidden(), source);
            foreach (Match directive in Using().Matches(source))
            {
                Assert.Matches(AllowedImport(), directive.Groups[1].Value);
            }
        }
    }

    [GeneratedRegex(@"\b(Algorithms|Validation|Packing|Constraints|Objectives|Nested)\.")]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"^using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex Using();

    [GeneratedRegex(@"^(System(\..+)?|Packvium\.(Serialization|Execution))$")]
    private static partial Regex AllowedImport();

    [GeneratedRegex(@"^[\x00-\x7F]*$")]
    private static partial Regex Ascii();

    private static (string Level, string? Because) Replay(JsonObject artifact)
    {
        var replay = (JsonObject)((JsonObject)artifact.Require("provenance")).Require("replay");
        return (((JsonString)replay.Require("level")).Value, (replay.Require("because") as JsonString)?.Value);
    }

    private static JsonObject Edit(JsonValue value, string key, Func<JsonValue, JsonValue> change)
    {
        var fields = (JsonObject)value;
        return new JsonObject(fields.Members.Select(member =>
            member.Key == key ? new KeyValuePair<string, JsonValue>(key, change(member.Value)) : member));
    }

    private static JsonObject Without(JsonObject value, string key) =>
        new(value.Members.Where(member => member.Key != key));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Packvium.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Packvium.slnx not found above the test binary");
    }
}
