# Changelog

What changed in `Packvium` on NuGet, release by release. The format follows
[Keep a Changelog](https://keepachangelog.com/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0]

Matches Packvium 1.2.0: all 398 packing fixtures of the 1.2.0 conformance corpus, byte for
byte in canonical form, all 55 commerce cases, the execution plan of every golden result, and
the six operator-lock scenarios of the release. Packing results are unchanged from 1.1.0.

### Added

- **Execution plans.** `Packvium.Execution.ExecutionPlan.Build` derives a plan an operator can
  follow from a result document: the solver's facts kept apart from presentation text that
  cites the fields it was built from, placements referenced by container index, item type,
  orientation and position ticks, a numbered loading order only when you supply one, and the
  first score index at which each alternative differs from the winner. It calls no solver and
  no validator. `ExecutionPlan.CanonicalJson` is byte-identical to the reference adapter's on
  every golden result; a malformed result throws `ExecutionPlanException`.
- **Operator locks.** `Packvium.Locks.OperatorLocks.ResolveWithLocks` re-solves a request with
  some placements pinned by `(container index, item type, orientation, position ticks)`, read
  from an execution plan with `LocksFromPlan` or built as `PlacementLock`. A lock is an
  ordinary placement constraint added through `ExtensionRegistry`, so the re-solve runs the
  same solvers and the same independent validator; the approved result is left untouched.
  `LockedResolve.Preserved` and `Missing` report, measured on the new result, which locks held.
  A lock set that contradicts itself (overlapping boxes, the same slot twice, an item type the
  request does not carry) throws `LockSetException` before any solve.
- **`JsonApi.PackFromJson(request, extensions)`.** The caller's placement constraints are
  added after the ones the request's `policy` compiles to, never substituted for them; the
  caller's orderings, solvers and container selector are used as given.
- **Scenario comparison, recommendations and holdout replay** in `Packvium.Intelligence`:
  - `Scenarios.RunScenario` runs an order corpus through your evaluator under a
    `ScenarioVersionPin`, and `Scenarios.CompareScenarios` compares two runs order by order as
    a Pareto report (`Pareto.GenerateReport`, `Pareto.Dominates`), never as one blended score.
  - `Recommendations.Propose` builds a `Recommendation` from paired evidence, or returns `null`
    when the cohort or its confidence is below the floors you pass. Only `Approve`,
    `ApproveCatalog` and `ApprovePolicy` publish, through your callback or the commerce
    `CatalogRegistry` and `PolicyRegistry`.
  - `OutcomeLedger` is an append-only record of what happened to each decision, with a closed
    field list per event type, idempotent redelivery, corrections that supersede rather than
    overwrite, and `ViewAsOf` for what was known at a past instant.
  - `Holdout.EvaluateOnHistory` scores a recommendation on the decisions a time split holds
    out, refuses one that cites its own holdout, and counts an arm your validator rejects as a
    failure whatever it cost.
- **An `Execution` example** covering execution plans and operator locks, and an
  **`Intelligence` example** covering scenario comparison, recommendations and holdout replay,
  each checked against the output of the matching Python example.

### Changed

- **The examples set a 60-second time limit** as a safety fuse, so a busy machine cannot cut a
  solve short and change what an example prints.
- **Candidate search allocates less.** A bounded search keeps only the candidates it will
  return, a stack's load order is derived from the one before it instead of sorted again, and
  support edges are read from the contact graph instead of copied. Over the 398 corpus
  fixtures in one process, allocation fell from about 1110 MB to about 935 MB. Results,
  counters and alternatives are unchanged.

## [1.1.0]

Matches Packvium 1.1.0: all 398 packing fixtures of the 1.1.0 conformance corpus, byte for
byte in canonical form, and all 55 commerce cases.

### Added

- **Container doors.** `Container(accessDirections: ...)` and the request field
  `container.access_directions` name the walls a container is unloaded through, from
  `+x -x +y -y +z -z`. With doors stated, an item due at an earlier stop must keep a clear
  path to one of them. Without them, results are unchanged from 1.0.0.

### Changed

- **`container.pallet_overhang_limit` is refused.** The field is reserved in the 1.1.0 schema
  and not implemented by any engine, so a request that sets it throws
  `UnsupportedFeatureException` (`unsupported_feature`) instead of being packed as if it were
  absent.
- **Candidate search does less work.** Rules that cannot fire for an item are dropped once per
  search instead of being asked at every position, and point and surface lookups use the
  spatial index instead of scanning every placed box. Results are unchanged.

## [1.0.0]

The first release of Packvium for .NET: a full C# implementation of the Packvium engine, for
.NET 8 and .NET 10, with no runtime dependencies.

### Added

- **Packing.** `Packer` runs the same solver portfolio as the other engines: grid, layer,
  extreme-point, maximal-space, homogeneous-block and exact-small. It returns placements in
  exact integer ticks, a five-key objective vector and a reason for every item left behind.
  An independent validator checks every answer.
- **The JSON contract.** `JsonApi.PackFromJson` accepts a request in the shared format and
  returns the same result document as the Python and PHP engines, byte for byte in its
  canonical form, on all 396 packing fixtures of the 1.0.0 conformance corpus.
- **Commerce.** `CommerceApi.Quote`, `EvaluatePolicy` and `CatalogVersionInfo` price a
  shipment, decide an eligibility question and resolve a catalog version. They pass all 55
  commerce conformance cases.
- **Nine examples**, each checked against the output of the matching Python example.
