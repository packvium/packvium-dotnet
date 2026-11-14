using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Intelligence;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>
/// Scenario runs and the order-by-order comparison built from them. A scenario is compared per
/// order, never as a blended delta: an aggregate would hide which orders improved and which paid
/// for the improvement.
/// </summary>
public sealed class SimulationTests
{
    private static readonly ScenarioVersionPin Pin = new(catalogVersion: 1);

    /// <summary>An evaluator that replays a table; a null row means the arm could not pack that order.</summary>
    internal static OrderEvaluator Arm(params (string Order, (double Cost, double Utilisation)? Measured)[] table) => (orderId, _) =>
    {
        var row = table.Single(entry => entry.Order == orderId);
        return row.Measured is { } measured
            ? new OrderRunResult(orderId, succeeded: true, metrics: ParetoTests.Metrics(measured.Cost, measured.Utilisation))
            : new OrderRunResult(orderId, succeeded: false, failureReason: "no carton fits");
    };

    private static OrderRunResult CostRun(string orderId) =>
        new(orderId, succeeded: true, metrics: new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 1.0 });

    // ------------------------------------------------------------ running a scenario

    [Fact]
    public void EveryOrderIsEvaluatedInTheGivenSequence()
    {
        var seen = new List<string>();
        var result = Scenarios.RunScenario("s", Pin, ["b", "a", "c"], (orderId, _) =>
        {
            seen.Add(orderId);
            return CostRun(orderId);
        });
        Assert.Equal(["b", "a", "c"], seen);
        Assert.Equal(["b", "a", "c"], result.OrderIds);
    }

    /// <summary>A scenario replays from stored versions, so the pin is what the evaluator resolves its catalog against.</summary>
    [Fact]
    public void ThePinReachesTheEvaluatorUnchanged()
    {
        var seen = new List<ScenarioVersionPin>();
        Scenarios.RunScenario("s", Pin, ["a"], (orderId, pin) =>
        {
            seen.Add(pin);
            return CostRun(orderId);
        });
        Assert.Equal([Pin], seen);
    }

    /// <summary>Reproducibility is this function's own determinism, not a separate mechanism.</summary>
    [Fact]
    public void TheSameArgumentsReproduceAnEqualResult()
    {
        var first = Scenarios.RunScenario("s", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", (12, 0.8))));
        var second = Scenarios.RunScenario("s", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", (12, 0.8))));
        Assert.Equal(first, second);
    }

    [Fact]
    public void AFailedOrderIsCarriedRatherThanDropped()
    {
        var result = Scenarios.RunScenario("s", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", null)));
        Assert.Equal([true, false], result.Runs.Select(run => run.Succeeded));
        Assert.Equal("no carton fits", result.Runs[1].FailureReason);
    }

    /// <summary>The run's metrics are an immutable copy: the caller's map can change afterwards without reaching the run.</summary>
    [Fact]
    public void MetricsAreReadOnlyOnceTheRunExists()
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 1.0 };
        var run = new OrderRunResult("a", succeeded: true, metrics: metrics);
        metrics["cost"] = 2.0;
        Assert.Equal(1.0, run.Metrics["cost"]);
    }

    // ---------------------------------------------------------- a run guards its own claims

    /// <summary>"It worked" with nothing measured is not a comparable result.</summary>
    [Fact]
    public void ASucceededRunMustReportAMetric()
    {
        var refusal = Assert.Throws<ArgumentException>(() =>
            new OrderRunResult("a", succeeded: true, metrics: new Dictionary<string, double>()));
        Assert.Contains("at least one metric", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRunMustSayWhy()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new OrderRunResult("a", succeeded: false));
        Assert.Contains("failure_reason", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stricter than the Pareto module, which accepts infinities: a measurement of infinite cost
    /// is a broken evaluator. (The reference's boolean case cannot arise: a metric is a double.)
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteMetricIsRefusedAtTheRun(double value)
    {
        var refusal = Assert.Throws<ArgumentException>(() =>
            new OrderRunResult("a", succeeded: true, metrics: new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = value }));
        Assert.Contains("finite", refusal.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- comparing two scenarios

    private static (ScenarioResult Baseline, ScenarioResult Treatment) Pair(
        (string, (double, double)?)[] baseline, (string, (double, double)?)[] treatment)
    {
        string[] orders = ["a", "b"];
        return (Scenarios.RunScenario("base", Pin, orders, Arm(baseline)), Scenarios.RunScenario("treat", Pin, orders, Arm(treatment)));
    }

    private static Dictionary<string, ProfileReport> Reports(ScenarioResult baseline, ScenarioResult treatment) =>
        Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions).ToDictionary(report => report.Profile, StringComparer.Ordinal);

    [Fact]
    public void OneReportPerOrder()
    {
        var (baseline, treatment) = Pair([("a", (10, 0.7)), ("b", (12, 0.8))], [("a", (8, 0.9)), ("b", (11, 0.85))]);
        Assert.Equal(["a", "b"], Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions).Select(report => report.Profile));
    }

    [Fact]
    public void ADominantArmIsNamedPerOrder()
    {
        var (baseline, treatment) = Pair([("a", (10, 0.7)), ("b", (12, 0.8))], [("a", (8, 0.9)), ("b", (14, 0.6))]);
        var reports = Reports(baseline, treatment);
        Assert.Equal("treatment", reports["a"].Winner);
        Assert.Equal("baseline", reports["b"].Winner);
    }

    /// <summary>Order b genuinely trades cost for density, and no single number can report that without deciding it.</summary>
    [Fact]
    public void ARealTradeOffOnOneOrderLeavesThatOrderWithoutAWinner()
    {
        var (baseline, treatment) = Pair([("a", (10, 0.7)), ("b", (12, 0.8))], [("a", (8, 0.9)), ("b", (14, 0.9))]);
        var reports = Reports(baseline, treatment);
        Assert.Equal("treatment", reports["a"].Winner);
        Assert.Null(reports["b"].Winner);
        Assert.Equal(["baseline", "treatment"], reports["b"].ParetoOptimal);
    }

    [Fact]
    public void AnOrderOnlyOneArmCouldPackReportsThatArmAlone()
    {
        var (baseline, treatment) = Pair([("a", (10, 0.7)), ("b", (12, 0.8))], [("a", (8, 0.9)), ("b", null)]);
        var report = Reports(baseline, treatment)["b"];
        Assert.Equal(["baseline"], report.ParetoOptimal);
        // Named winner, and not a win: there was only one candidate to be optimal.
        Assert.Equal("baseline", report.Winner);
    }

    [Fact]
    public void AnOrderNeitherArmCouldPackProducesNoReport()
    {
        var (baseline, treatment) = Pair([("a", (10, 0.7)), ("b", null)], [("a", (8, 0.9)), ("b", null)]);
        Assert.Equal(["a"], Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions).Select(report => report.Profile));
    }

    /// <summary>Comparing the overlap would let cohort mix manufacture a difference neither arm produced.</summary>
    [Fact]
    public void TwoDifferentCorporaAreRefusedRatherThanIntersected()
    {
        var baseline = Scenarios.RunScenario("base", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", (12, 0.8))));
        var treatment = Scenarios.RunScenario("treat", Pin, ["a", "c"], Arm(("a", (8, 0.9)), ("c", (9, 0.9))));
        var refusal = Assert.Throws<MismatchedOrderCorpusException>(() =>
            Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions));
        Assert.Contains("identical order corpus", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameOrdersInADifferentSequenceAreRefused()
    {
        var baseline = Scenarios.RunScenario("base", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", (12, 0.8))));
        var treatment = Scenarios.RunScenario("treat", Pin, ["b", "a"], Arm(("a", (8, 0.9)), ("b", (11, 0.9))));
        Assert.Throws<MismatchedOrderCorpusException>(() => Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions));
    }

    /// <summary>That is the point of a comparison: one pin per arm, the same orders.</summary>
    [Fact]
    public void TheArmsMayBePinnedToDifferentCatalogVersions()
    {
        var baseline = Scenarios.RunScenario("base", Pin, ["a"], Arm(("a", (10, 0.7))));
        var treatment = new ScenarioResult("treat", new ScenarioVersionPin(catalogVersion: 2), ["a"],
            [new OrderRunResult("a", succeeded: true, metrics: ParetoTests.Metrics(8, 0.9))]);
        Assert.Equal("treatment", Scenarios.CompareScenarios(baseline, treatment, ParetoTests.Directions)[0].Winner);
    }

    // ------------------------------------------------- a scenario reports its own confidence

    private static ScenarioResult Mixed() =>
        Scenarios.RunScenario("s", Pin, ["a", "b", "c", "d"], Arm(("a", (10, 0.7)), ("b", null), ("c", (12, 0.8)), ("d", (9, 0.9))));

    [Fact]
    public void TheCountsPartitionTheRuns()
    {
        var result = Mixed();
        Assert.Equal((3, 1), (result.SucceededCount, result.FailedCount));
        Assert.Equal(result.Runs.Length, result.SucceededCount + result.FailedCount);
    }

    [Fact]
    public void ConfidenceIsTheUsableShare() => Assert.Equal(0.75, Mixed().Confidence);

    [Fact]
    public void AWhollySuccessfulScenarioHasFullConfidence()
    {
        var result = Scenarios.RunScenario("s", Pin, ["a"], Arm(("a", (10, 0.7))));
        Assert.Equal(1.0, result.Confidence);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void AWhollyFailedScenarioHasNoConfidence()
    {
        var result = Scenarios.RunScenario("s", Pin, ["a"], Arm(("a", null)));
        Assert.Equal(0.0, result.Confidence);
        Assert.Equal(0, result.SucceededCount);
    }

    // ------------------------------------------------------ reaching one order's raw run

    [Fact]
    public void TheRunForAKnownOrderComesBack()
    {
        var result = Scenarios.RunScenario("s", Pin, ["a", "b"], Arm(("a", (10, 0.7)), ("b", (12, 0.8))));
        Assert.Equal(12.0, result.RawArtifact("b").Metrics["cost"]);
    }

    /// <summary>Returning null would push the mistake downstream into a metrics lookup.</summary>
    [Fact]
    public void AnUnknownOrderIsRefusedByName()
    {
        var result = Scenarios.RunScenario("s", Pin, ["a"], Arm(("a", (10, 0.7))));
        var refusal = Assert.Throws<ScenarioException>(() => result.RawArtifact("zzz"));
        Assert.Equal("no run recorded for order 'zzz'", refusal.Message);
    }

    // ------------------------------------------------------------------- the version pin

    [Fact]
    public void APinMustSetAtLeastOneField() =>
        Assert.Contains("at least one", Assert.Throws<ArgumentException>(() => new ScenarioVersionPin()).Message, StringComparison.Ordinal);

    [Fact]
    public void PolicyPinsAreSortedAndComparedByValue()
    {
        var first = new ScenarioVersionPin(policyVersions: [("zeta", 1), ("alpha", 2)]);
        var second = new ScenarioVersionPin(policyVersions: [("alpha", 2), ("zeta", 1)]);
        Assert.Equal([("alpha", 2), ("zeta", 1)], first.PolicyVersions);
        Assert.Equal(first, second);
    }

    [Fact]
    public void APinCannotNameTheSameRuleTwice() =>
        Assert.Throws<ArgumentException>(() => new ScenarioVersionPin(policyVersions: [("rule", 1), ("rule", 2)]));
}
