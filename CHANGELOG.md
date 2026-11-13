# Changelog

What changed in `Packvium` on NuGet, release by release. The format follows
[Keep a Changelog](https://keepachangelog.com/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
