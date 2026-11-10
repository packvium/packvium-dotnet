# Changelog

What changed in `Packvium` on NuGet, release by release. The format follows
[Keep a Changelog](https://keepachangelog.com/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
