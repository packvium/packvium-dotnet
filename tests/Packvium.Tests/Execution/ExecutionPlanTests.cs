using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Packvium.Execution;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Execution;

/// <summary>
/// The adapter's own rules, each on a constructed result: the golden corpus has no ranked
/// runners-up and no injected loading order, so these are the only place either is exercised.
/// </summary>
public sealed partial class ExecutionPlanTests
{
    /// <summary>
    /// A result with a repeating float, non-ASCII and control characters, an unpacked item and
    /// two alternatives, and the reference's <c>canonical_plan_json</c> of it with loading order
    /// <c>{0: [0]}</c>, produced by <c>packvium.execution</c> at v1.2.0.
    /// </summary>
    private const string AwkwardResult = "{\"status\": \"feasible\", \"objective\": \"default\", \"score\": [0, 3, 0, 250000, 900000], \"feasibility\": {\"code\": \"feasible\"}, \"optimality\": {\"code\": \"not_proven\"}, \"containers\": [{\"id\": \"box#1\", \"container_type\": \"kiste-\\u00fc\\u2603\\ud83d\\udce6\", \"volume_utilization\": 0.3333333333333333, \"placements\": [{\"item_id\": \"crate\\t\\\"q\\\"\\\\\\u0001\\u007f#000\", \"item_type\": \"crate\\t\\\"q\\\"\\\\\\u0001\\u007f\", \"orientation\": \"LWH\", \"position\": {\"x\": {\"ticks\": 0, \"value\": \"0\", \"unit\": \"mm\"}, \"y\": {\"ticks\": 0, \"value\": \"0\", \"unit\": \"mm\"}, \"z\": {\"ticks\": 0, \"value\": \"0\", \"unit\": \"mm\"}}, \"dimensions\": {\"length\": {\"ticks\": 1600000, \"value\": \"100\", \"unit\": \"mm\"}, \"width\": {\"ticks\": 1600000, \"value\": \"100\", \"unit\": \"mm\"}, \"height\": {\"ticks\": 1600000, \"value\": \"100\", \"unit\": \"mm\"}}, \"support_ratio\": 1.0, \"top_load\": 0}]}], \"unpacked_items\": [{\"item_id\": \"ladder#1\", \"item_type\": \"ladder\", \"reason\": \"no_container_fits\", \"details\": [\"longest dimension exceeds every container\"], \"proof\": {\"level\": \"observed\", \"observations\": [{\"code\": \"too_long\"}]}}], \"alternatives\": [{\"status\": \"feasible\", \"score\": [0, 4, 0, 250000, 900000]}, {\"status\": \"feasible\", \"score\": [0, 3, 0, 250000, 900000]}]}";

    private const string AwkwardPlan = "{\"alternatives\":[{\"facts\":{\"alternative_index\":0,\"first_difference\":{\"alternative\":4,\"difference\":1,\"index\":1,\"winner\":3},\"score\":[0,4,0,250000,900000],\"status\":\"feasible\"},\"presentation\":{\"cites\":[\"score\",\"alternatives[].score\"],\"summary\":\"This option differs first at objective axis 1 (unnamed objective axis): chosen 3, this 4.\"}},{\"facts\":{\"alternative_index\":1,\"first_difference\":null,\"score\":[0,3,0,250000,900000],\"status\":\"feasible\"},\"presentation\":{\"cites\":[\"score\",\"alternatives[].score\"],\"summary\":\"This option scored identically to the chosen one on every objective axis; the score does not record why one was taken.\"}}],\"containers\":[{\"container_index\":0,\"facts\":{\"container_type\":\"kiste-\u00fc\u2603\U0001f4e6\",\"placement_count\":1,\"volume_utilization\":0.3333333333333333},\"order\":\"loading\",\"steps\":[{\"placement\":{\"container_index\":0,\"item_type\":\"crate\\t\\\"q\\\"\\\\\\u0001\u007f\",\"orientation\":\"LWH\",\"position_ticks\":{\"x\":0,\"y\":0,\"z\":0}},\"sequence\":1}]}],\"facts\":{\"container_count\":1,\"feasibility\":{\"code\":\"feasible\"},\"optimality\":{\"code\":\"not_proven\"},\"score\":[0,3,0,250000,900000],\"status\":\"feasible\"},\"format\":\"packvium-execution-plan/v1\",\"objective\":\"default\",\"unplaced\":[{\"facts\":{\"details\":[\"longest dimension exceeds every container\"],\"item_type\":\"ladder\",\"proof_level\":\"observed\",\"reason\":\"no_container_fits\"},\"presentation\":{\"cites\":[\"unpacked_items[].reason\",\"unpacked_items[].proof.level\"],\"summary\":\"Not packed: no_container_fits (observed).\"}}]}";

