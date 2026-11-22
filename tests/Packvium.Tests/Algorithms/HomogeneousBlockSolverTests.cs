using System.Linq;
using Packvium.Algorithms;
using Packvium.Domain;
using Xunit;

namespace Packvium.Tests.Algorithms;

/// <summary>When the block solver steps aside for the per-item search (<c>test_solvers.py</c>).</summary>
public sealed class HomogeneousBlockSolverTests
{
    private static readonly Container Box = new("box", Dimensions.Mm("100", "100", "200"), quantity: 1);

    private static string Layout(SingleContainerSolution solution) =>
        string.Join("|", solution.State.Placements.Select(p =>
            $"{p.Instance.Id}@{p.EnvelopeOrigin.X},{p.EnvelopeOrigin.Y},{p.EnvelopeOrigin.Z}:{p.SupportRatio}"));

    private static SingleContainerSolution Pack(ISingleContainerSolver solver, Item item, PackingConfig config) =>
        solver.PackOne(Box, 1, item.Instances(), config, new SearchStats(), new Deadline(60_000));

    [Fact]
    public void CompressibleItemsAreLeftToThePerItemSearch()
    {
        var sponge = new Item("sponge", Dimensions.Mm("100", "100", "100"), quantity: 2,
            shapeType: ShapeType.Compressible, compressionRatioPpm: 200_000, maxCompressionPressureKpa: 50);
        var config = PackingConfig.Quality();
        var solution = Pack(new HomogeneousBlockSolver(), sponge, config);
        Assert.Equal(2, solution.State.Placements.Count);
        Assert.Equal(Layout(Pack(new ExtremePointSolver(), sponge, config)), Layout(solution));
    }

    [Fact]
    public void ARequestedSupportRatioIsLeftToThePerItemSearch()
    {
        var cube = new Item("cube", Dimensions.Mm("50", "50", "50"), quantity: 8);
        var config = new PackingConfig(SolverProfile.Quality, 60_000, minimumSupportRatio: 0.5);
        var solution = Pack(new HomogeneousBlockSolver(), cube, config);
        Assert.Equal(8, solution.State.Placements.Count);
        Assert.Equal(Layout(Pack(new ExtremePointSolver(), cube, config)), Layout(solution));
    }
}
