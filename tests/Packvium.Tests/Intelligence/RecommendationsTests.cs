using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Packvium.Commerce;
using Packvium.Intelligence;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Intelligence;

/// <summary>
/// Proposing a change, and the single route from a proposal to a published one. A proposal is
/// built from a paired comparison, so an order only one arm could pack contributes to neither
/// mean; and a recommendation is never itself a mutation: it is handed no registry.
/// </summary>
public sealed class RecommendationsTests
{
    private static readonly ScenarioVersionPin Pin = new(catalogVersion: 1);

    private static readonly string[] Bounds = ["no placement the validator rejects"];

    private const string Rollback = "republish catalog version 1";

    private static ScenarioResult Scenario(string name, params (string Order, (double Cost, double Utilisation)? Measured)[] table) =>
        new(name, Pin, table.Select(row => row.Order), table.Select(row => row.Measured is { } measured
            ? new OrderRunResult(row.Order, succeeded: true, metrics: ParetoTests.Metrics(measured.Cost, measured.Utilisation))
            : new OrderRunResult(row.Order, succeeded: false, failureReason: "no carton fits")));

    private static ScenarioResult Single(string name, Dictionary<string, double> metrics) =>
        new(name, Pin, ["a"], [new OrderRunResult("a", succeeded: true, metrics: metrics)]);

    private static Recommendation? Propose(ScenarioResult baseline, ScenarioResult treatment, int minimumCohortSize = 1,
        double minimumConfidence = 0.0) =>
        Recommendations.Propose("rec-1", "cheaper cartons", baseline, treatment, Bounds, Rollback, minimumCohortSize, minimumConfidence);

    // ---------------------------------------------- a proposal is built from paired evidence

    [Fact]
    public void TheDeltasAreTheMeansOfThePairedOrders()
    {
        var proposal = Propose(Scenario("b", ("a", (10, 0.6)), ("b", (20, 0.8))), Scenario("t", ("a", (8, 0.7)), ("b", (16, 0.9))));
        Assert.NotNull(proposal);
        var deltas = proposal.ExpectedDeltas.ToDictionary(delta => delta.Metric, delta => (delta.BaselineMean, delta.TreatmentMean));
        Assert.Equal((15.0, 12.0), deltas["cost"]);
        Assert.Equal((0.7, 0.8), deltas["utilisation"]);
    }

    /// <summary>Report order cannot depend on map insertion order, or two identical comparisons would print differently.</summary>
    [Fact]
    public void DeltasAreSortedByMetricName()
    {
        var proposal = Propose(Scenario("b", ("a", (10, 0.6))), Scenario("t", ("a", (8, 0.7))));
        Assert.Equal(["cost", "utilisation"], proposal!.ExpectedDeltas.Select(delta => delta.Metric));
    }

    /// <summary>The paired rule: averaging each arm's survivors separately would make who survived look like performance.</summary>
    [Fact]
    public void AnOrderOnlyOneArmPackedIsExcludedFromBothMeans()
    {
        var proposal = Propose(Scenario("b", ("a", (10, 0.6)), ("b", (100, 0.1))), Scenario("t", ("a", (8, 0.7)), ("b", null)));
        Assert.Equal(["a"], proposal!.SupportingOrderIds);
        Assert.Equal(10.0, proposal.ExpectedDeltas.Single(delta => delta.Metric == "cost").BaselineMean);
    }

    [Fact]
    public void ConfidenceIsThePairedShareOfTheWholeCorpus()
    {
        var proposal = Propose(
            Scenario("b", ("a", (10, 0.6)), ("b", (10, 0.6)), ("c", (10, 0.6)), ("d", (10, 0.6))),
            Scenario("t", ("a", (8, 0.7)), ("b", (8, 0.7)), ("c", (8, 0.7)), ("d", null)));
        Assert.Equal(0.75, proposal!.Confidence);
    }

    [Fact]
    public void OnlyMetricsBothArmsReportedBecomeDeltas()
    {
        var proposal = Propose(Single("b", new(StringComparer.Ordinal) { ["cost"] = 10, ["weight"] = 5 }),
            Single("t", new(StringComparer.Ordinal) { ["cost"] = 8, ["volume"] = 3 }));
        Assert.Equal(["cost"], proposal!.ExpectedDeltas.Select(delta => delta.Metric));
    }

    // --------------------------------------------- a proposal is withheld rather than hedged

    [Fact]
    public void ACohortBelowTheFloorProducesNothing() =>
        Assert.Null(Propose(Scenario("b", ("a", (10, 0.6))), Scenario("t", ("a", (8, 0.7))), minimumCohortSize: 2));