    private static JsonObject Scalar(long ticks) => Json($$"""{"ticks": {{ticks}}, "value": "{{ticks / 16000}}", "unit": "mm"}""");

    private static string Placement(string itemType, long x, long y = 0, long z = 0) =>
        $$"""{"item_id": "{{itemType}}#{{x}}{{y}}{{z}}", "item_type": "{{itemType}}", "orientation": "LWH", """
        + $$"""
            "position": {"x": {{Text(Scalar(x))}}, "y": {{Text(Scalar(y))}}, "z": {{Text(Scalar(z))}}
            """
        + "}}";

    private static JsonObject Result(string alternatives = "[]", string unpacked = "[]", string? placements = null) => Json($$"""
        {"status": "feasible", "objective": "default", "score": [0, 1, 0, 0, 1000000],
         "feasibility": {"code": "feasible"}, "optimality": {"code": "not_proven"},
         "containers": [{"id": "box#1", "container_type": "box", "volume_utilization": "0.500000",
                         "placements": {{placements ?? $"[{Placement("cube", 0)}, {Placement("cube", 1600000)}]"}}}],
         "unpacked_items": {{unpacked}}, "alternatives": {{alternatives}}}
        """);

    private static readonly JsonObject NoRequest = new([]);

    [Fact]
    public void ItMatchesTheReferenceBytesOnAnAwkwardResult()
    {
        var orders = new Dictionary<int, IReadOnlyList<int>> { [0] = [0] };
        Assert.Equal(AwkwardPlan, ExecutionPlan.CanonicalJson(ExecutionPlan.Build(NoRequest, Json(AwkwardResult), orders)));
    }

