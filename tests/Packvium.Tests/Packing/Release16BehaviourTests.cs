using System.Collections.Immutable;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;
using Packvium.Validation;
using Xunit;

namespace Packvium.Tests.Packing;

/// <summary>
/// The behaviour 1.6 changes outside the new request fields, with the expectations the tag's
/// Python gives (<c>test_limit_reasons.py</c>, <c>test_unsupported_placements.py</c>,
/// <c>test_solvers.py</c>, <c>test_flat_bag.py</c> and <c>test_made_to_measure.py</c>).
/// </summary>
public sealed class Release16BehaviourTests
{
    private const string Budget = """
        "configuration": {"solver_profile": "SOLVER", "time_limit_ms": 300000,
                          "effort_budget": {"max_candidates_evaluated": 1000000, "max_placement_attempts": 1000000,
                                            "max_search_nodes": 1000000RESTARTS}}
        """;

    private static JsonObject Pack(string body, string profile = "fast", string restarts = "") =>
        JsonApi.PackFromJson(JsonReader.Parse("{" + Budget.Replace("SOLVER", profile, System.StringComparison.Ordinal)
            .Replace("RESTARTS", restarts, System.StringComparison.Ordinal) + ", " + body + "}"));

    private static string[] Reasons(JsonObject result) =>
        [.. ((JsonArray)result.Require("unpacked_items")).Items.Cast<JsonObject>()
            .Select(item => ((JsonString)item.Require("reason")).Value)];

    private static string Solver(JsonObject result) =>
        ((JsonString)((JsonObject)result.Require("algorithm")).Require("solver")).Value;

    [Fact]
    public void AnItemThatOverloadsAnAxleWhereverItStandsAloneSaysSo()
    {
        var result = Pack("""
            "items": [{"id": "payload", "dimensions": {"length": "10", "width": "10", "height": "10"},
                       "weight": {"value": "1000", "unit": "g"}, "allowed_rotations": ["LWH"]}],
            "containers": [{"id": "vehicle", "inner_dimensions": {"length": "100", "width": "10", "height": "10"},
                            "axles": [{"position": "0", "max_load": {"value": "400", "unit": "g"}},
                                      {"position": "100", "max_load": {"value": "400", "unit": "g"}}]}]
            """);
        Assert.Equal(["axle_overloaded"], Reasons(result));
    }

    [Fact]
    public void AnItemAFreshContainerWouldTakeIsLeftOutForTheInventory() =>
        Assert.Equal(["container_inventory_exhausted"], Reasons(Pack("""
            "items": [{"id": "cube", "quantity": 2, "dimensions": {"length": "10", "width": "10", "height": "10"}}],
            "containers": [{"id": "box", "inner_dimensions": {"length": "10", "width": "10", "height": "10"}, "quantity": 1}]
            """)));

    [Fact]
    public void AxleAdmissionAloneIsOneInterval()
    {
        var axles = (new Axle(new Length(0), Weight.Parse("400 g")), new Axle(new Length(100), Weight.Parse("400 g")));
        Assert.False(AxleLoad.AdmitsAlone(axles, 0, 100, Weight.Parse("1000 g").Ticks, 10));
        Assert.True(AxleLoad.AdmitsAlone(axles, 0, 100, Weight.Parse("500 g").Ticks, 10));
        Assert.False(AxleLoad.AdmitsAlone(axles, 0, 100, 1, 110));
    }

    private const string SimilarBoxes = """
        "units": {"length": "cm"},
        "items": [{"id": "b1", "dimensions": {"length": "35", "width": "25", "height": "14"}},
                  {"id": "b2", "dimensions": {"length": "35", "width": "25", "height": "14"}},
                  {"id": "b3", "dimensions": {"length": "35", "width": "23", "height": "13"}},
                  {"id": "b4", "dimensions": {"length": "35", "width": "25", "height": "14"}}],
        "containers": [{"id": "box", "inner_dimensions": {"length": "60", "width": "40", "height": "25"},
                        "max_payload": {"value": "100", "unit": "kg"}}]
        """;

    /// <summary>The block start wins with one box where every per-item start needs two.</summary>
    [Fact]
    public void BalancedTriesTheBlockSolverAsOneMoreStart()
    {
        var result = Pack(SimilarBoxes, "balanced");
        Assert.Equal("homogeneous_blocks:volume", Solver(result));
        Assert.Single(((JsonArray)result.Require("containers")).Items);
    }

