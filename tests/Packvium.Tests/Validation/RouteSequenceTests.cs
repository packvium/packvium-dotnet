using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Validation;
using Xunit;

namespace Packvium.Tests.Validation;

/// <summary>
/// The loading and unloading orders of <c>packing_sequence.py</c>, each expected order the tag's
/// Python answer for the same boxes.
/// </summary>
public sealed class RouteSequenceTests
{
    private static readonly Dimensions Container = Dimensions.OfTicks(20, 10, 20);

    private static AxisAlignedBox Box(long x, long z) => new(new Point(x, 0, z), Dimensions.OfTicks(10, 10, 10));

    private static readonly List<AxisAlignedBox> Stacked = [Box(0, 0), Box(0, 10)];

    private static readonly List<AxisAlignedBox> SideBySide = [Box(0, 0), Box(10, 0)];

    [Fact]
    public void ASupporterIsLoadedBeforeWhatRestsOnIt()
    {
        Assert.Equal([0, 1], RouteSequence.SafeLoadingOrder(Stacked, Container));
        Assert.Equal([1, 0], RouteSequence.SafeRemovalOrder(Stacked, Container));
    }

    /// <summary>The trap a forward greedy loader falls into: with one door, the far box goes in first.</summary>
    [Fact]
    public void WithOneDoorTheFarBoxIsLoadedFirst()
    {
        Assert.Equal([1, 0], RouteSequence.SafeLoadingOrder(SideBySide, Container, ["-x"]));
        Assert.Equal([0, 1], RouteSequence.SafeRemovalOrder(SideBySide, Container, ["-x"]));
    }

    [Fact]
    public void NoSafeOrderIsReportedWithEveryStuckPlacement()
    {
        var error = Assert.Throws<SequenceException>(() => RouteSequence.SafeLoadingOrder(Stacked, Container, ["-z"]));
        Assert.Equal("no safe order exists: placements [0, 1] are mutually blocking", error.Message);
    }

    [Fact]
    public void AReplayRefusesAnOrderThatLoadsABoxBeforeItsSupporter()
    {
        var error = Assert.Throws<SequenceReplayException>(() => RouteSequence.ReplayLoadingOrder(Stacked, Container, [1, 0]));
        Assert.Equal("step 0: placement 1 is not safe there (a supporter has not been loaded yet)", error.Message);
        Assert.Throws<SequenceReplayException>(() => RouteSequence.ReplayRemovalOrder(Stacked, Container, [0]));
    }

    [Fact]
    public void AnUnknownDirectionIsRefused() =>
        Assert.Throws<InvalidDirectionException>(() => RouteSequence.SafeLoadingOrder(Stacked, Container, ["up"]));

    // ---------------------------------------------------------------- reachability

    private static AxisAlignedBox Cube(long x, long y, long z) => new(new Point(x, y, z), Dimensions.OfTicks(10, 10, 10));

    private static readonly Dimensions Row = Dimensions.OfTicks(20, 10, 10);

    private static readonly List<AxisAlignedBox> NearFar = [Cube(0, 0, 0), Cube(10, 0, 0)];

    [Fact]
    public void AnUnblockedItemIsReachableWithNoEvidence()
    {
        var reachability = RouteSequence.PlacementReachability(NearFar, Row, directions: ["-x", "+x"]);
        Assert.All(reachability, entry =>
        {
            Assert.True(entry.Reachable);
            Assert.Empty(entry.BlockedBySupport);
            Assert.Empty(entry.BlockedByNeighbors);
            Assert.Empty(entry.BlockedByRoute);
        });
    }

    /// <summary>A valid packing whose far item is unreachable: its only exit sweep runs through the near one.</summary>
    [Fact]
    public void ASingleExitDirectionMakesTheFarItemUnreachable()
    {
        var reachability = RouteSequence.PlacementReachability(NearFar, Row, directions: ["-x"]);
        Assert.True(reachability[0].Reachable);
        Assert.False(reachability[1].Reachable);
        Assert.Equal([0], reachability[1].BlockedByNeighbors);
        Assert.Empty(reachability[1].BlockedBySupport);
        Assert.Empty(reachability[1].BlockedByRoute);
        Assert.All(reachability, entry => Assert.Empty(entry.BlockedByRoute));
    }

    [Fact]
    public void AnItemBoxedInOnEverySideIsUnreachableAndEveryNeighbourIsNamed()
    {
        List<AxisAlignedBox> boxes =
        [
            Cube(10, 10, 10), Cube(0, 10, 10), Cube(20, 10, 10), Cube(10, 0, 10), Cube(10, 20, 10), Cube(10, 10, 0),
            Cube(10, 10, 20),
        ];
        var reachability = RouteSequence.PlacementReachability(boxes, Dimensions.OfTicks(30, 30, 30));
        Assert.False(reachability[0].Reachable);
        Assert.Equal([1, 2, 3, 4, 5, 6], reachability[0].BlockedByNeighbors);
        Assert.Equal([6], reachability[0].BlockedBySupport);
        Assert.Equal([true, true, true, true, false, true], reachability.Skip(1).Select(entry => entry.Reachable));
        Assert.Equal([0], reachability[5].BlockedBySupport);
    }

    [Fact]
    public void AChildStillPresentBlocksReachabilityEvenWithAClearSweep()
    {
        var reachability = RouteSequence.PlacementReachability(Stacked, Dimensions.OfTicks(10, 10, 20));
        Assert.False(reachability[0].Reachable);
        Assert.Equal([1], reachability[0].BlockedBySupport);
        Assert.Empty(reachability[0].BlockedByNeighbors);
        Assert.True(reachability[1].Reachable);
    }

    [Fact]
    public void RouteOrderMakesAnOtherwiseClearItemUnreachable()
    {
        var reachability = RouteSequence.PlacementReachability(NearFar, Row, [1, 0], ["-x", "+x"]);
        Assert.False(reachability[0].Reachable);
        Assert.Equal([1], reachability[0].BlockedByRoute);
        Assert.Empty(reachability[0].BlockedByNeighbors);
    }

    [Fact]
    public void ItemsAtTheSameStopNeverBlockEachOtherByRoute() =>
        Assert.All(RouteSequence.PlacementReachability(NearFar, Row, [0, 0], ["-x", "+x"]),
            entry => Assert.Empty(entry.BlockedByRoute));

    [Fact]
    public void AnEmptyDirectionSetLeavesEveryPlacementUnreachableWithNoNamedNeighbour()
    {
        var reachability = RouteSequence.PlacementReachability([Cube(0, 0, 0)], Dimensions.OfTicks(10, 10, 10), directions: []);
        Assert.False(reachability[0].Reachable);
        Assert.Empty(reachability[0].BlockedByNeighbors);
    }

    [Fact]
    public void ReachabilityIsReportedForEveryPlacementInInputOrder() =>
        Assert.Equal([0, 1, 2], RouteSequence.PlacementReachability([Cube(0, 0, 0), Cube(10, 0, 0), Cube(20, 0, 0)],
            Dimensions.OfTicks(30, 10, 10)).Select(entry => entry.Index));

    [Fact]
    public void ReachabilityNeedsOneStopPerPlacement() =>
        Assert.Throws<System.ArgumentException>(() => RouteSequence.PlacementReachability(NearFar, Row, [0]));
}
