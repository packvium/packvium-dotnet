# Packvium for .NET

Deterministic 3D cartonization and rectangular bin packing for .NET, with exact integer
geometry and no runtime dependencies.

Packvium for .NET is a full C# implementation of Packvium, not a binding over another
engine. It accepts the same requests as every Packvium engine. Python, PHP, Java, .NET and Go
return identical results at the same version; Rust and JavaScript return a valid answer at or
above the objective floor. Every release is checked against the reference engine, the Python
package `packvium`, byte for byte.

> **Version 1.7.0.** This release corresponds to Packvium 1.7.0 in the other languages, and
> it was checked against the 1.7.0 conformance corpus: every packing request, every commerce case,
> every execution plan, every operator-lock scenario, every operational-artifact case, every
> malformed-request case, every plan-revision case, every case that pins the
> unsupported-placements warning or an unpacked reason, and every result-validation case.
> Field names, status codes and the objective vector stay the same for all of 1.x.
> Read [Packvium's guarantees](https://github.com/packvium/packvium-python/blob/v1.7.0/docs/GUARANTEES.md)
> before relying on a result.

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [Packing with domain objects](#packing-with-domain-objects)
- [The JSON request API](#the-json-request-api)
- [Constraints and objectives](#constraints-and-objectives)
- [Boxes cut to measure, flat bags and stacking groups](#boxes-cut-to-measure-flat-bags-and-stacking-groups)
- [Validating a result](#validating-a-result)
- [Commerce](#commerce)
- [Execution plans](#execution-plans)
- [Operational artifacts](#operational-artifacts)
- [Fixed placements and plan revisions](#fixed-placements-and-plan-revisions)
- [Weight rebalancing](#weight-rebalancing)
- [Operator locks](#operator-locks)
- [Scenario comparison, recommendations and holdout replay](#scenario-comparison-recommendations-and-holdout-replay)
- [Command line](#command-line)
- [Examples](#examples)
- [Determinism](#determinism)
- [Guarantees](#guarantees)
- [Differences from the reference engine](#differences-from-the-reference-engine)
- [Versioning](#versioning)
- [The Packvium family](#the-packvium-family)

## Install

```bash
dotnet add package Packvium --version 1.7.0
```

The package targets **.NET 8** (`net8.0`) and **.NET 10** (`net10.0`), and has no runtime
dependencies.

## Quick start

```csharp
using Packvium.Domain;
using Packvium.Packing;

var result = new Packer(PackingConfig.Balanced()).Pack(
    [new Item("book", Dimensions.Mm("210", "140", "30"), quantity: 4)],
    [new Container("box", Dimensions.Mm("400", "300", "250"))]);

Console.WriteLine(result.Status.Value());               // feasible
foreach (var container in result.Containers)
{
    foreach (var placement in container.Placements)
    {
        Console.WriteLine($"{placement.Instance.Id} {placement.Position} {placement.Rotation}");
    }
}
```

Every length is a string so that it can be exact. Fractional inches are not approximated:

```csharp
Dimensions.Inches("12 3/8", "8 1/2", "3/4");
```

## Packing with domain objects

`Packer.Pack` takes items and containers and returns a `PackingResult`: the containers it
opened with their placements, every item it left out with a reason, and the objective score.

```csharp
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;

var result = new Packer(PackingConfig.Balanced(timeLimitMs: 60_000)).Pack(
    [
        new Item("book", Dimensions.Mm("210", "140", "30"), Weight.Parse("450 g"), quantity: 4),
        new Item("mug", Dimensions.Inches("4", "4", "5"), Weight.Parse("12 oz"), quantity: 2, keepUpright: true),
    ],
    [
        new Container("box-m", Dimensions.Mm("400", "300", "250"), maxPayload: Weight.Parse("20 kg"), costMinor: 180),
        new Container("box-l", Dimensions.Mm("500", "400", "350"), maxPayload: Weight.Parse("30 kg"), costMinor: 250),
    ]);

foreach (var unpacked in result.Unpacked)
{
    // A reason code, and the level of proof behind it: proven, observed, inferred or unknown_due_to_limit.
    Console.WriteLine($"{unpacked.Instance.Item.Id}: {unpacked.Reason} ({unpacked.Proof.Level})");
}
```

Positions are `Point`s in ticks of 1/16000 mm. `ResultWriter.Write(result)` turns a result
into the shared JSON result document.

A request built from objects is held to the same rules as the JSON request below. A constructor
that refuses its own value throws `ObjectRefusal`, an `InvalidRequestException` with the schema's
`Reason` (`below_minimum`, `not_allowed`, `invalid_value`, ...), an empty `Field` and the
constructor's text as its message. `Packer.Pack` then walks the objects in the JSON rules' order
and refuses a value the JSON path would refuse with the same reason, pointer and message: a
container's `TagLimits` entry past 2^53 - 1, two items with one id, or a `FixedPlacement` whose
container instance counts from 0 (`FixedPlacementException`, `invalid_fixed_placement`).

## The JSON request API

A request is a JSON document and so is the result. `JsonApi.PackFromJson` takes the same
request every Packvium engine takes and returns a result in the same format:

```csharp
using Packvium.Serialization;

var request = JsonReader.Parse("""
    {"items": [{"id": "box", "quantity": 8, "dimensions": {"length": "50", "width": "50", "height": "50"}}],
     "containers": [{"id": "carton", "inner_dimensions": {"length": "100", "width": "100", "height": "100"}}]}
    """);
var result = JsonApi.PackFromJson(request);

Console.WriteLine(PythonJson.Dumps(result, sortKeys: true, indent: 2));
```

A malformed request throws `InvalidRequestException` (an `ArgumentException`) before any
search runs. `Code` is `invalid_request`, `Reason` one of a closed set every engine shares
(`missing_field`, `wrong_type`, `below_minimum`, `above_maximum`, `negative_measure`,
`invalid_unit`, `duplicate_id`, `not_allowed`, `invalid_value`), `Field` the RFC 6901 pointer to
the bad value, and the message the same text in every engine:
`invalid_request: /items/0/quantity: must be at least 1`. Every `minimum` of the request schema
is a floor, and no length or weight may be negative.

Every closed vocabulary of the schema is refused as `not_allowed` at its pointer: rotations,
`shape_type`, `ground_contact_rule`, `configuration.solvers`, the output and dimensional-weight
units (only the codes `mm`, `cm`, `m`, `in`, `ft` and `mg`, `g`, `kg`, `oz`, `lb`; `ticks` is not
an output unit) and a fixed placement's `orientation`. A `nesting_height` not below the item's
height is `invalid_value`. Without an `output` block the result is written in the request's own
`units.length`, spelt as its code (`" Inches "` writes `in`, `tick` writes `mm`). When a request
breaks several rules, the first is named in this order: unsupported fields, units, output,
configuration, each item, item ids, each container, container ids, fixed placements.

`configuration` and its `effort_budget` are closed: a key the schema does not declare, such as
`profile` for `solver_profile` or `top_k` for `alternatives`, is refused as `not_allowed` with
its pointer rather than ignored, and so is `parallel`. When several are unknown, the first in
code-point order is named. An unknown `objective` or access direction is refused the same way:

```csharp
using Packvium.Domain;
using Packvium.Serialization;

try
{
    JsonApi.PackFromJson(JsonReader.Parse("""
        {"configuration": {"profile": "fast"},
         "items": [{"id": "box", "dimensions": {"length": "50", "width": "50", "height": "50"}}],
         "containers": [{"id": "carton", "inner_dimensions": {"length": "100", "width": "100", "height": "100"}}]}
        """));
}
catch (InvalidRequestException refusal)
{
    Console.WriteLine($"{refusal.Reason} {refusal.Field}");   // not_allowed /configuration/profile
    Console.WriteLine(refusal.Message);   // invalid_request: /configuration/profile: is not a known field
}
```

A key the reader does not recognise on an item or a container is still ignored.
`container.pallet_overhang_limit` is reserved in the schema and not implemented by any engine,
so a request that sets it is refused with `UnsupportedFeatureException` (`unsupported_feature`)
rather than packed as if it were absent. The other refusals a caller may want to catch by type
are public too: `UnknownSolverException`, `UnknownObjectiveException` and `PolicyException`, each
an `ArgumentException`.

`JsonApi.PackFromJson(request, extensions)` also takes an `ExtensionRegistry` of your own
rules. They are added after the rules the request's `policy` block compiles to, never put in
their place, so code of yours cannot switch off a policy the request carries.

## Constraints and objectives

Most rules are fields on `Item` and `Container`:

- weight and payload limits, allowed rotations, keep-upright, floor-only, non-stackable;
- top-load, stack-count and stack-density limits, minimum support ratio;
- incompatible tags, clearance and obstacles;
- axle loads, route order, convex-hull items and compressible items;
- container doors (`accessDirections`): an item due at an earlier stop must keep a clear
  path to one of them;
- versioned policy rules in the request's `policy` block.

```csharp
Item[] items =
[
    new("paint", Dimensions.Mm("200", "200", "250"), Weight.Parse("5 kg"), quantity: 2, keepUpright: true),
    new("glass-panel", Dimensions.Mm("400", "300", "40"), Weight.Parse("8 kg"), quantity: 1, mustBeOnFloor: true,
        maxTopLoad: Weight.Parse("2 kg")),
    new("bleach", Dimensions.Mm("120", "120", "300"), Weight.Parse("2 kg"), quantity: 2, tags: ["hazmat"],
        incompatibleTags: ["food"]),
    new("flour", Dimensions.Mm("200", "150", "100"), Weight.Parse("1500 g"), quantity: 3, tags: ["food"]),
];
```

The objective decides what "best" means. Every objective is an exact integer vector compared
lexicographically, lower first. Six of the seven put the number of items left behind first;
`maximum_volume` puts the share of the requested volume left behind first:

```csharp
var cheapest = new PackingConfig(objective: "lowest_cost", timeLimitMs: 60_000);
var byWeight = new PackingConfig(objective: "shipping_cost", dimensionalWeightDivisor: 5000, timeLimitMs: 60_000);
```

`default`, `lowest_cost`, `shipping_cost`, `lowest_landed_cost`, `open_dimension_height`,
`maximum_value` and `maximum_volume` are available; `Objectives.cs` shows six of the seven
objectives (all but `maximum_volume`) choosing differently. `maximum_volume` ranks the share of
the requested volume left behind ahead of the number of pieces, the way container-loading
benchmarks are scored, so one large item can beat two small ones:

```csharp
var fillTheSpace = new PackingConfig(objective: "maximum_volume", timeLimitMs: 60_000);
```

With the default `minimumSupportRatio` of 0 an item may stand above the floor with nothing
under it. Such a result ends its `Warnings` with
`unsupported_placements: N placement(s) rest on nothing above the container floor; set
configuration.minimum_support_ratio above 0 to require support`, the same line in every engine.
No answer changes.

A rule the request has no field for is an `IPlacementConstraint`. It answers about one
candidate position and never searches; it can refuse a candidate, never force one:

```csharp
using Packvium.Constraints;

sealed class FragileHeightLimit(Length limit) : IPlacementConstraint
{
    public ConstraintResult Evaluate(ConstraintContext context) =>
        !context.Item.Item.Tags.Contains("fragile") || context.Point.Z <= limit.Ticks
            ? ConstraintResult.Allow()
            : ConstraintResult.Reject("fragile_too_high");
}

var restricted = new Packer(PackingConfig.Balanced(timeLimitMs: 60_000),
    new ExtensionRegistry([new FragileHeightLimit(Length.Parse("400 mm"))])).Pack(items, containers);
```

## Boxes cut to measure, flat bags and stacking groups

A **made-to-measure** container is an on-demand box machine. Its `InnerDimensions` is the
largest box it cuts; `MadeToMeasure` adds the smallest size and the cut step per axis, in
ticks, and the prices. The packed container is the box that ships: `AchievedDimensions` is the
extent of its load, and its inner size, outer size, cost and tare are the fabricated box's.

```csharp
var machine = new Container("machine", Dimensions.Mm("1510", "1510", "1510"), costMinor: 40,
    madeToMeasure: new MadeToMeasure(
        minimum: new Triple(Length.Parse("60 mm").Ticks, Length.Parse("60 mm").Ticks, Length.Parse("60 mm").Ticks),
        increment: new Triple(Length.Parse("25 mm").Ticks, Length.Parse("25 mm").Ticks, Length.Parse("25 mm").Ticks),
        costPerSquareMetreMinor: 1000));
var cubes = new Item("cube", Dimensions.Mm("100", "100", "100"), Weight.Parse("100 g"), quantity: 8);
var box = new Packer(new PackingConfig(SolverProfile.Fast, 60_000)).Pack([cubes], [machine]).Containers[0];
Console.WriteLine($"{box.Container.InnerDimensions.Length.Ticks / Length.TicksPerMm} mm for {box.Container.CostMinor}");
// 210 mm for 305
```

A JSON request that gives such a container axles, obstacles or a void-fill reserve, or a fixed
placement, is refused with `UnsupportedFeatureException`.

A **flat bag** (`deformableBoundary: FlatBag.Kind`, or `"deformable_boundary": {"kind":
"flat_bag"}`) reads `InnerDimensions` as a poly mailer's flat length, flat width and largest
filled thickness. A packing is admitted only when length plus thickness and width plus
thickness of the load fit the bag, and the bag is billed on its `AchievedDimensions`:

```csharp
var mailer = new Container("mailer", Dimensions.Mm("240", "140", "100"), deformableBoundary: FlatBag.Kind);
var books = new Item("book", Dimensions.Mm("200", "100", "20"), Weight.Parse("300 g"), quantity: 3,
    allowedRotations: [Rotation.LWH]);
var bags = new Packer(new PackingConfig(SolverProfile.Fast, 60_000)).Pack([books], [mailer]);
Console.WriteLine(string.Join(", ", bags.Containers.Select(bag => bag.AchievedDimensions!.Height.Ticks / Length.TicksPerMm)));
// 40, 20
```

**Stacking groups** say which items may rest directly on which: `stackingGroup` names an
item's class and `acceptsStackingGroups` the classes it may carry. An empty list carries
anything; a non-empty one is closed, so an item with no class rests on no constrained item, and
`stackable: false` still wins.

```csharp
var drum = new Item("drum", Dimensions.Mm("400", "400", "400"), Weight.Parse("5 kg"), quantity: 2,
    stackingGroup: "drum", acceptsStackingGroups: ["carton"]);
var crate = new Item("crate", Dimensions.Mm("400", "400", "400"), Weight.Parse("1 kg"), quantity: 2,
    stackingGroup: "crate");
```

## Validating a result

`ResultValidation.Validate(request, result)` is the reference validator every Packvium package
ships. It takes a request and a result as JSON documents, from this package or any other
engine, recomputes units, geometry, support and load from first principles, and returns the
issues it finds, each a `ResultIssue` with a `Code` and a `Detail` the same in every engine. An
empty list means the result is valid. A result that is not the contract's shape is refused with
`MalformedResultException`, whose `Kind` is `contract` or `shape`.

```csharp
using Packvium.Serialization;
using Packvium.Validation;

var request = JsonReader.Parse("""
    {"items": [{"id": "box", "quantity": 8, "dimensions": {"length": "50", "width": "50", "height": "50"}}],
     "containers": [{"id": "carton", "inner_dimensions": {"length": "100", "width": "100", "height": "100"}}]}
    """);
var result = JsonApi.PackFromJson(request);

foreach (var issue in ResultValidation.Validate(request, result))
{
    Console.WriteLine(issue);   // nothing: the result is valid
}
```

## Commerce

`Packvium.Commerce.CommerceApi` prices a shipment, evaluates an eligibility policy and
resolves catalog versions from a document you supply. These are deterministic functions of
that document: they do not read the clock, use the network or keep hidden state.

```csharp
using Packvium.Commerce;
using Packvium.Serialization;

var document = JsonReader.Parse(File.ReadAllText("commerce.json"));
var quote = CommerceApi.Quote(document, JsonReader.Parse("""
    {"carrier_id": "acme", "service_id": "ground", "tariff_version": 1, "zone": "zone-a",
     "actual_weight_g": 1200, "volume_mm3": 6000000, "requested_accessorials": ["liftgate"]}
    """));
var decision = CommerceApi.EvaluatePolicy(document,
    JsonReader.Parse("""{"scope": "hazmat", "context": {"un_class": "1.4"}, "as_of": 0}"""));

Console.WriteLine(CommerceApi.CanonicalJson(quote));     // what you store, log and compare
```

A request the model cannot answer, such as a zone the tariff does not price, is a result
with a status and a code, not an exception. Only a malformed input throws
`CommerceInputException`. `Commerce.cs` holds a complete document.

## Execution plans

`Packvium.Execution.ExecutionPlan.Build` turns a result document into a plan an operator can
follow. What the solver decided is under `facts`; the sentences a screen shows are under
`presentation`, each with the fields it `cites`. It calls no solver and no validator.

```csharp
using Packvium.Execution;
using Packvium.Serialization;

var request = (JsonObject)JsonReader.Parse(requestJson);
var result = JsonApi.PackFromJson(request);
var plan = ExecutionPlan.Build(request, result);

// Steps are numbered only when you supply a loading order; otherwise the order is "unavailable".
var ordered = ExecutionPlan.Build(request, result, new Dictionary<int, IReadOnlyList<int>> { [0] = [1, 0] });

string canonical = ExecutionPlan.CanonicalJson(plan);   // the same bytes every Packvium engine emits
```

A loading order must be a permutation of the container's steps; anything else, or a result
that is not a result, throws `ExecutionPlanException`. Each alternative carries the first
score index at which it differs from the chosen answer.

`CanonicalJson` is RFC 8785: keys in UTF-16 order, numbers as JavaScript writes them, no
whitespace. A value that has no such spelling (a number beyond 2^53 - 1, a lone surrogate)
throws `CanonicalJsonException`.

## Operational artifacts

An operational artifact is the one document a warehouse or transport system needs to act
without calling an engine. It wraps the execution plan unchanged and adds geometry in ticks,
the result's display values, and provenance: the request itself, the catalog versions used,
the solver, and whether a replay can be exact. `ArtifactExports` turns it into canonical JSON,
an RFC 4180 CSV with one row per step and one per unpacked item, and a self-contained HTML work
order to print. Building and exporting call no solver, renderer or clock, and every Packvium
engine emits the same bytes.

```csharp
using Packvium.Artifacts;
using Packvium.Serialization;

var request = (JsonObject)JsonReader.Parse(requestJson);
var result = JsonApi.PackFromJson(request);
var orders = new Dictionary<int, IReadOnlyList<int>> { [0] = loadingOrder };   // optional

JsonObject artifact = OperationalArtifact.Build(request, result, orders);
string json = ArtifactExports.Json(artifact);            // RFC 8785, the bytes every engine builds
string csv = ArtifactExports.Csv(artifact);              // CRLF rows, quoted only where needed
string html = ArtifactExports.WorkOrderHtml(artifact);   // no script, no external resource

JsonValue stored = OperationalArtifact.Parse(json);      // a stored artifact exports the same way
```

`RouteSequence.SafeLoadingOrder` in `Packvium.Validation` computes a loading order from the
result's boxes; the artifact never invents one. Anything an artifact cannot carry throws
`OperationalArtifactException`, whose `Code` is the one every engine reports: `invalid_request`,
`invalid_result`, `invalid_plan_input`, `mixed_units`, `number_out_of_range`, `invalid_string`,
`invalid_value`, `unknown_format` or `invalid_json`. An export refuses a document of another
format by name, and a value with more than one spelling across languages (a boolean or a float
in a printed field) instead of guessing.

## Fixed placements and plan revisions

A request's `fixed_placements` are items already in a known place: loaded, or locked there by an
operator. Each names an item type, a container type and instance (from 1), a position and an
orientation. Fixed items take the first instances of their type, their containers open first
and are kept, and the solve packs the rest around them, with their weight, support and top load
counted. They come back with `"fixed": true`. A fixed set that cannot hold on its own is refused
with `FixedPlacementException` (a subclass of `InvalidRequestException`, code
`invalid_fixed_placement`) before any search, and the independent validator refuses an answer
that drops, moves or invents one.

A plan revision (`packvium-plan-revision/v1`) records what differed from an approved plan, as an
append-only chain hashed with SHA-256 over RFC 8785 bytes. Its events are `item_missing`,
`container_substituted`, `placement_locked` and `placement_verified`; locks and verified
placements become fixed placements of the derived request, and the replan is an ordinary solve
of it. Building a revision calls no solver, validator or clock, so every engine emits the same
bytes:

```csharp
using Packvium.Artifacts;
using Packvium.Revisions;
using Packvium.Serialization;

var root = PlanRevision.Root(JsonReader.Parse(requestJson));
var approved = OperationalArtifact.Build(root.Require("request"), JsonApi.PackFromJson(root.Require("request")));
var revision = PlanRevision.Derive(root, approved, JsonReader.Parse("""
    [{"sequence": 1, "type": "item_missing", "item_type": "slab", "quantity": 1}]
    """));
var replanned = JsonApi.PackFromJson(revision.Require("request"));        // an ordinary solve
var issues = PlanRevision.VerifyChain(new JsonArray([root, revision]));   // empty for an intact chain
string digest = PlanRevision.Digest(revision);                            // "sha256:..."
```

`OperationalArtifact.ReplayLevel(result)` answers the replay promise an artifact of a result would
make, `exact` or `not_guaranteed`, without building the artifact.

A revision that cannot be built throws `PlanRevisionException`, whose `Code` is one every engine
reports: `invalid_revision`, `invalid_event`, `event_conflict`, `invalid_artifact`, or a
canonical-form code. `RevisionOutcomes.Record` in `Packvium.Intelligence` appends a revision's
events to an `OutcomeLedger`, where holdout replay already reads them.

## Weight rebalancing

`WeightRebalance.Rebalance` evens out the payload across the containers of a finished packing. It
is a separate step you call after `Packer.Pack`, never part of a solve: each round moves one item
from the heaviest container to a lighter one, and keeps the move only if the item has a real place
there and the whole packing still passes the independent validator. It never adds a container,
drops an item or moves a fixed placement, and it stops when no single move narrows the spread, or
after `maxMoves`:

```csharp
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Units;

Item[] items = [new("paint", Dimensions.Mm("300", "300", "380"), Weight.Parse("24 kg"), 16, keepUpright: true)];
Container[] pallets = [new("pallet", Dimensions.Mm("1200", "800", "1000"), maxPayload: Weight.Parse("300 kg"))];
var packed = new Packer().Pack(items, pallets);
var rebalanced = WeightRebalance.Rebalance(new PackingRequest(items, pallets), packed.Containers, packed.Unpacked);
foreach (var move in rebalanced.Moves)
{
    Console.WriteLine($"{move.ItemId}: {move.FromContainerId} -> {move.ToContainerId}");
}
```

It is greedy, so the result is more even, not necessarily the most even split possible. Its time
limit (`timeLimitMs`, one second by default) is a wall clock, so a move search the clock cuts short
can differ between machines; give it a generous limit when the answer is stored or compared.

## Operator locks

An operator who is standing at the dock can pin a placement: *this printer stays here*.
`Packvium.Locks.OperatorLocks` re-solves the request with the lock applied and hands back a
second result beside the approved one, never an edited copy of it.

```csharp
using Packvium.Locks;

var locks = OperatorLocks.LocksFromPlan(plan, containerIndex: 0, itemTypes: ["printer"]);
var resolved = OperatorLocks.ResolveWithLocks(request, locks);

Console.WriteLine(resolved.Preserved);                  // True: every lock is in resolved.Result

var unreachable = new PlacementLock(containerIndex: 0, itemType: "printer", orientation: "LWH",
    positionTicks: (99_000_000, 0, 0));
var outcome = OperatorLocks.ResolveWithLocks(request, [unreachable]);
Console.WriteLine(outcome.Missing.Length);              // 1: the solve answered, the lock did not hold
```

A lock is addressed the way the execution plan addresses a placement: container index, item
type, orientation and position ticks, never an item id. It becomes an ordinary placement
constraint in the `ExtensionRegistry`, so the re-solve runs the same solvers and the same
independent validator as any other: a lock can reserve its own slot, and cannot make the
engine accept a placement it would otherwise refuse. Two kinds of failure are kept apart:

- **A lock the solve cannot honour** (it would float a box, overload one below it, block a
  route) comes back as a result with `Preserved == false` and the lock in `Missing`.
- **A lock set that contradicts itself** (two locks whose boxes overlap, the same slot twice,
  an item type the request does not carry) throws `LockSetException` before any solve.

Locks have no field in the JSON request, so they exist only in code.

## Scenario comparison, recommendations and holdout replay

`Packvium.Intelligence` answers a different question from packing: is a proposed change,
such as a new carton set, actually an improvement? None of it returns a single blended score.
Two runs are compared order by order as a Pareto report, so you see which orders improved,
which regressed and which traded one axis for another.

```csharp
using Packvium.Intelligence;

var higherIsBetter = new Dictionary<string, bool> { ["cost_minor"] = false, ["utilisation"] = true };
var baselinePin = new ScenarioVersionPin(catalogVersion: 1);
var treatmentPin = new ScenarioVersionPin(catalogVersion: 2);

// Your evaluator packs one order under one pin and reports its metrics.
OrderRunResult Evaluate(string orderId, ScenarioVersionPin pin) =>
    new(orderId, succeeded: true, metrics: Measure(orderId, pin));

var baseline = Scenarios.RunScenario("current-cartons", baselinePin, orderIds, Evaluate);
var treatment = Scenarios.RunScenario("proposed-cartons", treatmentPin, orderIds, Evaluate);
foreach (var report in Scenarios.CompareScenarios(baseline, treatment, higherIsBetter))
{
    Console.WriteLine($"{report.Profile}: {report.Winner ?? "no winner"}");
}

// null when the paired evidence is below the floors you set; never a proposal with a caveat.
var recommendation = Recommendations.Propose("rec-cartons-2024", "adopt the proposed carton set",
    baseline, treatment, constraints: ["no placement the validator would reject"],
    rollbackPlan: "republish catalog version 1", minimumCohortSize: 4, minimumConfidence: 0.75);
```

- **`Recommendations.Propose`** averages only orders both arms packed, and has no access to
  any registry. Publishing is a separate call: `Approve` with your own callback, or
  `ApproveCatalog` and `ApprovePolicy` through the commerce `CatalogRegistry` and
  `PolicyRegistry`, which are append-only.
- **`OutcomeLedger`** records what really happened to each decision: the carton that shipped,
  measured weight, damage, returns. Each event type accepts a closed list of fields, a
  redelivered event is idempotent, a correction supersedes rather than overwrites, and
  `ViewAsOf` answers what was known at a past instant.
- **`Holdout.EvaluateOnHistory`** replays a recommendation on the decisions a time split holds
  out. It refuses a recommendation that cites its own holdout, runs your independent
  validator before any metric is compared, counts an arm the validator rejects as a failure
  whatever it cost, and reports recorded damage against the carton that actually shipped.

`Intelligence.cs` runs the whole story end to end.

## Command line

The NuGet package is the library only. The command-line program in `src/Packvium.Cli` is not
packed; build it from source. It keeps the same contract as every Packvium command line:
- It reads a JSON packing request on standard input and writes the result JSON to standard
  output.
- When a request is refused, it exits with status 1 and prints the reason on standard error.

```bash
dotnet build Packvium.slnx -c Release
echo '{"items":[{"id":"box","quantity":8,"dimensions":{"length":"50","width":"50","height":"50"}}],
       "containers":[{"id":"carton","inner_dimensions":{"length":"100","width":"100","height":"100"}}]}' \
  | src/Packvium.Cli/bin/Release/net10.0/Packvium.Cli
```

`Packvium.Cli commerce` reads one commerce case (`operation`, `document`, `request`) the same
way and prints the canonical result document. A malformed case exits 1 with `input error:` on
standard error. A rejection is a normal answer and exits 0.

`Packvium.Cli artifacts`, `Packvium.Cli revisions` and `Packvium.Cli validate` are test-only
subcommands: each reads a JSON list of cases on standard input and writes one JSON line per case
on standard output. `validate` takes `{"request", "result"}` pairs of JSON text and answers
`{"issues": [...]}` or `{"refusal": {"kind", "message"}}` for each.

## Examples

[`examples/`](https://github.com/packvium/packvium-dotnet/tree/main/examples) has nineteen
examples, one file each, that you can read from top to bottom. Run one by name, or all of
them in turn:

```bash
dotnet run --project examples -- objectives
dotnet run --project examples
```

New here? Read `Basic.cs`, then `Objectives.cs`, which between them cover what most callers
need. `Units.cs` and `Serialization.cs` explain the two design choices that surprise people.
`Extensions.cs` comes late on purpose: reach for it only after the fields in `Constraints.cs`
have failed you.

| File | Run it with | What it shows |
| --- | --- | --- |
| [`Basic.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Basic.cs) | `-- basic` | The smallest useful call: items go in, placements come out. |
| [`Objectives.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Objectives.cs) | `-- objectives` | Six of the seven objectives (all but `maximum_volume`), on loads where they choose differently, including the rate card that makes the heavier shipment the cheaper one. |
| [`Constraints.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Constraints.cs) | `-- constraints` | Upright-only, floor-only, non-stackable, top-load limits and tags that keep two items apart, plus how to read why an item was refused. |
| [`Units.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Units.cs) | `-- units` | Why there are no floats: fractional inches, exact ticks, and the one tick between a fit and a refusal. |
| [`Serialization.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Serialization.cs) | `-- serialization` | The request as JSON, the result in full, and which mistakes are refused and which are ignored. |
| [`Shapes.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Shapes.cs) | `-- shapes` | Items that do not fill their box: wedges sharing a crate as `convex_hull`, and a cushion that compresses until the crush limit refuses it. |
| [`Nested.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Nested.cs) | `-- nested` | Units go into cartons and cartons go onto a pallet, in one call. |
| [`Commerce.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Commerce.cs) | `-- commerce` | Rate a shipment, apply an eligibility rule and pin a catalog version. |
| [`Extensions.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Extensions.cs) | `-- extensions` | A rule the request has no field for, a ranking of your own, and what you give up by writing them. |
| [`Execution.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Execution.cs) | `-- execution` | A result turned into a work order: facts kept apart from screen text, a step order that is supplied or honestly absent, the same bytes in every engine, and an operator lock that is honoured, refused, or rejected as a contradiction. |
| [`Intelligence.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Intelligence.cs) | `-- intelligence` | Prove a carton change is better before publishing it: two scenarios compared order by order, a proposal or an explicit refusal to make one, and a replay on history the proposal has never seen. |
| [`Artifacts.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Artifacts.cs) | `-- artifacts` | Hand a result to a warehouse system with no engine: one document with the request and solver inside, a CSV to import, a work order to print, and a refusal of a format it does not know. |
| [`Revisions.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Revisions.cs) | `-- revisions` | Replan a half-loaded job: a missing slab and a locked cube recorded against the approved plan, a replan that keeps the cube where it is, and an audit that notices an edited chain. |
| [`Rebalancing.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Rebalancing.cs) | `-- rebalancing` | Even out two pallets after packing: the moves made, the packing still valid, and a single pallet left as it is. |
| [`Reproducibility.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Reproducibility.cs) | `-- reproducibility` | The same answer on every machine: a clock-limited search on four simulated hosts, an effort budget that answers alike on all of them, and how to read `termination`. |
| [`FixedPlacements.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/FixedPlacements.cs) | `-- fixed_placements` | A trailer already partly loaded: packed around what is on board, the cost of a pallet left in the middle, and a record that cannot be true. |
| [`Limits.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Limits.cs) | `-- limits` | A van and cargo bikes: obstacles, `max_items`, tag limits, container eligibility and `max_containers`, and what each refusal proves. |
| [`Trucking.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Trucking.cs) | `-- trucking` | A van on a three-stop route within its axle ratings: the axle reactions, the unloading and loading orders, and what the driver can reach. |
| [`Errors.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Errors.cs) | `-- errors` | A refused request turned into a useful answer, the way a web handler would: reason, pointer and message, items in place that cannot be there, and a short load that is not an error. |

The test suite runs every example and checks its output against a recorded copy; the output
uses the notation shared by every Packvium language.

## Determinism

The same input and seed always give the same result, on every machine and every run:

- **No floating-point geometry.** Length is counted in ticks of 1/16000 mm and weight in ticks
  of 1/8 µg, so rounding cannot change where an item is placed.
- **No hidden inputs.** Nothing reads the culture, the random number generator or the order of
  a hash table. Names are ordered by Unicode code point, as in every Packvium engine.
- **The clock only stops a search.** Its only effect is the time limit you set. When you need
  byte-identical results under truncation too, set an effort budget instead
  (`configuration.effort_budget`); the examples set a generous 60-second limit as a safety
  fuse, so a busy machine cannot cut a solve short.
- **Checked answers.** An independent validator checks every solution again, with logic
  separate from the search, and every item that was not placed comes back with a reason code
  and the level of proof behind it.

## Guarantees

The guarantees are the same as in every Packvium engine, and
[Packvium's guarantees](https://github.com/packvium/packvium-python/blob/v1.7.0/docs/GUARANTEES.md)
states them. Every release of this package is checked against the shared Packvium conformance
corpus for that version, which lives outside this repository:

- **Packing.** All 451 packing requests with a recorded answer give the reference engine's
  result byte for byte in canonical form, and all 12 requests that must be refused are refused
  with the expected message.
- **Everything built from a result.** The execution plan of every one of those 451 results,
  all 472 operational-artifact cases (JSON, CSV and HTML, or the expected error code), all 102
  plan-revision cases and all six operator-lock cases give the reference engine's bytes.
- **Requests and commerce.** All 207 malformed requests are refused with the reference engine's
  code, reason, field and message, built as JSON and, wherever objects can hold them, as
  objects, and all 55 commerce cases give its answer.
- **Warnings and reasons.** The 3 cases that pin the unsupported-placements warning and the 12
  that pin an unpacked reason give the expected warning and reasons.
- **Result validation.** `ResultValidation.Validate` gives the reference validator's issues, or
  its refusal, on every one of the 5451 pairs of the shared validation suite.

In this repository, `dotnet test` runs the unit tests, the CLI tests and every example; the
tests that need the conformance corpus skip and say why.

## Differences from the reference engine

Valid requests get identical results. This package differs in a few places, none of which the
conformance corpus reaches:

- **64-bit ticks.** Lengths and weights are stored as 64-bit ticks, as in PHP. If a value would
  overflow, the request is refused, whereas Python would keep calculating with larger
  integers.
- **Schema-invalid input is refused.** A few such values are refused where Python would happen
  to coerce them, for example a non-string tag.
- **No parallel portfolio.** It can only be enabled programmatically, never from a request, so
  it is not included. Every start runs one after another, which is how the reference engine
  runs them.

## Versioning

The package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html), and each
version matches the Packvium release of the same number: 1.7.0 here passes the 1.7.0
conformance corpus. The request and result formats, status codes and the objective vector are
frozen for all of 1.x. What changed in each release is in the
[changelog](https://github.com/packvium/packvium-dotnet/blob/main/CHANGELOG.md).

## The Packvium family

There are packages for Python (`pip install packvium`), PHP (`composer require
packvium/packvium`), Java, Go, Rust, Node.js and the browser. They all accept the same requests,
so you can mix them in one system. Python, PHP, Java, .NET and Go return identical results at
the same version; Rust and JavaScript return a valid answer at or above the objective floor.
Documentation, the constraint reference and benchmarks are at [packvium.com](https://packvium.com).

## License

MIT.
