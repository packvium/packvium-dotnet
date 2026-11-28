using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Algorithms;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Packing;

/// <summary>
/// The behaviour 1.7 changes, with the expectations the tag's Python gives (<c>test_solver_edges.py</c>,
/// <c>test_block_lattice.py</c>, <c>test_constraints.py</c>, <c>test_axle_load.py</c> and
/// <c>test_request_objects.py</c>).
/// </summary>
public sealed class Release17BehaviourTests
{
    private static readonly long Mm = Length.TicksPerMm;

    private static List<ItemInstance> Instances(params Item[] items) => [.. items.SelectMany(item => item.Instances())];

    private static Item Box(string id, string length, string width, string height, int quantity = 1,
        string? groundContactRule = null) =>
        new(id, Dimensions.Mm(length, width, height), quantity: quantity, groundContactRule: groundContactRule);

    private static SingleContainerSolution Blocks(Container box, IReadOnlyList<ItemInstance> items, PackingConfig config) =>
        new HomogeneousBlockSolver().PackOne(box, 1, items, config, new SearchStats(), new Deadline(600_000));

    private static PackingConfig Greedy() =>
        new(SolverProfile.Balanced, containerPlanNodeLimit: 100_000);

    [Fact]
    public void TheQualityLookaheadFindsALayoutWithNothingOverhanging()
    {
        // One base and three tops in a 100 mm crate: the greedy pass sets the base on two tops.
        var crate = new Container("crate", Dimensions.Mm("100", "100", "100"), quantity: 1);
        var items = Instances(Box("base", "60", "60", "40"), Box("top", "40", "60", "50", 3));
        var greedy = Blocks(crate, items, Greedy());
        var looked = Blocks(crate, items, PackingConfig.Quality());
        Assert.Empty(looked.Unpacked);
        Assert.False(looked.TimeLimitReached);
        Assert.Equal([1.0], looked.State.Placements.Select(placement => placement.SupportRatio).Distinct());
        Assert.True(looked.State.MaxZ < greedy.State.MaxZ);
    }

    [Theory]
    [InlineData(150, 120, 100)]
    [InlineData(400, 300, 260)]
    [InlineData(237, 181, 143)]
    public void TheQualityLookaheadNeverRanksBelowTheGreedyPass(int length, int width, int height)
    {
        var box = new Container("box", Dimensions.OfTicks(length * Mm, width * Mm, height * Mm));
        var items = Instances(Box("t0", "37", "52", "29", 17), Box("t1", "61", "23", "44", 9),
            Box("t2", "20", "90", "35", 22));
        var looked = Blocks(box, items, PackingConfig.Quality());
        var passed = Blocks(box, items, Greedy());
        Assert.True((looked.Unpacked.Length, -looked.State.UsedVolumeTicks) is var first
                    && (passed.Unpacked.Length, -passed.State.UsedVolumeTicks) is var second
                    && (first.Item1 < second.Item1 || (first.Item1 == second.Item1 && first.Item2 <= second.Item2)));
    }

    [Fact]
    public void CoveredCountsOnlyBoxesTheCandidateActuallyRestsOn()
    {
        // `a` carries the front half of the candidate; `b` and `c` touch its two back corners with
        // zero overlap area, so those corners hang.
        var front = Box("a", "10", "5", "10").Instances()[0];
        var left = Box("b", "10", "10", "10").Instances()[0];
        var right = Box("c", "10", "10", "10").Instances()[0];
        var candidate = Box("top", "10", "10", "10", groundContactRule: "covered").Instances()[0];
        Placement Placed(ItemInstance instance, long x, long y) =>
            new(instance, new Point(x, y, 0), Rotation.LWH, instance.Dimensions, new Point(x, y, 0), instance.Dimensions, 1.0);
        ImmutableArray<Placement> placements = [Placed(front, 10 * Mm, 0), Placed(left, 0, 10 * Mm), Placed(right, 20 * Mm, 10 * Mm)];
        var context = new ConstraintContext(new Container("box", Dimensions.Mm("100", "100", "100")), placements, candidate,
            new Point(10 * Mm, 0, 10 * Mm), Rotation.LWH, candidate.Dimensions, candidate.Dimensions, false, false);
        var result = new SupportConstraint(0.0).Evaluate(context);
        Assert.False(result.Allowed);
        Assert.Equal(("ground_contact_violation", "covered: 1 supporter(s), not all four corners touched"),
            (result.Code, result.Detail));
    }

    private static LoadUnit Unit(long x, long y, long z, long length, long width, long height, string label,
        long? stackLimit = null) =>
        new(new AxisAlignedBox(new Point(x, y, z), Dimensions.OfTicks(length, width, height)), 0, null, stackLimit, label);

    [Fact]
    public void TheFirstBlockerNamedIsTheLowestIndexNotTheFirstReached()
    {
        LoadUnit[] scene =
        [
            Unit(0, 0, 0, 20, 10, 10, "base"), Unit(0, 0, 10, 10, 10, 10, "left"),
            Unit(10, 0, 10, 10, 10, 10, "right"), Unit(0, 0, 20, 10, 10, 10, "high"),
        ];
        Assert.Equal(("unloading_order_violation", "base blocked by right"),
            LoadPropagation.RouteOrderViolated(scene, [0, 0, 1, 2]));
    }

