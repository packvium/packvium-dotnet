using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Algorithms;
using Packvium.Domain;
using Packvium.Objectives;
using Packvium.Packing;
using Packvium.Units;
using Packvium.Validation;
using Xunit;
using JsonNode = System.Text.Json.Nodes.JsonNode;

namespace Packvium.Tests.Packing;

/// <summary>
/// Weight balancing across already-packed containers (<c>test_rebalance.py</c>): an item
/// is never lost or duplicated, a move that would strand another item's support is declined, and
/// the spread actually narrows.
/// </summary>
public sealed class WeightRebalanceTests
{
    private const long TicksPerGram = 8_000_000;

    private const int Trials = 20;

    private static Item Cube(string id, string? grams = null, double minimumSupportRatio = 0) =>
        new(id, Dimensions.Mm("40", "40", "40"), grams is null ? default : Weight.Parse(grams), minimumSupportRatio: minimumSupportRatio);

    private static Container Box(string id, string length = "200", string width = "200", string height = "200") =>
        new(id, Dimensions.Mm(length, width, height));

    /// <summary>An item resting flat on the container floor, so support is trivially satisfied.</summary>
    private static Placement Floor(Item item, int sequence, long x, long y)
    {
        var origin = new Point(x, y, 0);
        return new Placement(new ItemInstance(item, sequence), origin, Rotation.LWH, item.Dimensions, origin, item.Dimensions);
    }

    /// <summary>An item resting exactly on top of <paramref name="below"/>, fully supported by it alone.</summary>
    private static Placement Stacked(Item item, int sequence, Placement below)
    {
        var origin = new Point(below.EnvelopeOrigin.X, below.EnvelopeOrigin.Y, below.EnvelopeOrigin.Z + below.EnvelopeDimensions.Height.Ticks);
        return new Placement(new ItemInstance(item, sequence), origin, Rotation.LWH, item.Dimensions, origin, item.Dimensions);
    }

    private static PackedContainer Packed(Container container, int sequence, params Placement[] placements) =>
        new(container, sequence, [.. placements]);

    private static List<long> Weights(IEnumerable<PackedContainer> containers) =>
        [.. containers.Select(packed => packed.PayloadWeight.Ticks)];

    private static long Spread(List<long> weights) => weights.Count == 0 ? 0 : weights.Max() - weights.Min();

    private static List<string> Moved(RebalanceResult outcome) => [.. outcome.Moves.Select(move => move.ItemId)];

