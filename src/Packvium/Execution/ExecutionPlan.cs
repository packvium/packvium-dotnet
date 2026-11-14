using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Execution;

/// <summary>
/// An execution plan derived from an already validated packing result (<c>execution.py</c>;
/// <c>docs/EXECUTION-PLAN.md</c> is the contract). A result answers what goes where; an operator
/// needs what to do first and why this carton. This turns the first into the second and is
/// built so that it cannot do anything else: it reads the result's JSON form only, imports no
/// solver and no validator, holds no registry and reads no clock, so the same result yields the
/// same plan forever.
///
/// Anything the solver or validator decided appears under <c>facts</c>; anything a human reads
/// appears under <c>presentation</c> and names the fields it was derived from in <c>cites</c>.
/// A placement is referenced by fields the cross-language projection keeps, never by
/// <c>item_id</c>, which is an instance counter four engines need not agree on.
///
/// <see cref="CanonicalJson"/> is held byte-identical to the reference's
/// <c>canonical_plan_json</c>. Every pass is linear in the result's size, plus the sort of each
/// injected loading order.
/// </summary>
public static class ExecutionPlan
{
    /// <summary>
    /// The plan's own format tag. Not the packing schema's version, and it does not move with
    /// it: a result can gain fields without changing what a plan says.
    /// </summary>
    public const string Format = "packvium-execution-plan/v1";

    /// <summary>
    /// What a <c>score</c> index means is a property of the request's objective, which this
    /// adapter does not know. Naming an index it cannot explain would be inventing meaning.
    /// </summary>
    public const string UnnamedAxis = "unnamed objective axis";

    private static readonly string[] Axes = ["x", "y", "z"];