    /// <summary>
    /// A stack of sheets is a chain as tall as the stack. Counted on a thread with a 128 KiB stack,
    /// which a walk spending one frame per level would overflow long before the top.
    /// </summary>
    [Fact]
    public void AStackTallerThanASmallCallStackIsCountedWithoutRecursing()
    {
        const int levels = 3_000;
        var column = Enumerable.Range(0, levels).Select(level => Unit(0, 0, level, 10, 10, 1, $"s{level}", 50_000)).ToArray();
        int[] counts = [];
        (string, string)? exceeded = ("", "");
        var walker = new System.Threading.Thread(() =>
        {
            counts = LoadPropagation.StackedCounts(column);
            exceeded = LoadPropagation.StackLimitExceeded(column);
        }, 128 * 1024);
        walker.Start();
        walker.Join();
        Assert.Equal(Enumerable.Range(0, levels).Select(level => levels - 1 - level), counts);
        Assert.Null(exceeded);
    }

    [Fact]
    public void AxlePointsAlreadyOfferedAreNotOfferedAgain()
    {
        List<Point> ordinary = [new(0, 0, 0), new(5, 0, 0), new(0, 5, 0)];
        List<Point> axle = [new(7, 0, 0), new(5, 0, 0), new(7, 5, 0), new(7, 0, 0), new(0, 5, 0)];
        CandidateFinder.AppendUnseen(ordinary, axle);
        Assert.Equal([new(0, 0, 0), new(5, 0, 0), new(0, 5, 0), new(7, 0, 0), new(7, 5, 0)], ordinary);
    }

    [Fact]
    public void AFixedPlacementCountingFromZeroIsRefusedByPackWithItsPointer()
    {
        var cube = Box("cube", "10", "10", "10");
        var box = new Container("box", Dimensions.Mm("100", "100", "100"));
        var error = Assert.Throws<FixedPlacementException>(() => new Packer().Pack([cube], [box],
            [new FixedPlacement("cube", "box", Point.Origin, Rotation.LWH, containerInstance: 0)]));
        Assert.Equal(("malformed", "/fixed_placements/0/container_instance",
                "invalid_fixed_placement: fixed_placements[0].container_instance counts from 1"),
            (error.Reason, error.Field, error.Message));
    }

    [Fact]
    public void AnObjectRequestIsRefusedWhereTheJsonRequestWouldBe()
    {
        var cube = Box("cube", "10", "10", "10");
        var box = new Container("box", Dimensions.Mm("100", "100", "100"), tagLimits: [new("fragile", 1L << 54)]);
        var error = Assert.Throws<InvalidRequestException>(() => new Packer().Pack([cube], [box]));
        Assert.Equal(("above_maximum", "/containers/0/tag_limits/fragile", "invalid_request: /containers/0/tag_limits/fragile: must be at most 9007199254740991"),
            (error.Reason, error.Field, error.Message));
        var repeated = Assert.Throws<InvalidRequestException>(() => new Packer().Pack([cube, cube], [box]));
        Assert.Equal(("duplicate_id", "/items/1/id"), (repeated.Reason, repeated.Field));
    }

    [Fact]
    public void AModelRefusalCarriesTheSchemaReasonAndNoField()
    {
        var error = Assert.Throws<ObjectRefusal>(() => new Item("cube", Dimensions.Mm("10", "10", "10"), stopIndex: -1));
        Assert.Equal(("below_minimum", string.Empty, "stop_index must be a non-negative safe integer"),
            (error.Reason, error.Field, error.Message));
        Assert.Equal("not_allowed", Assert.Throws<ObjectRefusal>(() =>
            new PackingConfig(dimensionalWeightLengthUnit: "furlong")).Reason);
        Assert.Equal("invalid_value", Assert.Throws<ObjectRefusal>(() =>
            new Item("cube", Dimensions.Mm("10", "10", "10"), minimumSupportRatio: double.NaN)).Reason);
    }

    [Theory]
    [InlineData(" in ", "in")]
    [InlineData("Inches", "in")]
    [InlineData("tick", "mm")]
    [InlineData("CM", "cm")]
    public void AResultWithoutAnOutputBlockIsWrittenInTheRequestUnitsCode(string unit, string code) =>
        Assert.Equal(code, Length.OutputCode(unit));

    [Fact]
    public void ANullOutputUnitIsTheDefault()
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse("""
            {"units": {"length": "cm"}, "output": {"length_unit": null, "weight_unit": null},
             "items": [{"id": "b", "weight": "5", "dimensions": {"length": "1", "width": "1", "height": "1"}}],
             "containers": [{"id": "c", "inner_dimensions": {"length": "2", "width": "2", "height": "2"}}]}
            """));
        var placement = (JsonObject)((JsonArray)((JsonObject)((JsonArray)result.Require("containers")).Items[0])
            .Require("placements")).Items[0];
        Assert.Equal("cm", ((JsonString)((JsonObject)((JsonObject)placement.Require("position")).Require("x")).Require("unit")).Value);
        var weight = (JsonObject)((JsonObject)((JsonArray)result.Require("containers")).Items[0]).Require("gross_weight");
        Assert.Equal("g", ((JsonString)weight.Require("unit")).Value);
    }
}