    [Fact]
    public void AConfidenceBelowTheFloorProducesNothing() =>
        Assert.Null(Propose(Scenario("b", ("a", (10, 0.6)), ("b", (10, 0.6))), Scenario("t", ("a", (8, 0.7)), ("b", null)),
            minimumConfidence: 0.9));

    /// <summary>Full cohort, full confidence, and still no axis both sides measured: null, not an empty delta list.</summary>
    [Fact]
    public void ArmsThatShareNoMetricProduceNothing() =>
        Assert.Null(Propose(Single("b", new(StringComparer.Ordinal) { ["cost"] = 10 }),
            Single("t", new(StringComparer.Ordinal) { ["utilisation"] = 0.9 })));

    [Fact]
    public void TwoDifferentCorporaAreRefused() =>
        Assert.Throws<MismatchedOrderCorpusException>(() => Propose(Scenario("b", ("a", (10, 0.6))), Scenario("t", ("z", (8, 0.7)))));

    // ----------------------------------------------- publishing is a separate explicit act

    private static Recommendation Recommendation() =>
        new("rec-1", "cheaper cartons", ["a"], [new ExpectedDelta("cost", 10, 8)], 1.0, Bounds, Rollback);

    [Fact]
    public void ApproveCallsTheInjectedPublisherExactlyOnce()
    {
        var calls = 0;
        var record = Recommendations.Approve(Recommendation(), at: 1_000, publish: () =>
        {
            calls++;
            return 7;
        });
        Assert.Equal(1, calls);
        Assert.Equal(new ApprovalRecord("rec-1", 1_000, 7), record);
    }

    private static CatalogSnapshot EmptySnapshot() => new([], [], [], [], []);

    [Fact]
    public void ACatalogApprovalPublishesThroughTheRealRegistry()
    {
        var registry = new CatalogRegistry("cartons");
        var record = Recommendations.ApproveCatalog(Recommendation(), registry, EmptySnapshot(), approvedAt: 1_000, effectiveAt: 2_000);
        Assert.Equal(1, record.PublishedVersion);
        Assert.Equal("rec-1", record.RecommendationId);
        Assert.Equal("approved recommendation rec-1", registry.Versions[0].Note);
    }

    /// <summary>Append-only: publishing again never replaces what was published before.</summary>
    [Fact]
    public void ASecondCatalogApprovalAppendsANewVersion()
    {
        var registry = new CatalogRegistry("cartons");
        var first = Recommendations.ApproveCatalog(Recommendation(), registry, EmptySnapshot(), approvedAt: 1_000, effectiveAt: 2_000);
        var second = Recommendations.ApproveCatalog(Recommendation(), registry, EmptySnapshot(), approvedAt: 3_000, effectiveAt: 4_000);
        Assert.Equal((1, 2), (first.PublishedVersion, second.PublishedVersion));
    }

    [Fact]
    public void APolicyApprovalPublishesAVersionedRule()
    {
        var registry = new PolicyRegistry();
        var record = Recommendations.ApprovePolicy(Recommendation(), registry, ruleId: "no-hazmat-air", scope: "hazmat",
            action: "reject", predicates: [new PolicyPredicate("hazmat", "class", "equals", new JsonString("1.4"))],
            priority: 10, approvedAt: 1_000, effectiveAt: 2_000);
        Assert.Equal(1, record.PublishedVersion);
        Assert.Equal("rec-1", record.RecommendationId);
        Assert.Equal("approved recommendation rec-1", registry.Version("no-hazmat-air", 1).Reason);
    }

    /// <summary>Structural, not a convention: the proposal path has no registry parameter, so it cannot reach a mutation even by mistake.</summary>
    [Fact]
    public void AProposalIsHandedNoRegistryOfItsOwn()
    {
        var propose = typeof(Recommendations).GetMethod(nameof(Recommendations.Propose), BindingFlags.Public | BindingFlags.Static)!;
        Assert.DoesNotContain(propose.GetParameters(), parameter =>
            parameter.ParameterType.Name.Contains("Registry", StringComparison.Ordinal)
            || parameter.Name!.Contains("registry", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ the delta itself

    /// <summary>Signed on purpose: whether a negative delta is good depends on the axis, and this type does not know.</summary>
    [Fact]
    public void DeltaIsTreatmentMinusBaseline()
    {
        Assert.Equal(-2.0, new ExpectedDelta("cost", 10, 8).Delta);
        Assert.Equal(0.2, new ExpectedDelta("utilisation", 0.7, 0.9).Delta, 1e-9);
    }

    /// <summary>A proposal with nothing measured is advice without evidence.</summary>
    [Fact]
    public void ARecommendationMustReportAtLeastOneDelta()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new Recommendation("rec-1", "p", ["a"], [], 1.0, Bounds, Rollback));
        Assert.Contains("at least one expected delta", refusal.Message, StringComparison.Ordinal);
    }
}
