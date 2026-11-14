using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Packvium.Execution;
using Packvium.Locks;
using Packvium.Serialization;

namespace Packvium.Examples;

/// <summary>
/// Turn a packing result into instructions someone can follow on a dock.
///
/// Run it:
///
///     dotnet run --project examples -- execution
///
/// <c>Pack</c> answers where every box goes. That answer is not yet a work order: it does not
/// say what to lift first, it does not separate what the solver decided from what a screen
/// should say, and it has no way for the operator who is standing there to say the printer must
/// go in the corner.
///
/// The execution plan is that second document. It is derived from an already validated result
/// -- it calls no solver and no validator, and a test in each language asserts so. Anything it
/// could decide on its own would be a decision made twice. An operator lock is the one way back
/// into the solver, and it goes through the ordinary path: sections 5 and 6.
/// </summary>
public static class Execution
{
    /// <summary>
    /// One crate, a printer that must stay upright, four toner cartridges, and a pallet jack that
    /// was never going to fit. The last one is deliberate: an execution plan has to say what is
    /// not going on the truck as clearly as what is.
    /// </summary>
    private const string Request = """
        {
          "units": {"length": "mm"},
          "configuration": {
            "objective": "default",
            "profile": "balanced",
            "seed": 42,
            "time_limit_ms": 60000
          },
          "items": [
            {"id": "printer", "quantity": 1, "weight": "9 kg", "keep_upright": true,
             "dimensions": {"length": "420", "width": "340", "height": "260"}},
            {"id": "toner", "quantity": 4, "weight": "900 g",
             "dimensions": {"length": "180", "width": "120", "height": "100"}},
            {"id": "pallet-jack", "quantity": 1, "weight": "80 kg",
             "dimensions": {"length": "1200", "width": "550", "height": "1200"}}
          ],
          "containers": [
            {"id": "crate", "quantity": 1, "max_payload": "30 kg",
             "inner_dimensions": {"length": "600", "width": "400", "height": "400"}}
          ]
        }
        """;

    private static readonly string Rule = new('=', 78);