    [Fact]
    public void WithoutAnInjectedOrderThePlanSaysSo()
    {
        var container = Containers(ExecutionPlan.Build(NoRequest, Result()))[0];
        Assert.Equal("unavailable", ((JsonString)container.Require("order")).Value);
        var steps = Steps(container);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.False(step.Contains("sequence")));
    }

    [Fact]
    public void AnInjectedOrderIsUsedVerbatim()
    {
        var orders = new Dictionary<int, IReadOnlyList<int>> { [0] = [1, 0] };
        var container = Containers(ExecutionPlan.Build(NoRequest, Result(), orders))[0];
        Assert.Equal("loading", ((JsonString)container.Require("order")).Value);
        var steps = Steps(container);
        Assert.Equal("1,2", string.Join(",", steps.Select(step => ((JsonInteger)step.Require("sequence")).Value)));
        var first = (JsonObject)((JsonObject)steps[0].Require("placement")).Require("position_ticks");
        Assert.Equal(1600000, (long)((JsonInteger)first.Require("x")).Value);
    }

    [Fact]
    public void AnOrderThatIsNotAPermutationIsRefused()
    {
        var orders = new Dictionary<int, IReadOnlyList<int>> { [0] = [0, 0] };
        var error = Assert.Throws<ExecutionPlanException>(() => ExecutionPlan.Build(NoRequest, Result(), orders));
        Assert.Contains("permutation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlacementReferenceDoesNotUseItemIdAndReadsTicks()
    {
        var placement = Json(Placement("cube", 12345).Replace("\"value\": \"0\"", "\"value\": \"wrong\"", StringComparison.Ordinal));
        var reference = ExecutionPlan.PlacementReference(0, placement);
        Assert.Equal("container_index,item_type,orientation,position_ticks", string.Join(",", reference.Keys));
        Assert.Equal(12345, (long)((JsonInteger)((JsonObject)reference.Require("position_ticks")).Require("x")).Value);
    }

    [Fact]
    public void APlacementItCannotReferenceIsRefused()
    {
        var placement = Json(Placement("cube", 0).Replace("\"orientation\": \"LWH\",", "", StringComparison.Ordinal));
        var error = Assert.Throws<ExecutionPlanException>(() => ExecutionPlan.PlacementReference(0, placement));
        Assert.Equal("placement is missing a field the reference is built from: 'orientation'", error.Message);
    }

    [Fact]
    public void TheLossIsTheFirstDifferingIndexAndTheSentenceClaimsNoCause()
    {
        var plan = ExecutionPlan.Build(NoRequest, Result("""[{"status": "feasible", "score": [0, 2, 0, 0, 900000]}]"""));
        var alternative = (JsonObject)((JsonArray)plan.Require("alternatives")).Items[0];
        var facts = (JsonObject)alternative.Require("facts");
        Assert.Equal("""{"alternative":2,"difference":1,"index":1,"winner":1}""",
            PythonJson.Dumps(facts.Require("first_difference"), sortKeys: true));
        var summary = ((JsonString)((JsonObject)alternative.Require("presentation")).Require("summary")).Value;
        Assert.Contains("axis 1", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("because", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScoreVectorsOfDifferentLengthAreRefused()
    {
        var error = Assert.Throws<ExecutionPlanException>(() =>
            ExecutionPlan.Build(NoRequest, Result("""[{"score": [0, 1]}]""")));
        Assert.Contains("different length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpackedItemKeepsItsProofLevelUnsoftened()
    {
        var plan = ExecutionPlan.Build(NoRequest, Result(unpacked: """
            [{"item_id": "ladder#1", "item_type": "ladder", "reason": "no_container_fits", "details": [],
              "proof": {"level": "observed", "observations": [{"code": "too_long"}]}}]
            """));
        var entry = (JsonObject)((JsonArray)plan.Require("unplaced")).Items[0];
        Assert.Equal("observed", ((JsonString)((JsonObject)entry.Require("facts")).Require("proof_level")).Value);
        Assert.Equal("""{"cites":["unpacked_items[].reason","unpacked_items[].proof.level"],"summary":"Not packed: no_container_fits (observed)."}""",
            PythonJson.Dumps(entry.Require("presentation"), sortKeys: true));
    }

    [Fact]
    public void AResultWithoutAStatusIsNotAValidatedResult()
    {
        var error = Assert.Throws<ExecutionPlanException>(() => ExecutionPlan.Build(NoRequest, Json("""{"containers": []}""")));
        Assert.Contains("validated result", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameInputsProduceTheSameBytes()
    {
        var first = ExecutionPlan.CanonicalJson(ExecutionPlan.Build(NoRequest, Result()));
        Assert.Equal(first, ExecutionPlan.CanonicalJson(ExecutionPlan.Build(NoRequest, Result())));
        Assert.StartsWith("""{"alternatives":""", first, StringComparison.Ordinal);
        Assert.DoesNotContain(", ", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dependency direction from <c>docs/EXECUTION-PLAN.md</c>, as the cheapest test of it:
    /// with no solver the adapter cannot produce a placement, and with no validator it cannot
    /// bless one. A namespace is reachable either through a <c>using</c> or by a qualified name,
    /// and inside <c>Packvium.Execution</c> a qualified name may omit the <c>Packvium.</c> prefix.
    /// </summary>
    [Fact]
    public void TheAdapterImportsNoSolverAndNoValidator()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "Packvium", "Execution");
        var sources = Directory.GetFiles(directory, "*.cs");
        Assert.NotEmpty(sources);
        foreach (var path in sources)
        {
            var source = File.ReadAllText(path);
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

    [GeneratedRegex(@"^(System(\..+)?|Packvium\.Serialization)$")]
    private static partial Regex AllowedImport();

    private static JsonObject Json(string text) => (JsonObject)JsonReader.Parse(text);

    private static string Text(JsonObject value) => PythonJson.Dumps(value, sortKeys: false);

    private static List<JsonObject> Containers(JsonObject plan) =>
        [.. ((JsonArray)plan.Require("containers")).Items.Cast<JsonObject>()];

    private static List<JsonObject> Steps(JsonObject container) =>
        [.. ((JsonArray)container.Require("steps")).Items.Cast<JsonObject>()];

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