    /// <summary>Every requested instance comes back exactly once, placed or explained.</summary>
    private static void AssertAccountingHolds(PackingRequest request, IEnumerable<PackedContainer> containers,
        IEnumerable<UnpackedItem> unpacked)
    {
        var placed = containers.SelectMany(packed => packed.Placements).Select(p => p.Instance.Id).ToList();
        Assert.Equal(placed.Count, placed.Distinct(StringComparer.Ordinal).Count());
        var expected = request.Items.SelectMany(item => item.Instances()).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(placed.Concat(unpacked.Select(u => u.Instance.Id))), "items were lost or fabricated");
    }

    private static void AssertValid(PackingRequest request, IReadOnlyList<PackedContainer> containers,
        IReadOnlyList<UnpackedItem> unpacked, PackingConfig config)
    {
        var report = IndependentSolutionValidator.Validate(request, containers, config.MinimumSupportRatio, config.Clearance, unpacked);
        Assert.True(report.Valid, string.Join(", ", report.Issues.Select(issue => $"{issue.Code}: {issue.Detail}")));
    }

    /// <summary>The hand-computed 6 kg / 1 kg scene, optionally with the lone item in a container type of its own.</summary>
    private static (PackingRequest Request, PackedContainer[] Packed) HeavyLightAlone(Container? aloneBox = null)
    {
        var heavy = Cube("heavy", "5000 g");
        var light = Cube("light", "1000 g");
        var alone = Cube("alone", "1000 g");
        var box = Box("box");
        var other = aloneBox ?? box;
        var request = new PackingRequest([heavy, light, alone], other == box ? [box] : [box, other]);
        return (request, [
            Packed(box, 1, Floor(heavy, 1, 0, 0), Floor(light, 1, 50, 0)),
            Packed(other, other == box ? 2 : 1, Floor(alone, 1, 0, 0)),
        ]);
    }

    /// <summary>
    /// Moving the 5 kg item only flips which side is heavier, so the 1 kg item moves instead:
    /// a 5 kg / 2 kg split. The move is the suite's shared scene fixture where it is present.
    /// </summary>
    [Fact]
    public void AMoveStrictlyReducesThePayloadSpreadBetweenTwoContainers()
    {
        var (request, packed) = HeavyLightAlone();
        Assert.Equal(5000 * TicksPerGram, Spread(Weights(packed)));
        var config = new PackingConfig();
        var outcome = WeightRebalance.Rebalance(request, packed, [], config);
        var after = Weights(outcome.Containers);
        Assert.Equal([2000 * TicksPerGram, 5000 * TicksPerGram], after.Order());
        Assert.Equal(["light#1"], Moved(outcome));
        Assert.Equal(("box#1", "box#2"), (outcome.Moves[0].FromContainerId, outcome.Moves[0].ToContainerId));
        Assert.True(outcome.Improved);
        var layout = outcome.Containers.Select(c => c.Placements.Select(p => p.Instance.Id).ToList()).ToList();
        Assert.Equal([["heavy#1"], ["alone#1", "light#1"]], layout);
        var shared = Path.Combine(Tests.Conformance.ConformanceTests.CorpusDirectory(), "conformance", "scene", "rebalance-fixtures.json");
        if (File.Exists(shared))
        {
            var scene = JsonNode.Parse(File.ReadAllText(shared))!["cases"]![0]!;
            var move = scene["expected_move"]!;
            Assert.Equal((move["item_id"]!.GetValue<string>(), move["from_container_id"]!.GetValue<string>(),
                move["to_container_id"]!.GetValue<string>()), (outcome.Moves[0].ItemId, outcome.Moves[0].FromContainerId,
                outcome.Moves[0].ToContainerId));
            Assert.Equal(scene["expected_container_item_ids"]!.AsArray().Select(ids => ids!.AsArray().Select(id => id!.GetValue<string>()).ToList()).ToList(), layout);
        }
        AssertAccountingHolds(request, outcome.Containers, []);
        AssertValid(request, outcome.Containers, [], config);
    }

    /// <summary>Moving the base would leave the support-sensitive item above it floating, so the top item moves instead.</summary>
    [Fact]
    public void AMoveThatWouldStrandASupportedItemIsDeclined()
    {
        var baseItem = Cube("base", "5000 g");
        var top = Cube("top", "1000 g", minimumSupportRatio: 1.0);
        var box = Box("box");
        var request = new PackingRequest([baseItem, top], [box]);
        var below = Floor(baseItem, 1, 0, 0);
        PackedContainer[] packed = [Packed(box, 1, below, Stacked(top, 1, below)), Packed(box, 2)];
        Assert.Equal([6000 * TicksPerGram, 0], Weights(packed));
        var config = new PackingConfig();
        var outcome = WeightRebalance.Rebalance(request, packed, [], config);
        Assert.Equal(["base#1"], outcome.Containers.First(c => c.Id == "box#1").Placements.Select(p => p.Instance.Id));
        Assert.Equal([1000 * TicksPerGram, 5000 * TicksPerGram], Weights(outcome.Containers).Order());
        Assert.Equal(["top#1"], Moved(outcome));
        AssertAccountingHolds(request, outcome.Containers, []);
        AssertValid(request, outcome.Containers, [], config);
    }

    [Fact]
    public void ACompactLatticeCanSupplyAnItemWithoutLosingItsOtherPlacements()
    {
        var box = Box("box");
        var cube = new Item("cube", Dimensions.Mm("40", "40", "40"), Weight.Parse("1000 g"), quantity: 3);
        var lattice = new LatticeSummary(cube.Id, Rotation.LWH, cube.Dimensions, cube.Dimensions,
            3, 1, cube.Dimensions.Height.Ticks, 0, 3, cube.Weight.Ticks);
        PackedContainer[] packed = [
            new(box, 1, [], lattice, [.. cube.Instances()]),
            Packed(box, 2),
        ];
        var request = new PackingRequest([cube], [box]);
        var config = new PackingConfig();

        var outcome = WeightRebalance.Rebalance(request, packed, [], config, maxMoves: 1);

        Assert.Single(outcome.Moves);
        Assert.Equal([2000 * TicksPerGram, 1000 * TicksPerGram], Weights(outcome.Containers));
        AssertAccountingHolds(request, outcome.Containers, []);
        AssertValid(request, outcome.Containers, [], config);
        Assert.Empty(packed[0].Placements);
    }

    [Fact]
    public void ASingleContainerHasNothingToRebalance()
    {
        var solo = Cube("solo", "500 g");
        var box = Box("box");
        PackedContainer[] packed = [Packed(box, 1, Floor(solo, 1, 0, 0))];
        var outcome = WeightRebalance.Rebalance(new PackingRequest([solo], [box]), packed);
        Assert.Empty(outcome.Moves);
        Assert.False(outcome.Improved);
        Assert.Equal(packed, outcome.Containers);
    }

    [Fact]
    public void AnAlreadyBalancedPairIsLeftUntouched()
    {
        var a = Cube("a", "1000 g");
        var b = Cube("b", "1000 g");
        var box = Box("box");
        PackedContainer[] packed = [Packed(box, 1, Floor(a, 1, 0, 0)), Packed(box, 2, Floor(b, 1, 0, 0))];
        var outcome = WeightRebalance.Rebalance(new PackingRequest([a, b], [box]), packed);
        Assert.Empty(outcome.Moves);
        Assert.Equal(packed, outcome.Containers);
    }

    /// <summary>
    /// A plausible order over one container type whose payload ceiling forces a split. The scenes
    /// come from a seeded <see cref="Random"/>, not Python's generator, so they are this suite's own.
    /// </summary>
    private static (List<Item> Items, List<Container> Containers) Generate(int seed)
    {
        var random = new Random(seed);
        var items = new List<Item>();
        var count = random.Next(2, 7);
        for (var index = 0; index < count; index++)
        {
            var sides = Enumerable.Range(0, 3).Select(_ => (10 + 5 * random.Next(0, 6)).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            items.Add(new Item($"i{index}", Dimensions.Mm(sides[0], sides[1], sides[2]),
                Weight.Parse($"{random.Next(100, 900)} g"), quantity: random.Next(1, 4)));
        }
        var container = new Container("c0", Dimensions.Mm("150", "150", "150"), quantity: random.Next(2, 5),
            maxPayload: Weight.Parse($"{random.Next(1, 3)} kg"));
        return (items, [container]);
    }

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, Trials)];

    /// <summary>Holds however many containers an order lands in, one included.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ARebalanceNeverLosesOrDuplicatesAnItemNorWidensTheSpread(int seed)
    {
        var (items, containers) = Generate(seed);
        var request = new PackingRequest(items, containers);
        var result = new Packer(PackingConfig.Balanced(250)).Pack(items, containers);
        var config = PackingConfig.Balanced(200);
        var outcome = WeightRebalance.Rebalance(request, result.Containers, result.Unpacked, config);
        AssertAccountingHolds(request, outcome.Containers, result.Unpacked);
        AssertValid(request, outcome.Containers, result.Unpacked, config);
        var before = Weights(result.Containers);
        var after = Weights(outcome.Containers);
        Assert.True(Spread(after) <= Spread(before));
        if (outcome.Improved)
        {
            Assert.NotEqual(before, after);
        }
    }

    private static readonly Item Bricks = new("brick", Dimensions.Mm("100", "100", "100"), Weight.Parse("1000g"), quantity: 4);

    private static readonly Container Wide = new("wide", Dimensions.Mm("300", "100", "100"), rateTable: new RateTable([4_000], [500]));

    private static readonly Container Narrow = new("narrow", Dimensions.Mm("400", "100", "100"), rateTable: new RateTable([1_500], [300]));

    private static PackingConfig LandedCost(long divisor = 5_000) => new(objective: "lowest_landed_cost",
        dimensionalWeightDivisor: divisor, dimensionalWeightLengthUnit: "cm", dimensionalWeightWeightUnit: "kg");

    /// <summary>The only spread-improving move would bill the lighter box past its card, so it is not made.</summary>
    [Fact]
    public void ARebalanceMoveNeverPricesAContainerPastItsBracket()
    {
        var config = LandedCost();
        var result = new Packer(config).Pack([Bricks], [Wide, Narrow]);
        Assert.Equal(["wide", "narrow"], result.Containers.Select(c => c.Container.Id));
        var request = new PackingRequest([Bricks], [Wide, Narrow]);
        var rebalanced = WeightRebalance.Rebalance(request, result.Containers, result.Unpacked, config);
        Assert.Empty(rebalanced.Moves);
        Assert.Equal(["wide", "narrow"], rebalanced.Containers.Select(c => c.Container.Id));
        // The veto is objective-gated: under a plain config the same packing makes the move.
        Assert.Single(WeightRebalance.Rebalance(request, result.Containers, result.Unpacked, new PackingConfig()).Moves);
    }

    [Fact]
    public void ARebalanceRefusesAnUnpriceableInput()
    {
        var config = LandedCost();
        var result = new Packer(config).Pack([Bricks], [Wide, Narrow]);
        var stingy = new Container("wide", Dimensions.Mm("300", "100", "100"), rateTable: new RateTable([1_000], [500]));
        var reshaped = result.Containers.Select(c => new PackedContainer(c.Container.Id == "wide" ? stingy : c.Container,
            c.Sequence, c.Placements)).ToList();
        var refusal = Assert.Throws<UnratedWeightException>(() =>
            WeightRebalance.Rebalance(new PackingRequest([Bricks], [stingy, Narrow]), reshaped, result.Unpacked, config));
        Assert.Contains("no published price", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Rebalance admits exactly what pack admits, a container the packing did not use included.</summary>
    [Fact]
    public void ARebalanceAppliesTheSameLandedCostAdmissionAsPack()
    {
        var parcel = new Item("parcel", Dimensions.Mm("100", "100", "100"), Weight.Parse("500g"));
        var rated = new Container("rated", Dimensions.Mm("200", "200", "200"), rateTable: new RateTable([2_000], [500]));
        var valid = LandedCost(8_000);
        var result = new Packer(valid).Pack([parcel], [rated]);
        var missingDivisor = Assert.Throws<UnknownObjectiveException>(() => WeightRebalance.Rebalance(
            new PackingRequest([parcel], [rated]), result.Containers, result.Unpacked, new PackingConfig(objective: "lowest_landed_cost")));
        Assert.Contains("dimensional_weight_divisor", missingDivisor.Message, StringComparison.Ordinal);
        var untabled = new Container("untabled", Dimensions.Mm("300", "300", "300"));
        var unrated = Assert.Throws<UnknownObjectiveException>(() => WeightRebalance.Rebalance(
            new PackingRequest([parcel], [rated, untabled]), result.Containers, result.Unpacked, valid));
        Assert.Contains("rate_table on every container; 'untabled'", unrated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMoveBudgetCapsHowManyMovesAreCommitted()
    {
        var (request, packed) = HeavyLightAlone();
        Assert.Equal(["light#1"], Moved(WeightRebalance.Rebalance(request, packed, [], new PackingConfig(), maxMoves: 1)));
    }

    [Fact]
    public void ADestinationWithNoRoomLeftReceivesNothing()
    {
        var (request, packed) = HeavyLightAlone(Box("snug", "40", "40", "40"));
        var outcome = WeightRebalance.Rebalance(request, packed, [], new PackingConfig());
        Assert.Empty(outcome.Moves);
        Assert.Equal(packed, outcome.Containers);
    }

    [Fact]
    public void AnItemMovedOntoAFullFloorIsStackedAndStaysSupported()
    {
        var (request, packed) = HeavyLightAlone(Box("tower", "40", "40", "80"));
        var config = new PackingConfig();
        var outcome = WeightRebalance.Rebalance(request, packed, [], config);
        Assert.Equal(["light#1"], Moved(outcome));
        var moved = outcome.Containers.First(c => c.Id == "tower#1").Placements.First(p => p.Instance.Id == "light#1");
        Assert.Equal(40 * 16_000, moved.Position.Z);
        Assert.Equal(1.0, moved.SupportRatio);
        AssertAccountingHolds(request, outcome.Containers, []);
        AssertValid(request, outcome.Containers, [], config);
    }

    [Fact]
    public void AWeightlessItemIsNeverMovedSinceItCannotChangeTheSpread()
    {
        var heavy = Cube("heavy", "5000 g");
        var feather = Cube("feather");
        var alone = Cube("alone", "1000 g");
        var box = Box("box");
        PackedContainer[] packed = [Packed(box, 1, Floor(heavy, 1, 0, 0), Floor(feather, 1, 50, 0)), Packed(box, 2, Floor(alone, 1, 0, 0))];
        Assert.Empty(WeightRebalance.Rebalance(new PackingRequest([heavy, feather, alone], [box]), packed).Moves);
    }

    /// <summary>A deadline that expires once the round has started reads as no move, not an error.</summary>
    [Fact]
    public void ADeadlineReachedMidSearchDeclinesTheMoveInsteadOfFailing()
    {
        var readings = new Queue<long>([0, 0]);
        var deadline = new Deadline(1000, clock: () => readings.Count > 0 ? readings.Dequeue() : 1_000_000_000_000_000_000);
        var (request, packed) = HeavyLightAlone();
        var outcome = WeightRebalance.Rebalance(request, packed, [], new PackingConfig(), 64, deadline);
        Assert.Empty(outcome.Moves);
        Assert.Equal(packed, outcome.Containers);
    }
}
