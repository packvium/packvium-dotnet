using System;
using System.Linq;
using System.Numerics;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Domain;

/// <summary>
/// Made-to-measure containers (<c>test_made_to_measure.py</c>): the arithmetic, the
/// rungs and the flow, pinned to the worked numbers of docs/MADE-TO-MEASURE-CONTAINERS.md.
/// </summary>
public sealed class MadeToMeasureTests
{
    private const long Mm = Length.TicksPerMm;

    private static readonly BigInteger SquareMetre = (BigInteger)MadeToMeasure.LengthTicksPerMetre * MadeToMeasure.LengthTicksPerMetre;

    private static MadeToMeasure Machine(long minimum = 60, long increment = 25, long costPerSquareMetreMinor = 0) =>
        new(Cube(minimum), Cube(increment), costPerSquareMetreMinor);

    private static Triple Cube(long sideMm) => new(sideMm * Mm, sideMm * Mm, sideMm * Mm);

    private static Triple Ticks(long length, long width, long height) => new(length * Mm, width * Mm, height * Mm);

    [Fact]
    public void TheGridRoundsUpOntoTheMachineGridAndNeverBelowTheMinimum()
    {
        var spec = Machine();
        Assert.Equal(210 * Mm, spec.Grid(0, 200 * Mm));
        Assert.Equal(210 * Mm, spec.Grid(0, 210 * Mm));
        Assert.Equal(60 * Mm, spec.Grid(0, 1));
        Assert.Equal(1510 * Mm, spec.Clamp(0, 2000 * Mm, 1510 * Mm));
        Assert.Equal(60 * Mm, spec.Clamp(0, 30 * Mm, 1510 * Mm));
    }

    [Fact]
    public void TheSplitCostEqualsTheDirectCeiling()
    {
        // Python draws these from random.Random(2702), which C# cannot reproduce; the property is the reference's.
        var generator = new Random(2702);
        for (var draw = 0; draw < 200; draw++)
        {
            var rate = generator.NextInt64(0, 1L << 53);
            var area = new BigInteger(generator.NextInt64(0, long.MaxValue)) * generator.NextInt64(0, 1L << 47);
            var direct = BigInteger.DivRem(rate * area, SquareMetre, out var remainder) + (remainder.Sign > 0 ? 1 : 0);
            Assert.Equal(direct, MadeToMeasure.PerSquareMetre(rate, area));
        }
    }

    [Fact]
    public void CostRoundsASliverOfMaterialUpToAWholeMinorUnit()
    {
        Assert.Equal(BigInteger.One, MadeToMeasure.PerSquareMetre(1, 1));
        Assert.Equal(BigInteger.Zero, MadeToMeasure.PerSquareMetre(1000, 0));
    }

