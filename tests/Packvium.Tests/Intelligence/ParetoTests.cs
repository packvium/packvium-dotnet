using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Intelligence;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>
/// Dominance and the report built from it. The module's one job: never collapse a trade-off into
/// a single number. A result dominates another only when it is no worse on every named axis and
/// strictly better on at least one, and when two survive that, the report says so instead of picking.
/// </summary>
public sealed class ParetoTests
{
    /// <summary>Cost is cheaper-is-better, utilisation higher-is-better; stating both is mandatory.</summary>
    internal static readonly Dictionary<string, bool> Directions = new(StringComparer.Ordinal)
    {
        ["cost"] = false,
        ["utilisation"] = true,
    };

    internal static Dictionary<string, double> Metrics(double cost, double utilisation) => new(StringComparer.Ordinal)
    {
        ["cost"] = cost,
        ["utilisation"] = utilisation,
    };

    private static Dictionary<string, double> Cost(double cost) => new(StringComparer.Ordinal) { ["cost"] = cost };

    private static readonly Dictionary<string, bool> CheaperIsBetter = new(StringComparer.Ordinal) { ["cost"] = false };

    private static CandidateResult Candidate(string engine, double cost, double utilisation, string profile = "balanced") =>
        new(profile, engine, Metrics(cost, utilisation));

    // ------------------------------------------------------------------- dominance

    [Fact]
    public void BetterOnBothAxesDominates() => Assert.True(Pareto.Dominates(Metrics(8, 0.9), Metrics(10, 0.7), Directions));

    [Fact]
    public void WorseOnBothAxesDoesNot() => Assert.False(Pareto.Dominates(Metrics(12, 0.6), Metrics(10, 0.7), Directions));

    /// <summary>Cheaper but sparser: the case the module exists for. Ranking these is a claim about the caller's priorities.</summary>
    [Fact]
    public void AGenuineTradeOffDominatesInNeitherDirection()
    {
        var (cheap, dense) = (Metrics(8, 0.6), Metrics(12, 0.9));
        Assert.False(Pareto.Dominates(cheap, dense, Directions));
        Assert.False(Pareto.Dominates(dense, cheap, Directions));
    }

    /// <summary>"No worse on every axis" holds; "strictly better somewhere" does not.</summary>
    [Fact]
    public void IdenticalMetricsDominateInNeitherDirection() =>
        Assert.False(Pareto.Dominates(Metrics(10, 0.8), Metrics(10, 0.8), Directions));

    [Fact]
    public void EqualOnOneAxisAndBetterOnTheOtherDominates() =>
        Assert.True(Pareto.Dominates(Metrics(10, 0.9), Metrics(10, 0.8), Directions));

    /// <summary>Nothing about the numbers says which way is better, which is why the map has no default.</summary>
    [Fact]
    public void DirectionIsReadFromTheMapRatherThanGuessed()
    {
        Assert.True(Pareto.Dominates(Cost(8), Cost(12), CheaperIsBetter));
        Assert.True(Pareto.Dominates(Cost(12), Cost(8), new Dictionary<string, bool>(StringComparer.Ordinal) { ["cost"] = true }));
    }

    [Fact]
    public void AnAxisMissingFromEitherSideIsRefused()
    {
        var left = Assert.Throws<InconsistentAxesException>(() => Pareto.Dominates(Cost(8), Metrics(10, 0.7), Directions));
        Assert.Equal("missing metric axis/axes: ['utilisation']", left.Message);
        var right = Assert.Throws<InconsistentAxesException>(() => Pareto.Dominates(Metrics(8, 0.9), Cost(10), Directions));
        Assert.Contains("utilisation", right.Message, StringComparison.Ordinal);
    }

