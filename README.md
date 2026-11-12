# Packvium for .NET

Deterministic 3D cartonization and rectangular bin packing for .NET, with exact integer
geometry and no runtime dependencies.

Packvium for .NET is a full C# reimplementation of the Packvium reference engine, not a
binding over another one. It accepts the same requests and returns the same results as the
Python and PHP engines. Its tests run the shared conformance corpus, and every result
matches the reference result byte for byte.

> **Version 1.0.0.** This release corresponds to Packvium 1.0.0 in the other languages, and
> its tests run the 1.0.0 conformance corpus: every packing fixture and every commerce case.
> Field names, status codes and the objective vector stay the same for all of 1.x.
> Read [GUARANTEES.md](https://github.com/packvium/packvium-python/blob/main/docs/GUARANTEES.md)
> before relying on a result.

## Install

```bash
dotnet add package Packvium
```

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

Fractional inches are exact, not approximated:

```csharp
Dimensions.Inches("12 3/8", "8 1/2", "3/4");
```

## Command line

`src/Packvium.Cli` is a command-line program with the same contract as the other engines'
CLIs:
- It reads a JSON packing request on standard input and writes the result JSON to standard
  output.
- When a request is refused, it exits with status 1 and prints the reason on standard error.

```bash
echo '{"items":[{"id":"box","quantity":8,"dimensions":{"length":"50","width":"50","height":"50"}}],
       "containers":[{"id":"carton","inner_dimensions":{"length":"100","width":"100","height":"100"}}]}' \
  | src/Packvium.Cli/bin/Release/net10.0/Packvium.Cli
```

`Packvium.Cli commerce` reads one commerce case (`operation`, `document`, `request`) the same
way and prints the canonical result document. A malformed case exits 1 with `input error:` on
standard error. A rejection is a normal answer and exits 0.

## Examples

[`examples/`](https://github.com/packvium/packvium-dotnet/tree/main/examples) has nine examples, one file each, that you can read from top to
bottom. Run one by name:

```bash
dotnet run --project examples -- objectives
```

The test suite runs every example and compares its output with what the Python example of
the same release prints. Four lines are allowed to differ because they are about the language
rather than the packing: two name a file or a command, one is how each language spells null,
and one shows a Python-only refusal that the .NET example omits.

New here? Read `Basic.cs`, then `Objectives.cs`, which between them cover what most callers
need. `Units.cs` and `Serialization.cs` explain the two design choices that surprise people.
`Extensions.cs` is last on purpose: reach for it only after the fields in `Constraints.cs`
have failed you.

| File | What it shows |
| --- | --- |
| [`Basic.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Basic.cs) | The smallest useful call: items go in, placements come out. |
| [`Objectives.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Objectives.cs) | All six objectives, on scenes where they choose differently, including the rate card that makes the heavier shipment the cheaper one. |
| [`Constraints.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Constraints.cs) | Upright-only, floor-only, non-stackable, top-load limits and tags that keep two items apart, plus how to read why an item was refused. |
| [`Units.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Units.cs) | Why there are no floats: fractional inches, exact ticks, and the one tick between a fit and a refusal. |
| [`Serialization.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Serialization.cs) | The request as JSON, the result in full, and which mistakes are refused and which are ignored. |
| [`Shapes.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Shapes.cs) | Items that do not fill their box: wedges sharing a crate as `convex_hull`, and a cushion that compresses until the crush limit refuses it. |
| [`Nested.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Nested.cs) | Units go into cartons and cartons go onto a pallet, in one call. |
| [`Commerce.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Commerce.cs) | Rate a shipment, apply an eligibility rule and pin a catalog version. |
| [`Extensions.cs`](https://github.com/packvium/packvium-dotnet/blob/main/examples/Extensions.cs) | A rule the request has no field for, a ranking of your own, and what you give up by writing them. |

## What it does

- **Exact arithmetic.** Length is counted in ticks of 1/16000 mm and weight in ticks of
  1/8 µg. No coordinate is ever a floating-point number, so rounding cannot change where an
  item is placed.
- **Real constraints:**
  - weight and payload limits, allowed rotations, keep-upright, floor-only, non-stackable;
  - top-load, stack-count and stack-density limits, minimum support ratio;
  - incompatible tags, clearance and obstacles;
  - axle loads, route order, convex-hull items and compressible items;
  - versioned policy rules.
- **Several solvers, not one.** Grid, layer, extreme-point, maximal-space, homogeneous-block
  and exact search for small problems. Which ones run depends on the shape of the problem and
  the chosen profile.
- **Checked answers.** An independent validator checks every solution again, with logic
  separate from the search. Every item that was not placed comes back with a reason code and
  the level of proof behind it.
- **Deterministic.** The same input and seed always give the same result. The only effect of
  the clock is the time limit you set; set an effort budget instead when you need
  byte-identical truncation.
- **Multiple and nested containers.** Items can be split across several containers, and
  containers can be packed inside other containers.
- **Extensible.** You can register your own placement constraints, item orderings, container
  selectors, solution scorers and complete solvers.
- **Commerce.** `Packvium.Commerce.CommerceApi` prices a shipment, evaluates an eligibility
  policy and resolves catalog versions. These are deterministic functions of the document you
  pass in: they do not read the clock, use the network or keep hidden state.

## Guarantees

The guarantees are the same as in every Packvium engine, and
[GUARANTEES.md](https://github.com/packvium/packvium-python/blob/main/docs/GUARANTEES.md) states
them. This port adds the following:

- **Every corpus case passes.** All 396 packing fixtures of the 1.0.0 conformance corpus match
  the reference engine's canonical result byte for byte, and all 55 commerce cases pass the
  release's own commerce harness.
- **Conformance is checked by the release's own harness.** The command-line program is driven
  through that harness, and the reference's independent result validator accepts every result.

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

## Requirements

.NET 8 or newer. There are no runtime dependencies.

## The Packvium family

There are packages for Python (`pip install packvium`), PHP (`composer require
packvium/packvium`), Rust, Node.js and the browser. They all accept the same requests and
return the same results, so you can mix them in one system. Documentation, the constraint
reference and benchmarks are at [packvium.com](https://packvium.com).

## License

MIT.