    /// <summary>It runs last, so a restart budget drops it first.</summary>
    [Fact]
    public void ARestartBudgetDropsTheBlockStartFirst()
    {
        var result = Pack(SimilarBoxes, "balanced", ", \"max_restarts\": 1");
        Assert.Equal("extreme_points:volume", Solver(result));
        Assert.Equal(2, ((JsonArray)result.Require("containers")).Items.Length);
    }

    private static Placement At(Item item, long x, long y, long z, double support = 1.0)
    {
        var origin = new Point(x, y, z);
        return new Placement(new ItemInstance(item, 1), origin, Rotation.LWH, item.Dimensions, origin, item.Dimensions,
            support);
    }

    [Fact]
    public void ThePlacementsThatRestOnNothingAreCountedOnlyWhenNoSupportIsRequired()
    {
        var cube = new Item("cube", Dimensions.Mm("10", "10", "10"));
        var mm = Length.TicksPerMm;
        PackedContainer[] packed =
        [
            new(new Container("box", Dimensions.Mm("100", "100", "100")), 1,
                [At(cube, 0, 0, 0, 1.0), At(cube, 20 * mm, 0, 30 * mm, 0.0), At(cube, 40 * mm, 0, 0, 0.0)]),
        ];
        Assert.Equal("unsupported_placements: 1 placement(s) rest on nothing above the container floor; "
                     + "set configuration.minimum_support_ratio above 0 to require support",
            Packer.UnsupportedPlacementsWarning(packed, 0));
        Assert.Null(Packer.UnsupportedPlacementsWarning(packed, 0.5));
    }

    [Fact]
    public void TheValidatorRefusesABagPastItsGirth()
    {
        var book = new Item("book", Dimensions.Mm("200", "100", "20"), quantity: 3);
        var mailer = new Container("mailer", Dimensions.Mm("240", "140", "100"), deformableBoundary: FlatBag.Kind);
        var mm = Length.TicksPerMm;
        var placements = Enumerable.Range(1, 3).Select(sequence =>
        {
            var origin = new Point(0, 0, (sequence - 1) * 20 * mm);
            return new Placement(new ItemInstance(book, sequence), origin, Rotation.LWH, book.Dimensions, origin, book.Dimensions);
        }).ToImmutableArray();
        var report = IndependentSolutionValidator.Validate(new PackingRequest([book], [mailer]),
            [new PackedContainer(mailer, 1, placements)]);
        Assert.Contains(report.Issues, issue => issue is { Code: "bag_girth_exceeded", Detail: "mailer#1" });
    }

    [Fact]
    public void TheValidatorReCutsAMadeToMeasureBox()
    {
        var mm = Length.TicksPerMm;
        var spec = new MadeToMeasure(new Triple(60 * mm, 60 * mm, 60 * mm), new Triple(25 * mm, 25 * mm, 25 * mm), 1000);
        var machine = new Container("machine", Dimensions.Mm("1510", "1510", "1510"), costMinor: 40, madeToMeasure: spec);
        var cube = new Item("cube", Dimensions.Mm("100", "100", "100"));
        PackedContainer Packed(Container container) => new(container, 1, [At(cube, 0, 0, 0)]);
        var request = new PackingRequest([cube], [machine]);
        var cut = machine.Fabricate(new Triple(100 * mm, 100 * mm, 100 * mm));
        Assert.True(IndependentSolutionValidator.Validate(request, [Packed(cut)]).Valid);
        var stale = machine.Fabricate(new Triple(200 * mm, 100 * mm, 100 * mm));
        var codes = IndependentSolutionValidator.Validate(request, [Packed(stale)]).Issues.Select(issue => issue.Code).ToList();
        Assert.Equal(["fabricated_dimensions_mismatch", "outer_dimensions_mismatch", "made_to_measure_cost_mismatch"], codes);
    }

    [Fact]
    public void AMachineWhoseLargestBoxPassesSixtyFourBitsIsRefused()
    {
        var mm = Length.TicksPerMm;
        var spec = new MadeToMeasure(new Triple(60 * mm, 60 * mm, 60 * mm), new Triple(25 * mm, 25 * mm, 25 * mm),
            outerAllowance: new Triple(long.MaxValue - 1, 0, 0));
        Assert.Contains("past 64 bits", Assert.Throws<System.ArgumentException>(() =>
            new Container("machine", Dimensions.Mm("1510", "1510", "1510"), madeToMeasure: spec)).Message);
    }
}