    /// <summary>
    /// Derive the execution plan for one validated result, in its JSON form.
    /// </summary>
    /// <param name="request">Accepted for the reference's signature; nothing in a plan is read from it yet.</param>
    /// <param name="result">The result document, as <c>ResultWriter</c> writes it or as a golden stores it.</param>
    /// <param name="loadingOrders">
    /// A container's index to the engine-computed order its placements are loaded in. Injected,
    /// because the order is a statement about physics this adapter must not make for itself.
    /// </param>
    public static JsonObject Build(JsonObject request, JsonObject result,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? loadingOrders = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.GetPresent("status") is null)
        {
            throw new ExecutionPlanException("a result without a status is not a validated result");
        }
        var containers = Sequence(result.Get("containers"));
        var winnerScore = Sequence(result.Get("score"));
        var planContainers = containers
            .Select((container, index) => (JsonValue)PlanContainer(index, Mapping(container),
                loadingOrders?.GetValueOrDefault(index)))
            .ToImmutableArray();
        var unplaced = Sequence(result.Get("unpacked_items"))
            .Select(item => (JsonValue)Unplaced(Mapping(item)))
            .ToImmutableArray();
        var alternatives = Sequence(result.Get("alternatives"))
            .Select((alternative, index) => (JsonValue)Alternative(index, winnerScore, Mapping(alternative)))
            .ToImmutableArray();
        return Object(
            ("format", new JsonString(Format)),
            ("objective", Field(result, "objective")),
            ("facts", Object(
                ("status", Field(result, "status")),
                ("score", new JsonArray(winnerScore)),
                ("feasibility", Field(result, "feasibility")),
                ("optimality", Field(result, "optimality")),
                ("container_count", new JsonInteger(containers.Length)))),
            ("containers", new JsonArray(planContainers)),
            // Often empty, and not for one reason: the fast profile runs one solver, the start
            // loop stops once the grid lattice packs everything, only one start completed, or
            // `alternatives: 1` -- the cap counts the winner. An empty list is never an error.
            ("alternatives", new JsonArray(alternatives)),
            ("unplaced", new JsonArray(unplaced)));
    }

    /// <summary>
    /// The one byte-comparable spelling of a plan: keys sorted by code point at every depth,
    /// lists in order, compact separators, non-ASCII unescaped -- <c>json.dumps(plan,
    /// sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c>.
    /// </summary>
    public static string CanonicalJson(JsonObject plan) => PythonJson.Dumps(plan, sortKeys: true);

    /// <summary>
    /// A reference two languages agree on, for one placement in one container. The container is
    /// named by its position in the result, not its <c>id</c>, and the position by its exact
    /// <c>ticks</c>, never by the rendered <c>value</c>.
    /// </summary>
    public static JsonObject PlacementReference(int containerIndex, JsonValue placement)
    {
        var fields = Mapping(placement);
        var position = Mapping(Required(fields, "position"));
        var itemType = Required(fields, "item_type");
        var orientation = Required(fields, "orientation");
        var ticks = Axes.Select(axis => (axis, (JsonValue)new JsonInteger(Ticks(Mapping(Required(position, axis))))));
        return Object(
            ("container_index", new JsonInteger(containerIndex)),
            ("item_type", itemType),
            ("orientation", orientation),
            ("position_ticks", Object([.. ticks])));
    }

    private static JsonObject PlanContainer(int index, JsonObject container, IReadOnlyList<int>? loadingOrder)
    {
        var placements = Sequence(container.Get("placements"));
        var (order, steps) = Steps(index, placements, loadingOrder);
        return Object(
            ("container_index", new JsonInteger(index)),
            ("facts", Object(
                ("container_type", Field(container, "container_type")),
                ("placement_count", new JsonInteger(placements.Length)),
                ("volume_utilization", Field(container, "volume_utilization")))),
            ("order", new JsonString(order)),
            ("steps", new JsonArray(steps)));
    }

    /// <summary>
    /// The operator sequence for one container, or an honest absence of one. Falling back to the
    /// order placements happen to appear in would present an artifact of how the solver walked
    /// its candidates as a safe order to lift boxes in, so without an injected order no step is
    /// numbered.
    /// </summary>
    private static (string Order, ImmutableArray<JsonValue> Steps) Steps(int containerIndex,
        ImmutableArray<JsonValue> placements, IReadOnlyList<int>? loadingOrder)
    {
        if (loadingOrder is null)
        {
            return ("unavailable", [.. placements.Select(placement =>
                (JsonValue)Object(("placement", PlacementReference(containerIndex, placement))))]);
        }
        if (!loadingOrder.Order().SequenceEqual(Enumerable.Range(0, placements.Length)))
        {
            throw new ExecutionPlanException(
                $"loading order for container {containerIndex} is not a permutation of its {placements.Length} placements");
        }
        return ("loading", [.. loadingOrder.Select((placementIndex, step) => (JsonValue)Object(
            ("sequence", new JsonInteger(step + 1)),
            ("placement", PlacementReference(containerIndex, placements[placementIndex]))))]);
    }

    private static JsonObject Alternative(int index, ImmutableArray<JsonValue> winnerScore, JsonObject alternative)
    {
        var score = Sequence(alternative.Get("score"));
        var difference = FirstDifference(winnerScore, score);
        var text = difference is { } first
            ? $"This option differs first at objective axis {Decimal(first.Index)} ({UnnamedAxis}): "
              + $"chosen {Decimal(first.Winner)}, this {Decimal(first.Alternative)}."
            : "This option scored identically to the chosen one on every objective axis; "
              + "the score does not record why one was taken.";
        JsonValue differenceFacts = difference is { } found
            ? Object(
                ("index", new JsonInteger(found.Index)),
                ("winner", new JsonInteger(found.Winner)),
                ("alternative", new JsonInteger(found.Alternative)),
                ("difference", new JsonInteger(found.Alternative - found.Winner)))
            : JsonNull.Instance;
        return Object(
            ("facts", Object(
                ("alternative_index", new JsonInteger(index)),
                ("score", new JsonArray(score)),
                ("status", Field(alternative, "status")),
                ("first_difference", differenceFacts))),
            // Deliberately not "it lost because it is taller". The solver recorded a score, not
            // a cause; a sentence naming a cause would be a claim nothing in the result supports.
            ("presentation", Presentation(text, "score", "alternatives[].score")));
    }

    /// <summary>
    /// The first index at which two score vectors differ, and by how much. Never a blended
    /// number: the portfolio compared these lexicographically, so the first differing index is
    /// the decision.
    /// </summary>
    private static (int Index, BigInteger Winner, BigInteger Alternative)? FirstDifference(
        ImmutableArray<JsonValue> winner, ImmutableArray<JsonValue> loser)
    {
        for (var index = 0; index < Math.Min(winner.Length, loser.Length); index++)
        {
            if (winner[index] == loser[index])
            {
                continue;
            }
            if (winner[index] is not JsonInteger a || loser[index] is not JsonInteger b)
            {
                throw new ExecutionPlanException("score vectors are compared as integers, and an entry is not one");
            }
            return (index, a.Value, b.Value);
        }
        if (winner.Length != loser.Length)
        {
            throw new ExecutionPlanException("score vectors of different length cannot be compared lexicographically");
        }
        return null;
    }

    private static JsonObject Unplaced(JsonObject item)
    {
        var proof = item.Get("proof");
        // The proof's level is carried through unchanged. Softening `observed` into "could not
        // fit" would turn an honest limit into a false certainty.
        var level = proof is not null && PythonCoercion.Bool(proof) ? Field(Mapping(proof), "level") : JsonNull.Instance;
        var reason = Field(item, "reason");
        return Object(
            ("facts", Object(
                ("item_type", Field(item, "item_type")),
                ("reason", reason),
                ("proof_level", level),
                ("details", new JsonArray(Sequence(item.Get("details")))))),
            ("presentation", Presentation($"Not packed: {Str(reason)} ({Str(level)}).",
                "unpacked_items[].reason", "unpacked_items[].proof.level")));
    }

    private static JsonObject Presentation(string summary, params string[] cites) => Object(
        ("summary", new JsonString(summary)),
        ("cites", new JsonArray([.. cites.Select(cite => (JsonValue)new JsonString(cite))])));

    /// <summary><c>dict.get(key)</c>, with an absent key written as <c>null</c>.</summary>
    private static JsonValue Field(JsonObject fields, string key) => fields.Get(key) ?? JsonNull.Instance;

    private static JsonValue Required(JsonObject fields, string key) =>
        fields.Get(key) ?? throw new ExecutionPlanException($"placement is missing a field the reference is built from: '{key}'");

    /// <summary><c>int(position[axis]["ticks"])</c>: the integer cast the reference applies before emitting a tick.</summary>
    private static BigInteger Ticks(JsonObject scalar)
    {
        var ticks = Required(scalar, "ticks");
        try
        {
            return PythonCoercion.BigInt(ticks);
        }
        catch (ArgumentException error)
        {
            throw new ExecutionPlanException($"placement is missing a field the reference is built from: {error.Message}");
        }
    }

    /// <summary><c>list(value or ())</c> over a JSON array; anything else a result cannot hold there is refused.</summary>
    private static ImmutableArray<JsonValue> Sequence(JsonValue? value) => value switch
    {
        null => [],
        JsonArray array => array.Items,
        _ when !PythonCoercion.Bool(value) => [],
        _ => throw new ExecutionPlanException($"expected a list, got {PythonCoercion.TypeName(value)}"),
    };

    private static JsonObject Mapping(JsonValue value) =>
        value as JsonObject ?? throw new ExecutionPlanException($"expected an object, got {PythonCoercion.TypeName(value)}");

    /// <summary><c>str(value)</c> inside an f-string, for the scalars a result carries there.</summary>
    private static string Str(JsonValue value) => value is JsonArray or JsonObject
        ? throw new ExecutionPlanException($"a {PythonCoercion.TypeName(value)} cannot be rendered into a summary")
        : PythonCoercion.Str(value);

    private static string Decimal(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonObject Object(params (string Key, JsonValue Value)[] members) =>
        new(members.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)));
}
