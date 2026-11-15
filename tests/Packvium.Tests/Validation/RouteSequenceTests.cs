using System.Collections.Generic;
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
}