    /// <summary>Extra metrics are data the caller did not ask to rank on, not an error.</summary>
    [Fact]
    public void AnAxisAbsentFromTheDirectionMapIsSimplyNotCompared()
    {
        var a = new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 8, ["weight"] = 99 };
        var b = new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 10, ["weight"] = 1 };
        Assert.True(Pareto.Dominates(a, b, CheaperIsBetter));
    }

    /// <summary>NaN compares false both ways, so an unguarded axis would tie and let garbage onto the frontier.</summary>
    [Fact]
    public void ANaNOnEitherSideIsRefusedRatherThanCountedEqual()
    {
        var left = Assert.Throws<NonFiniteMetricException>(() => Pareto.Dominates(Cost(double.NaN), Cost(1), CheaperIsBetter));
        Assert.Equal("left candidate: metric 'cost' is NaN and cannot be compared", left.Message);
        var right = Assert.Throws<NonFiniteMetricException>(() => Pareto.Dominates(Cost(1), Cost(double.NaN), CheaperIsBetter));
        Assert.StartsWith("right candidate: metric 'cost'", right.Message, StringComparison.Ordinal);
    }

    /// <summary>Deliberately allowed: a caller encoding "unpriceable" as an infinite cost gets the answer they mean.</summary>
    [Fact]
    public void InfinitiesCompareAsTheEndsOfTheNumberLine()
    {
        Assert.True(Pareto.Dominates(Cost(5), Cost(double.PositiveInfinity), CheaperIsBetter));
        Assert.False(Pareto.Dominates(Cost(double.PositiveInfinity), Cost(5), CheaperIsBetter));
        Assert.False(Pareto.Dominates(Cost(double.PositiveInfinity), Cost(double.PositiveInfinity), CheaperIsBetter));
    }

    // ------------------------------------------------------------------ the report

    [Fact]
    public void OneDominantCandidateBecomesTheNamedWinner()
    {
        var reports = Pareto.GenerateReport([Candidate("python", 10, 0.7), Candidate("php", 8, 0.9)], Directions);
        var report = Assert.Single(reports);
        Assert.Equal("php", report.Winner);
        Assert.Equal(["php"], report.ParetoOptimal);
        Assert.Equal(["python"], report.Dominated);
    }

    [Fact]
    public void ATradeOffLeavesNoWinnerAndListsBoth()
    {
        var report = Pareto.GenerateReport([Candidate("python", 8, 0.6), Candidate("php", 12, 0.9)], Directions)[0];
        Assert.Null(report.Winner);
        Assert.Equal(["php", "python"], report.ParetoOptimal);
        Assert.Empty(report.Dominated);
    }

    /// <summary>Nothing can dominate a lone candidate; worth pinning because it reads like evidence that it beat something.</summary>
    [Fact]
    public void ASingleCandidateIsOptimalByDefault()
    {
        var report = Pareto.GenerateReport([Candidate("python", 10, 0.7)], Directions)[0];
        Assert.Equal(["python"], report.ParetoOptimal);
        Assert.Equal("python", report.Winner);
    }

    [Fact]
    public void ThreeCandidatesSplitIntoFrontierAndDominated()
    {
        var report = Pareto.GenerateReport(
            [Candidate("cheap", 8, 0.6), Candidate("dense", 12, 0.9), Candidate("worse", 14, 0.5)], Directions)[0];
        Assert.Equal(["cheap", "dense"], report.ParetoOptimal);
        Assert.Equal(["worse"], report.Dominated);
        Assert.Null(report.Winner);
    }

    [Fact]
    public void EachProfileGetsItsOwnReportSortedByName()
    {
        var reports = Pareto.GenerateReport(
            [Candidate("python", 10, 0.7, profile: "quality"), Candidate("php", 8, 0.9, profile: "fast")], Directions);
        Assert.Equal(["fast", "quality"], reports.Select(report => report.Profile));
    }

    /// <summary>Report order cannot depend on input order, or two runs of one comparison would print differently.</summary>
    [Fact]
    public void FrontierAndDominatedAreEachSorted()
    {
        var forward = Pareto.GenerateReport([Candidate("zeta", 8, 0.6), Candidate("alpha", 12, 0.9)], Directions);
        var reverse = Pareto.GenerateReport([Candidate("alpha", 12, 0.9), Candidate("zeta", 8, 0.6)], Directions);
        Assert.Equal(["alpha", "zeta"], forward[0].ParetoOptimal);
        Assert.Equal(forward[0].ParetoOptimal, reverse[0].ParetoOptimal);
    }

    /// <summary>Code-point order, not UTF-16 order: a character above U+FFFF sorts after U+E000..U+FFFF, as Python sorts it.</summary>
    [Fact]
    public void NamesAreSortedByCodePoint()
    {
        var report = Pareto.GenerateReport([Candidate("\U0001F4E6", 8, 0.6), Candidate("～", 12, 0.9)], Directions)[0];
        Assert.Equal(["～", "\U0001F4E6"], report.ParetoOptimal);
    }

    [Fact]
    public void NoCandidatesProduceNoReports() => Assert.Empty(Pareto.GenerateReport([], Directions));

    [Fact]
    public void TwoResultsForOneEngineInOneProfileAreRefused()
    {
        var refusal = Assert.Throws<ParetoReportException>(() =>
            Pareto.GenerateReport([Candidate("python", 10, 0.7), Candidate("python", 8, 0.9)], Directions));
        Assert.Equal("profile 'balanced' has more than one result for the same engine", refusal.Message);
    }

    /// <summary>With no axes there is no dominance, so every candidate would be "optimal".</summary>
    [Fact]
    public void AnEmptyDirectionMapIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() =>
            Pareto.GenerateReport([Candidate("python", 10, 0.7)], new Dictionary<string, bool>()));
        Assert.Contains("at least one metric axis", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Measured by the reference before the refusal existed: an all-NaN candidate came back optimal.</summary>
    [Fact]
    public void ANaNCandidateCannotReachTheFrontier() =>
        Assert.Throws<NonFiniteMetricException>(() =>
            Pareto.GenerateReport([Candidate("python", 10, 0.7), Candidate("broken", double.NaN, double.NaN)], Directions));

    // ------------------------------------------------ the report type guards its own claims

    [Fact]
    public void AWinnerMustBeOnTheFrontier()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ProfileReport("fast", ["php"], ["python"], winner: "python"));
        Assert.Contains("must be one of the Pareto-optimal", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWinnerCannotBeNamedWhenTwoSurvive()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new ProfileReport("fast", ["php", "python"], [], winner: "php"));
        Assert.Contains("exactly one candidate", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWinnerAlongsideAFrontierOfTwoIsWellFormed() =>
        Assert.Null(new ProfileReport("fast", ["php", "python"], []).Winner);
}