    [Fact]
    public void TheWorkedBoxCostsItsFeePlusItsMaterial() =>
        Assert.Equal(new BigInteger(305), Machine(costPerSquareMetreMinor: 1000).BoxCostMinor(40, Cube(210)));

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("7", "2")]
    [InlineData("8", "2")]
    [InlineData("9", "3")]
    [InlineData("1000000000000000000000", "10000000")]
    [InlineData("1000000000000000000001", "10000001")]
    public void TheIntegerCubeRootIsTheLeastCubeAtOrAbove(string value, string root) =>
        Assert.Equal(BigInteger.Parse(root, System.Globalization.CultureInfo.InvariantCulture),
            MadeToMeasure.CubeRootCeiling(BigInteger.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void AMachineOffItsOwnGridIsRefused()
    {
        Assert.Contains("does not divide", Assert.Throws<ObjectRefusal>(() =>
            new Container("odd", Dimensions.Mm("1500", "1510", "1510"), madeToMeasure: Machine())).Message);
        Assert.Contains("exceeds inner_dimensions", Assert.Throws<ObjectRefusal>(() =>
            new Container("small", Dimensions.Mm("50", "1510", "1510"), madeToMeasure: Machine())).Message);
        Assert.Contains("minimum must be positive", Assert.Throws<ObjectRefusal>(() =>
            new MadeToMeasure(new Triple(0, 1, 1), new Triple(1, 1, 1))).Message);
        Assert.Contains("increment must be positive", Assert.Throws<ObjectRefusal>(() =>
            new MadeToMeasure(new Triple(1, 1, 1), new Triple(1, 0, 1))).Message);
        Assert.Contains("prices cannot be negative", Assert.Throws<ObjectRefusal>(() =>
            new MadeToMeasure(new Triple(1, 1, 1), new Triple(1, 1, 1), tarePerSquareMetre: -1)).Message);
        Assert.Contains("outer_allowance cannot be negative", Assert.Throws<ObjectRefusal>(() =>
            new MadeToMeasure(new Triple(1, 1, 1), new Triple(1, 1, 1), outerAllowance: new Triple(0, -1, 0))).Message);
    }

    [Fact]
    public void EightCubesInTheWorkedMachineTry260310410Then1510()
    {
        var rungs = Machine().SolvingEnvelopes(Cube(1510), Enumerable.Repeat(Cube(100), 8));
        Assert.Equal([Cube(260), Cube(310), Cube(410), Cube(1510)], rungs);
    }

    [Fact]
    public void APinnedCrossSectionKeepsOnlyTheMaximumRung()
    {
        var spec = new MadeToMeasure(Ticks(200, 100, 100), Ticks(70, 10, 10));
        Assert.Equal([Ticks(1950, 100, 100)], spec.SolvingEnvelopes(Ticks(1950, 100, 100), Enumerable.Repeat(Cube(100), 5)));
    }

    [Fact]
    public void ThreeCubesInTheSmallMachineTry190220290Then300()
    {
        var rungs = Machine(50, 10).SolvingEnvelopes(Cube(300), Enumerable.Repeat(Cube(100), 3));
        Assert.Equal([Cube(190), Cube(220), Cube(290), Cube(300)], rungs);
    }

    [Fact]
    public void RungsIgnoreTheOrderOfTheInstances()
    {
        Triple[] edges = [Ticks(300, 20, 40), Ticks(100, 100, 100), Ticks(50, 60, 70)];
        Assert.Equal(Machine().SolvingEnvelopes(Cube(1510), edges), Machine().SolvingEnvelopes(Cube(1510), Enumerable.Reverse(edges)));
    }

    /// <summary>The worked flow: eight 100 mm cubes cut a 210 mm box, priced and weighed as the tag's Python prices it.</summary>
    [Fact]
    public void EightCubesShipInTheFabricatedBox()
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse("""
            {"units": {"length": "mm"},
             "configuration": {"solver_profile": "fast", "minimum_support_ratio": 1, "time_limit_ms": 300000,
                               "effort_budget": {"max_search_nodes": 1000000}},
             "items": [{"id": "cube", "quantity": 8, "weight": "100",
                        "dimensions": {"length": "100", "width": "100", "height": "100"}}],
             "containers": [{"id": "machine", "cost_minor": 40,
                             "inner_dimensions": {"length": "1510", "width": "1510", "height": "1510"},
                             "made_to_measure": {"minimum": {"length": "60", "width": "60", "height": "60"},
                                                 "increment": {"length": "25", "width": "25", "height": "25"},
                                                 "cost_per_square_metre_minor": 1000, "tare_per_square_metre": "500",
                                                 "outer_allowance": {"length": "3", "width": "3", "height": "3"}}}]}
            """));
        var container = (JsonObject)((JsonArray)result.Require("containers")).Items.Single();
        Assert.Equal("feasible", ((JsonString)result.Require("status")).Value);
        Assert.Equal("""{"height":{"ticks":3360000,"unit":"mm","value":"210"},"length":{"ticks":3360000,"unit":"mm","value":"210"},"width":{"ticks":3360000,"unit":"mm","value":"210"}}""",
            CanonicalJsonWriter.Write(container.Require("inner_dimensions")));
        Assert.Equal("""{"height":{"ticks":3408000,"unit":"mm","value":"213"},"length":{"ticks":3408000,"unit":"mm","value":"213"},"width":{"ticks":3408000,"unit":"mm","value":"213"}}""",
            CanonicalJsonWriter.Write(container.Require("outer_dimensions")));
        Assert.Equal("""{"height":{"ticks":3200000,"unit":"mm","value":"200"},"length":{"ticks":3200000,"unit":"mm","value":"200"},"width":{"ticks":3200000,"unit":"mm","value":"200"}}""",
            CanonicalJsonWriter.Write(container.Require("achieved_dimensions")));
        Assert.Equal("""{"cost_minor":305,"tare_weight":{"ticks":1058400000,"unit":"g","value":"132.3"}}""",
            CanonicalJsonWriter.Write(container.Require("made_to_measure")));
    }

    [Theory]
    [InlineData("axles", """[{"position": "20"}, {"position": "180"}]""", "container.made_to_measure+axles")]
    [InlineData("void_fill_reserve_ratio", "0.1", "container.made_to_measure+void_fill_reserve_ratio")]
    public void ACombinationWithNoMeaningForACutBoxIsUnsupported(string field, string value, string named)
    {
        var request = """
            {"items": [{"id": "cube", "dimensions": {"length": "10", "width": "10", "height": "10"}}],
             "containers": [{"id": "machine", "inner_dimensions": {"length": "200", "width": "100", "height": "200"},
                             "FIELD": VALUE,
                             "made_to_measure": {"minimum": {"length": "100", "width": "100", "height": "100"},
                                                 "increment": {"length": "50", "width": "50", "height": "50"}}}]}
            """.Replace("FIELD", field, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal);
        var refusal = Assert.Throws<UnsupportedFeatureException>(() => JsonApi.PackFromJson(JsonReader.Parse(request)));
        Assert.Contains($"does not yet implement {named}", refusal.Message, StringComparison.Ordinal);
    }
}
