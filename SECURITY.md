# Security policy

## Supported versions

Only the latest `1.x` release receives fixes. There is no long-term support branch.

## Reporting a vulnerability

Please report privately, not in a public issue. Use GitHub's **Report a vulnerability**
button under the Security tab, which opens a private advisory.

Include the version, the .NET runtime, a minimal request that reproduces the problem, and
what you observed. A crash, a hang or a wildly wrong result on a small input is worth
reporting even if you are unsure it is a security matter.

You can expect an acknowledgement within a few working days and an assessment after that.
Please give us a chance to ship a fix before disclosing publicly.

## Threat model

This is a computation library. It has **no runtime dependencies**, opens no network
connections, spawns no processes, and executes no user-supplied code except the extension
objects you register yourself. The command-line tool, built from source, reads one request
from the input path it is given (or standard input) and writes the result to standard output
or to the file named with `-o`; it touches no other file.

The realistic risks are therefore about untrusted **input**:

- **Resource exhaustion.** A request with a large item count, many container types or a
  permissive solver profile can consume substantial CPU and memory. If you accept requests
  from untrusted callers, set `time_limit_ms` and an `effort_budget`, cap item quantities and
  container inventory, and run the call where you can bound memory. The deadline is honoured
  by the search, but a single enormous request can still allocate a lot before the first
  check.
- **Integer magnitude.** Dimensions and weights become exact 64-bit integer ticks (16 000 per
  millimetre). A value too large for 64 bits is refused rather than allowed to overflow, but
  large values within range still cost time and memory. Validate dimensions against a sane
  maximum before passing them in.
- **Malformed input.** Bad units, unparseable numbers and contradictory constraints throw an
  exception rather than producing a wrong packing. Do not suppress those exceptions.
- **Results you did not compute.** `ResultValidation.Validate` is meant for results from
  any source, so it reads them as untrusted: a document that is not the contract's shape is
  refused with `MalformedResultException`, and its arithmetic is exact at any size. Its cost
  grows with the number of placements per container (pairwise overlap checks), so bound the
  size of documents you accept from untrusted callers.

A result that reports success has passed independent validation, but validation checks the
constraints you declared. It cannot know about a constraint you did not express.

## Extensions

Custom constraints, orderings, scorers, container selectors and solvers run with the
privileges of your process. Treat a third-party extension as you would any other dependency:
read it before you register it.

## How releases are published

Every release is built and published from this repository's CI, never from a maintainer's
workstation.

- **NuGet Trusted Publishing.** The release workflow runs in the `nuget` environment and
  exchanges its GitHub OIDC identity for a short-lived NuGet key (`NuGet/login`). No
  long-lived API key exists, so there is none to leak.
- **Provenance.** Each package also carries a GitHub build-provenance attestation, which binds
  it to the workflow run, commit and tag that produced it.
- **Revocation.** A defective or compromised version is unlisted or deprecated on NuGet rather
  than deleted, so that projects already pinned to it get a clear signal. The security
  advisory names the affected versions.
- **Recovery.** If the workflow or its trust configuration is suspected compromised, the
  trust policy is revoked at once, the workflow's recent runs are audited, and the fix is
  released from a clean, reviewed commit under a new version.
