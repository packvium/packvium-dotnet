using System;
using System.Collections.Generic;
using System.Globalization;
using Packvium.Intelligence;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Prove a carton change is better before you publish it.
///
/// Run it:
///
///     dotnet run --project examples -- intelligence
///
/// Every other example asks the engine to pack one shipment. This one asks a different question,
/// the one that comes up when you already pack well and want to change something: is the new
/// carton set actually an improvement, and how would I know?
///
/// Four functions answer it, and none of them returns a single blended number. A score that folds
/// cost and utilisation together can name a "winner" that is worse on the axis you actually care
/// about, so what you get back is a per-order Pareto report: which orders improved, which
/// regressed, and which genuinely traded one axis for another.
///
/// Nothing here reads the clock or the network. The scenarios are pinned to catalog versions you
/// supply, so the same comparison replays to the same answer next year.
/// </summary>
public static class Intelligence
{
    /// <summary>
    /// Two carton sets over the same five orders. You would measure these by packing each order
    /// twice; they are written out here so the example has nothing to hide. <c>cost_minor</c> is
    /// in cents and lower is better; <c>utilisation</c> is filled volume over container volume and
    /// higher is better. Order 5 is the interesting one: the proposed cartons cannot pack it at all.
    /// </summary>
    private static readonly (string Order, (double Cost, double Utilisation) Baseline, (double Cost, double Utilisation)? Treatment)[] Measured =
    [
        ("order-1", (1240, 0.71), (1120, 0.76)),
        ("order-2", (980, 0.68), (910, 0.72)),
        ("order-3", (1500, 0.80), (1600, 0.83)),
        ("order-4", (2100, 0.64), (1890, 0.69)),
        ("order-5", (1350, 0.70), null),
    ];

    /// <summary>
    /// Both directions are stated explicitly. There is no default, because guessing that runtime or
    /// cost is "higher is better" would silently invert the whole report.
    /// </summary>
    private static readonly Dictionary<string, bool> HigherIsBetter = new(StringComparer.Ordinal)
    {
        ["cost_minor"] = false,
        ["utilisation"] = true,
    };

    /// <summary>
    /// A pin is what makes a comparison replayable: it names the catalog version each arm was run
    /// against rather than whatever is current when you happen to run it.
    /// </summary>
    private static readonly ScenarioVersionPin BaselinePin = new(catalogVersion: 1);

    private static readonly ScenarioVersionPin TreatmentPin = new(catalogVersion: 2);

    /// <summary>The replayed history: the two arms of each held-out decision.</summary>
    private static readonly Dictionary<string, ((double Cost, double Utilisation) Baseline, (double Cost, double Utilisation) Treatment)> Replay =
        new(StringComparer.Ordinal)
        {
            ["shipment-101"] = ((1400, 0.66), (1210, 0.73)),
            ["shipment-102"] = ((1150, 0.74), (1180, 0.77)),
            // Cheaper under the proposed cartons -- and unstackable, which the validator catches.
            ["shipment-103"] = ((1600, 0.62), (1050, 0.81)),
            ["shipment-104"] = ((990, 0.69), (940, 0.71)),
        };

    private static readonly Dictionary<(string Decision, string Arm), string[]> Invalid = new()
    {
        [("shipment-103", "treatment")] = ["unsupported_item"],
    };

    private static readonly string Rule = new('=', 78);

