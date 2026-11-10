using System;
using System.Collections.Immutable;
using System.Linq;
using Packvium.Domain;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Domain;

/// <summary>Expected values were produced by the reference engine at v1.0.0, not derived by hand.</summary>
public sealed class ModelTests
{
    private static readonly Dimensions Cube = Dimensions.Mm("100", "100", "100");

    [Fact]
    public void KeepsTheFirstSpellingOfEachDistinctRotation() =>
        Assert.Equal([Rotation.LWH, Rotation.LHW, Rotation.HLW],
            Dimensions.Mm("100", "100", "300").UniqueRotations(Rotations.All).Select(pair => pair.Rotation).ToArray());

    [Fact]
    public void FloorsDimensionalWeight() =>
        Assert.Equal(26_106_035_683, DimensionalWeight.Of(Dimensions.Of("10", "10", "10", "in"), 139).Ticks);

    [Fact]
    public void KeepUprightNarrowsTheDefaultRotations() =>
        Assert.Equal([Rotation.LWH, Rotation.WLH], new Item("a", Dimensions.Mm("1", "1", "1"), keepUpright: true).AllowedRotations.ToArray());

    public static TheoryData<string, Func<Item>> InvalidItems => new()
    {
        { "item quantity must be positive", () => new Item("a", Cube, quantity: 0) },
        { "minimum_support_ratio must be between 0 and 1", () => new Item("a", Cube, minimumSupportRatio: 2.0) },
        { "at least one rotation must be allowed", () => new Item("a", Cube, keepUpright: true, allowedRotations: [Rotation.HLW]) },
        { "max_stacked_items must be at least 1", () => new Item("a", Cube, maxStackedItems: 0) },
        { "ground_contact_rule must be one of ['covered', 'free', 'multiple', 'single']", () => new Item("a", Cube, groundContactRule: "x") },
        { "a convex_hull item requires hull_vertices", () => new Item("a", Cube, shapeType: ShapeType.ConvexHull) },
        { "compression_ratio_ppm is not part of a rigid_cuboid item", () => new Item("a", Cube, compressionRatioPpm: 5) },
        {
            "nesting_height must be at least zero and strictly less than the item's own height",
            () => new Item("a", Cube, nestingHeight: Length.Mm("100"))
        },
    };

    [Theory]
    [MemberData(nameof(InvalidItems))]
    public void RefusesAnItemWithTheReferenceMessage(string message, Func<Item> build) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(build).Message);

    [Fact]
    public void DerivesPackedContainerFactsAsTheReference()
    {
        var item = new Item("cube", Cube, Weight.Parse("1 kg"), quantity: 3);
        var container = new Container("box", Dimensions.Mm("300", "100", "100"), tareWeight: Weight.Parse("100 g"));
        var placements = item.Instances().Take(2).Select((instance, index) =>
        {
            var origin = new Point(index * 1_600_000L, 0, 0);
            return new Placement(instance, origin, Rotation.LWH, item.Dimensions, origin, item.Dimensions);
        }).ToImmutableArray();
        var packed = new PackedContainer(container, 1, placements);
        Assert.Equal(16_000_000_000, packed.PayloadWeight.Ticks);
        Assert.Equal(16_800_000_000, packed.GrossWeight.Ticks);
        Assert.Equal(Int128.Parse("8192000000000000000"), packed.UsedVolume);
        Assert.Equal(0.6666666666666666, packed.Utilization);
        Assert.Equal(333_333, packed.CentreOfMassOffsetPpm);
        Assert.Equal(1_600_000, packed.MaxZTicks);
    }

    [Fact]
    public void GradesProofsByReason()
    {
        Assert.Equal("proven", ReasonProof.ForReason("payload_exceeded").Level);
        Assert.Equal("unknown_due_to_limit", ReasonProof.ForReason("time_limit").Level);
        Assert.Equal("inferred", ReasonProof.ForReason("x").Level);
    }

    [Fact]
    public void CompressesAsTheReference()
    {
        Assert.Equal(300_000, Compression.RatioToPpm(0.3));
        Assert.Equal(1, Compression.RatioToPpm(0.0000005));
        var pressure = Compression.AppliedPressure(Weight.Parse("10 kg"), (Int128)16_000 * 16_000 * 10_000);
        Assert.Equal(196_133, pressure.Numerator);
        Assert.Equal(20_000, pressure.Denominator);
        Assert.Equal(1_443_094, Compression.EffectiveHeightTicks(1_600_000, 500_000, 50, pressure));
    }

    [Fact]
    public void ChargesTheFirstBracketAtOrAboveTheWeight()
    {
        var table = new RateTable([1000, 5000], [500, 900], 600, 125);
        Assert.Equal(675, table.ChargeMinorOrNull(10));
        Assert.Equal(1013, table.ChargeMinorOrNull(3000));
        Assert.Null(table.ChargeMinorOrNull(6000));
    }

    [Fact]
    public void RefusesAnObstacleOutsideItsContainer()
    {
        var obstacle = new Obstacle("o", new AxisAlignedBox(Point.Origin, Dimensions.Mm("20", "1", "1")));
        var error = Assert.Throws<ArgumentException>(() =>
            new Container("c", Dimensions.Mm("10", "10", "10"), obstacles: [obstacle]));
        Assert.Equal("obstacle o lies outside container", error.Message);
    }
}
