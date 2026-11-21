# Packvium for .NET

Deterministic 3D cartonization and rectangular bin packing for .NET, with exact integer
geometry and no runtime dependencies.

Packvium for .NET is a full C# reimplementation of the Packvium reference engine, not a
binding over another one. It accepts the same requests and returns the same results as the
Python and PHP engines. Its tests run the shared conformance corpus, and every result
matches the reference result byte for byte.

> **Version 1.4.0.** This release corresponds to Packvium 1.4.0 in the other languages, and
> its tests run the 1.4.0 conformance corpus: every packing fixture, every commerce case,
> every execution plan, every operator-lock scenario, every operational-artifact case, every
> malformed-request case and every plan-revision case.
> Field names, status codes and the objective vector stay the same for all of 1.x.
> Read [GUARANTEES.md](https://github.com/packvium/packvium-python/blob/main/docs/GUARANTEES.md)
> before relying on a result.

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [Packing with domain objects](#packing-with-domain-objects)
- [The JSON request API](#the-json-request-api)
- [Constraints and objectives](#constraints-and-objectives)
- [Commerce](#commerce)
- [Execution plans](#execution-plans)
- [Operational artifacts](#operational-artifacts)
- [Fixed placements and plan revisions](#fixed-placements-and-plan-revisions)
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
dotnet add package Packvium --version 1.4.0
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

## The JSON request API

A request is a JSON document and so is the result. `JsonApi.PackFromJson` takes the same
request the other engines take and returns the same result document:

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
is a floor, and no length or weight may be negative. `container.pallet_overhang_limit` is reserved in the schema and not implemented by any
engine, so a request that sets it is refused with `unsupported_feature` rather than packed
as if it were absent.

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
lexicographically, and every one puts the number of items left behind first:

```csharp
var cheapest = new PackingConfig(objective: "lowest_cost", timeLimitMs: 60_000);
var byWeight = new PackingConfig(objective: "shipping_cost", dimensionalWeightDivisor: 5000, timeLimitMs: 60_000);
```

`default`, `lowest_cost`, `shipping_cost`, `lowest_landed_cost`, `open_dimension_height` and
`maximum_value` are available; `Objectives.cs` shows each one choosing differently.

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

string canonical = ExecutionPlan.CanonicalJson(plan);   // the same bytes as the reference adapter
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

A revision that cannot be built throws `PlanRevisionException`, whose `Code` is one every engine
reports: `invalid_revision`, `invalid_event`, `event_conflict`, `invalid_artifact`, or a
canonical-form code. `RevisionOutcomes.Record` in `Packvium.Intelligence` appends a revision's
events to an `OutcomeLedger`, where holdout replay already reads them.

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

`src/Packvium.Cli` is a command-line program with the same contract as the other engines'
CLIs:
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

`Packvium.Cli artifacts` is the batch probe of the release's artifact conformance run, and
`Packvium.Cli revisions` that of its plan-revision and malformed-request runs: a JSON list of
cases on standard input, one JSON line per case on standard output.

## Examples

[`examples/`](https://github.com/packvium/packvium-dotnet/tree/main/examples) has thirteen
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
| [`Objectives.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Objectives.cs) | `-- objectives` | All six objectives, on scenes where they choose differently, including the rate card that makes the heavier shipment the cheaper one. |
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

The test suite runs every example and compares its output with what the Python example of
the same release prints. The only lines allowed to differ are listed in the test's
`LanguageLines` map, because they are about the language rather than the packing: a file or
command name, how each language spells null, a Python-only refusal that the .NET example
omits, and section 4 of `execution`, which speaks for this engine rather than for four.

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
[GUARANTEES.md](https://github.com/packvium/packvium-python/blob/main/docs/GUARANTEES.md) states
them. This port adds the following:

- **Every corpus case passes.** All 400 packing fixtures of the 1.4.0 conformance corpus match
  the reference engine's canonical result byte for byte, all 55 commerce cases pass the
  release's own commerce harness, the execution plan of every one of the 400 golden results is
  byte-identical to the reference adapter's, all six operator-lock scenarios of the release
  pass, all 421 operational-artifact cases (400 results and 21 edge cases) give the
  reference's JSON, CSV and HTML byte for byte, or its error code, all 122 malformed requests
  are refused with the reference's code, reason, field and message, and all 102 plan-revision
  cases give the reference's bytes, digest, audit or refusal.
- **Conformance is checked by the release's own harness.** The command-line program is driven
  through that harness, the reference's independent result validator accepts every result, and
  the release's artifact schema and reference consumer accept every artifact, and its
  revision schema every revision.

Full conformance runs in the Packvium suite repository, which holds the shared corpus. In this
repository, `dotnet test` runs the unit tests, the CLI tests and every example against the
Python example's recorded output; the corpus tests skip and say why.

## Differences from the reference engine

Valid requests get identical results. The port differs in a few places, none of which the
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
version matches the Packvium release of the same number: 1.4.0 here passes the 1.4.0
conformance corpus. The request and result formats, status codes and the objective vector are
frozen for all of 1.x. What changed in each release is in the
[changelog](https://github.com/packvium/packvium-dotnet/blob/main/CHANGELOG.md).

## The Packvium family

There are packages for Python (`pip install packvium`), PHP (`composer require
packvium/packvium`), Rust, Node.js and the browser. They all accept the same requests and
return the same results, so you can mix them in one system. Documentation, the constraint
reference and benchmarks are at [packvium.com](https://packvium.com).

## License

MIT.