    public static void Run()
    {
        Heading("1. Two scenarios, compared order by order");
        var orderIds = Array.ConvertAll(Measured, measured => measured.Order);
        var baseline = Scenarios.RunScenario("current-cartons", BaselinePin, orderIds, Arm(treatment: false));
        var treatment = Scenarios.RunScenario("proposed-cartons", TreatmentPin, orderIds, Arm(treatment: true));
        foreach (var report in Scenarios.CompareScenarios(baseline, treatment, HigherIsBetter))
        {
            Console.WriteLine(report.Winner is { } winner
                ? $"  {report.Profile}: {winner}"
                : $"  {report.Profile}: no winner — {string.Join(" and ", report.ParetoOptimal)} are both Pareto-optimal");
        }
        Console.WriteLine();
        Say("order-3 has no winner because the trade-off is real: the proposed cartons cost",
            "more and pack denser. A blended score would have picked one and hidden that.");
        Console.WriteLine();
        Say("order-5 names `baseline` as winner for a duller reason — the proposed cartons",
            "produced no answer there, so there was only one candidate to be optimal. That",
            "is not a baseline win, and the next step is careful not to count it as one.");
        Console.WriteLine();

        Heading("2. A proposal, or an explicit refusal to make one");
        // The paired cohort is four orders out of five, so confidence is 0.8. Ask for more than the
        // evidence supports and you get null rather than a proposal with a caveat attached.
        var strict = Propose(baseline, treatment, minimumConfidence: 0.9);
        Console.WriteLine($"  minimum_confidence=0.9 -> {strict?.RecommendationId ?? "null"}");
        Say("Four of five orders are comparable, so confidence is 0.80 and the bar is not",
            "met. `null` is the whole answer: there is no low-confidence proposal to weigh.");
        Console.WriteLine();

        var recommendation = Propose(baseline, treatment, minimumConfidence: 0.75)
                             ?? throw new InvalidOperationException("the evidence no longer supports a proposal");
        Console.WriteLine($"  minimum_confidence=0.75 -> {recommendation.RecommendationId}");
        Console.WriteLine($"  supported by: {string.Join(", ", recommendation.SupportingOrderIds)}");
        Console.WriteLine($"  confidence:   {Fixed(recommendation.Confidence, 2)}");
        foreach (var delta in recommendation.ExpectedDeltas)
        {
            Console.WriteLine($"    {delta.Metric,-12} {Fixed(delta.BaselineMean, 3),8} -> {Fixed(delta.TreatmentMean, 3),8}");
        }
        Console.WriteLine();
        Say("order-5 is absent from the supporting orders. A delta is a paired comparison,",
            "so an order only one arm could pack contributes to neither mean.");
        Console.WriteLine();

        Heading("3. Replay against history the proposal has never seen");
        var ledger = History();
        var evaluation = Holdout.EvaluateOnHistory(recommendation, ledger,
            ["order-1", "order-2", "shipment-101", "shipment-102", "shipment-103", "shipment-104"],
            BaselinePin, TreatmentPin, ReplayDecision, Validate, HigherIsBetter, splitAt: 500);
        Say("split_at=500 — required, with no default, because how much history is enough",
            "is a claim about your business that this library cannot make for you.");
        Console.WriteLine();
        Console.WriteLine($"  training: {string.Join(", ", evaluation.TrainingDecisionIds)}");
        Console.WriteLine($"  held out: {string.Join(", ", evaluation.HoldoutDecisionIds)}");
        Console.WriteLine();
        foreach (var decision in evaluation.Decisions)
        {
            var note = !decision.TreatmentCodes.IsEmpty
                ? $"  (treatment rejected: {string.Join(", ", decision.TreatmentCodes)})"
                : !decision.BaselineRealisedRisk.IsEmpty
                    ? $"  (baseline actually suffered: {string.Join(", ", decision.BaselineRealisedRisk)})"
                    : "";
            Console.WriteLine($"    {decision.DecisionId,-14} {decision.Verdict}{note}");
        }
        Console.WriteLine();
        Console.WriteLine($"  improved {evaluation.Improved}, regressed {evaluation.Regressed}, "
                          + $"traded off {evaluation.TradedOff}, unpackable {evaluation.Unpackable}");
        Console.WriteLine();
        Say("shipment-104's note is negative evidence about the baseline, not a reason the",
            "treatment won. Damage, returns, repacks and operator overrides happened under",
            "the carton that actually shipped, so they are reported against that arm and",
            "never credited to the one that was never tried.");
        Console.WriteLine();
        Say("shipment-103 is the one to read twice. The proposed cartons packed it for 1050",
            "against 1600 and denser besides — and the independent validator rejected the",
            "placement, so it counts as a regression. There is no cost at which a packing",
            "your validator refuses becomes an improvement; the metrics are discarded, not",
            "discounted.");
        Console.WriteLine();

        Heading("4. A proposal cannot be scored on its own evidence");
        // Move the split earlier and order-1 -- which the recommendation cites -- lands in the
        // held-out set. That is marking your own homework, so it is refused rather than reported.
        try
        {
            Holdout.EvaluateOnHistory(recommendation, ledger, ["order-1", "shipment-101"],
                BaselinePin, TreatmentPin, ReplayDecision, Validate, HigherIsBetter, splitAt: 50);
        }
        catch (SupportingEvidenceInHoldoutException refusal)
        {
            Console.WriteLine($"  refused: {refusal.Message}");
        }
        Console.WriteLine();
        Say("The refusal is the feature. A holdout score is worth exactly as much as the",
            "separation between the evidence that produced the proposal and the evidence",
            "that tests it, and nothing else in the call can enforce that separation.");
    }