    public static void Run()
    {
        // The time limit above is a safety fuse, not a target -- nothing in this scene comes close to it.
        var request = (JsonObject)JsonReader.Parse(Request);
        var result = JsonApi.PackFromJson(request);
        var plan = ExecutionPlan.Build(request, result);
        var container = Objects(plan, "containers")[0];

        Heading("1. What the solver decided, kept apart from what a screen says");
        var facts = Get(plan, "facts");
        Console.WriteLine($"  format:          {Text(plan, "format")}");
        Console.WriteLine($"  status:          {Text(facts, "status")}");
        Console.WriteLine($"  containers used: {Text(facts, "container_count")}");
        Console.WriteLine($"  score:           [{string.Join(", ", ((JsonArray)facts.Require("score")).Items.Select(Scalar))}]");
        Console.WriteLine($"  utilization:     {Text(Get(container, "facts"), "volume_utilization")}");
        Console.WriteLine();
        Say("Everything above is under `facts`. It is the solver's own answer, copied and",
            "not re-derived, so a downstream system that reads only `facts` loses nothing it",
            "is entitled to rely on. The score stays a vector: collapsing five axes into one",
            "number is a judgement about your priorities that this document does not make.");
        Console.WriteLine();

        Heading("2. The step order is injected, or it is honestly absent");
        Console.WriteLine($"  order: {Text(container, "order")}");
        var steps = Objects(container, "steps");
        foreach (var step in steps)
        {
            var reference = Get(step, "placement");
            var ticks = Get(reference, "position_ticks");
            Console.WriteLine($"    {Text(reference, "item_type"),-9} {Text(reference, "orientation")}  "
                              + $"at ({Text(ticks, "x")}, {Text(ticks, "y")}, {Text(ticks, "z")})");
        }
        Console.WriteLine();
        Say("Every placement is listed and not one is numbered. The engines compute a safe",
            "loading order from geometry this adapter never sees, so without one it says",
            "`unavailable` rather than guessing.");
        Console.WriteLine();
        Say("There is no third behaviour on purpose. Falling back to the order placements",
            "happen to appear in would present an artifact of how the solver walked its",
            "candidate points as an order that is safe to lift boxes in. It is not.");
        Console.WriteLine();

        var reversed = new Dictionary<int, IReadOnlyList<int>> { [0] = [.. Enumerable.Range(0, steps.Count).Reverse()] };
        var ordered = Objects(ExecutionPlan.Build(request, result, reversed), "containers")[0];
        Console.WriteLine($"  order: {Text(ordered, "order")}");
        foreach (var step in Objects(ordered, "steps"))
        {
            Console.WriteLine($"    {Text(step, "sequence")}. {Text(Get(step, "placement"), "item_type")}");
        }
        Console.WriteLine();
        Say("Hand it an order and each step is numbered. The order above is reversed on",
            "purpose, to show that the sequence is the one you supplied and not one the",
            "adapter re-derived behind your back.");
        Console.WriteLine();

        Heading("3. Every sentence names the fields it was built from");
        foreach (var entry in Objects(plan, "unplaced"))
        {
            var entryFacts = Get(entry, "facts");
            var presentation = Get(entry, "presentation");
            Console.WriteLine($"  facts:        item_type='{Text(entryFacts, "item_type")}'");
            Console.WriteLine($"                reason='{Text(entryFacts, "reason")}' proof_level='{Text(entryFacts, "proof_level")}'");
            Console.WriteLine($"  presentation: {Text(presentation, "summary")}");
            Console.WriteLine($"  cites:        {string.Join(", ", ((JsonArray)presentation.Require("cites")).Items.Select(Scalar))}");
        }
        Console.WriteLine();
        Say("`proven` is a claim about a search, not a summary of one: no orientation of the",
            "pallet jack fits any offered crate, so nothing was tried and nothing needed to",
            "be. A reason with no citation would be a sentence nobody can check, which is",
            "why `cites` is part of the format rather than a convention.");
        Console.WriteLine();

        Heading("4. One plan, every engine, the same bytes");
        var canonical = ExecutionPlan.CanonicalJson(plan);
        Console.WriteLine($"  canonical form: {Encoding.UTF8.GetByteCount(canonical)} bytes, first 68 of them");
        Console.WriteLine($"    {canonical[..68]}...");
        Console.WriteLine();
        Say("Hand the same *result* to any Packvium adapter, this one included, and it emits",
            "the same bytes. That is stricter than the packing contract, and it can be: a",
            "plan is derived from a result, so there is nothing left to differ about.");
        Console.WriteLine();
        Say("It does not follow that every engine packing the same *request* agrees. This",
            "engine, like Python and PHP, is held to identical placements and produces this",
            "exact form; the Rust and JavaScript engines are held to a valid answer at or",
            "above the objective floor, so their plan for this request can be a different",
            "length. Whose packing is better is a question the plan never answers.");
        Console.WriteLine();

        Heading("5. The operator pins a box, and gets a second plan — never an edited one");
        var locks = OperatorLocks.LocksFromPlan(plan, containerIndex: 0, itemTypes: ["printer"]);
        var printerLock = locks[0];
        Console.WriteLine($"  locked: {printerLock.ItemType} {printerLock.Orientation} at {Ticks(printerLock.PositionTicks)}");
        Say("The lock is read out of the plan's own placement reference. That is the only",
            "reason the two can be trusted to mean the same box: there is no second address",
            "format to drift.");
        Console.WriteLine();

        var resolved = OperatorLocks.ResolveWithLocks(request, locks);
        Console.WriteLine($"  preserved: {resolved.Preserved}");
        Console.WriteLine("  the approved plan is untouched: "
                          + $"{ExecutionPlan.CanonicalJson(ExecutionPlan.Build(request, result)) == canonical}");
        Console.WriteLine();
        Say("A lock becomes an ordinary placement constraint, so the re-solve is the same",
            "portfolio under the same independent validator. Nothing here can produce a",
            "placement the engine would otherwise refuse — the strongest thing a lock can do",
            "is reserve its own slot and make the search work around it.");
        Console.WriteLine();

        Heading("6. Two ways a lock can fail, and they are not the same kind of thing");
        // Far outside the crate: a single lock that is individually impossible. The solve still
        // runs and still answers; it simply cannot honour this one.
        var unreachable = new PlacementLock(containerIndex: 0, itemType: "printer", orientation: "LWH",
            positionTicks: (99_000_000, 0, 0));
        var outcome = OperatorLocks.ResolveWithLocks(request, [unreachable]);
        Console.WriteLine($"  a lock the solve cannot honour -> preserved={outcome.Preserved}, "
                          + $"{outcome.Missing.Length} lock reported back");
        Console.WriteLine($"    {outcome.Missing[0].ItemType} at {Ticks(outcome.Missing[0].PositionTicks)}");
        Say("Not an exception. The operator asked for something the geometry does not allow,",
            "and what they need back is a valid plan plus the news that their pin was not",
            "honoured — not a stack trace and no plan at all.");
        Console.WriteLine();

        // An item the request never contained: the lock set is wrong about the world, and no
        // solve could make it right.
        try
        {
            OperatorLocks.ResolveWithLocks(request,
                [printerLock, new PlacementLock(containerIndex: 0, itemType: "scanner", orientation: "LWH", positionTicks: (0, 0, 0))]);
        }
        catch (LockSetException refusal)
        {
            Console.WriteLine($"  a lock set that contradicts itself -> {refusal.Message}");
        }
        Console.WriteLine();
        Say("Refused before any solve runs, because no result could satisfy it. The same goes",
            "for two locks whose boxes overlap. The distinction is worth keeping: the first",
            "case is an answer the operator may not like, the second is a question that has",
            "no answer, and reporting them the same way would hide which one happened.");
    }

    /// <summary>A position the way the reference prints a tuple of ticks.</summary>
    private static string Ticks((long X, long Y, long Z) ticks) =>
        string.Create(CultureInfo.InvariantCulture, $"({ticks.X}, {ticks.Y}, {ticks.Z})");

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

    private static JsonObject Get(JsonObject value, string key) => (JsonObject)value.Require(key);

    private static List<JsonObject> Objects(JsonObject value, string key) =>
        [.. ((JsonArray)value.Require(key)).Items.Cast<JsonObject>()];

    private static string Text(JsonObject value, string key) => Scalar(value.Require(key));

    private static string Scalar(JsonValue value) => value switch
    {
        JsonString text => text.Value,
        JsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"not a scalar the example prints: {value}"),
    };
}