    /// <summary>
    /// An evaluator for one arm. In your code this calls <c>Pack</c>; here it replays the
    /// measurements above so the example teaches the comparison and not the packing.
    /// </summary>
    private static OrderEvaluator Arm(bool treatment) => (orderId, _) =>
    {
        var row = Array.Find(Measured, measured => measured.Order == orderId);
        if ((treatment ? row.Treatment : row.Baseline) is not { } measured)
        {
            return new OrderRunResult(orderId, succeeded: false,
                failureReason: "no carton in the proposed set fits this order");
        }
        return new OrderRunResult(orderId, succeeded: true, metrics: Metrics(measured));
    };

    private static Recommendation? Propose(ScenarioResult baseline, ScenarioResult treatment, double minimumConfidence) =>
        Recommendations.Propose("rec-cartons-2024", "adopt the proposed carton set", baseline, treatment,
            constraints: ["no placement the validator would reject"],
            rollbackPlan: "republish catalog version 1",
            minimumCohortSize: 4, minimumConfidence: minimumConfidence);

    /// <summary>
    /// The ledger is append-only and records what actually happened to a shipment; its timestamps
    /// are what a holdout split is made of. Each event type accepts a closed set of fields and
    /// nothing else -- <c>damage</c> takes a reason and a severity, not a carton id. A ledger that
    /// accepted arbitrary payloads would be a log; the point of this one is that a replay can rely
    /// on what it finds there.
    /// </summary>
    private static OutcomeLedger History()
    {
        var history = new (string Decision, OutcomeEventType Type, long At, Dictionary<string, JsonValue> Payload)[]
        {
            ("order-1", OutcomeEventType.ActualCarton, 100, Shipped("box-a")),
            ("order-2", OutcomeEventType.ActualCarton, 120, Shipped("box-a")),
            ("shipment-101", OutcomeEventType.ActualCarton, 900, Shipped("box-b")),
            ("shipment-102", OutcomeEventType.ActualCarton, 910, Shipped("box-a")),
            ("shipment-103", OutcomeEventType.ActualCarton, 920, Shipped("box-c")),
            ("shipment-104", OutcomeEventType.ActualCarton, 930, Shipped("box-b")),
            // A real bad outcome under the carton that actually shipped. It is reported against the
            // baseline and never credited to the treatment, because it happened.
            ("shipment-104", OutcomeEventType.Damage, 940, new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["reason_code"] = new JsonString("crushed_corner"),
                ["severity"] = new JsonString("minor"),
            }),
        };
        var ledger = new OutcomeLedger();
        for (var index = 0; index < history.Length; index++)
        {
            var (decision, type, at, payload) = history[index];
            ledger.Record(new OutcomeEvent($"e{index + 1}", decision, type, payload, at));
        }
        return ledger;
    }

    private static Dictionary<string, JsonValue> Shipped(string carton) => new(StringComparer.Ordinal)
    {
        ["carton_id"] = new JsonString(carton),
        ["catalog_version"] = new JsonInteger(1),
    };

    /// <summary>Re-packs one historical decision under whichever carton set the pin names.</summary>
    private static OrderEvaluationArtifact ReplayDecision(string decisionId, ScenarioVersionPin versionPin)
    {
        var arm = versionPin == BaselinePin ? "baseline" : "treatment";
        var (baselineArm, treatmentArm) = Replay[decisionId];
        var run = new OrderRunResult(decisionId, succeeded: true, metrics: Metrics(arm == "baseline" ? baselineArm : treatmentArm));
        // The request/result pair is what the validator is handed. It deliberately carries no
        // score, so nothing it decides can be influenced by how good the packing looked.
        return new OrderEvaluationArtifact(run, request: decisionId, result: arm);
    }

    /// <summary>Your own independent check, run over the packing rather than over its score.</summary>
    private static ValidationVerdict Validate(object request, object result) =>
        Invalid.TryGetValue(((string)request, (string)result), out var codes)
            ? new ValidationVerdict(valid: false, codes)
            : new ValidationVerdict(valid: true);

    private static Dictionary<string, double> Metrics((double Cost, double Utilisation) measured) => new(StringComparer.Ordinal)
    {
        ["cost_minor"] = measured.Cost,
        ["utilisation"] = measured.Utilisation,
    };

    /// <summary>Python's <c>:.Nf</c>: the exact binary value rounded to N places.</summary>
    private static string Fixed(double value, int places) =>
        value.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static void Heading(string title)
    {
        Console.WriteLine(Rule);
        Console.WriteLine(title);
        Console.WriteLine(Rule);
        Console.WriteLine();
    }

    private static void Say(params string[] lines)
    {
        foreach (var line in lines)
        {
            Console.WriteLine($"  {line}");
        }
    }
}
