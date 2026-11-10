# Contributing

Thanks for looking. This library is dependency-free and deliberately strict about
exactness. Please keep it that way.

## Getting set up

You need the .NET 10 SDK; the tests also run on the .NET 8 runtime when it is installed.

```bash
dotnet build Packvium.slnx
dotnet test --solution Packvium.slnx -f net10.0
```

The suite should be green before you change anything, so that any later failure is yours.

## The rules that matter here

**No floats in geometry or feasibility.** Every length is an integer number of ticks
(1/16000 mm) and every weight an integer number of 1/8 µg. A change that makes a placement
decision depend on binary floating point is wrong even if the tests pass. Ratios are compared
as scaled integers or exact rationals. See
[UNITS-AND-NUMERICS.md](https://github.com/packvium/packvium-python/blob/main/docs/UNITS-AND-NUMERICS.md).

**No runtime dependencies.** The package installs with nothing else. A new dependency needs a
very good argument.

**Inputs are immutable.** A pack call must never mutate the items or containers it was given.
Requests, configuration and results are immutable value objects.

**Determinism.** The same input and seed must produce the same output. Never let the order of
a dictionary or set reach a result: sort explicitly, and compare strings by code point
(`CodePointComparer`), as every other engine does.

**Warnings are errors.** The build treats every compiler and analyzer warning as an error.
Fix the cause; do not suppress it.

**Every fixed defect gets a regression test.** Not "a test somewhere": a test that fails before
your fix and passes after.

## Behavioural changes

This package is one of several implementations of the same documented contract, held to
identical results on a shared conformance corpus. That means:

- A change to solver behaviour, the objective vector, serialization field names or status
  semantics is a **cross-language change**. Open an issue describing it before writing code,
  so the other implementations move with it.
- A change confined to .NET internals (types, refactoring, performance without a change in
  output) is local and needs no coordination.

The examples are the quickest check of which kind you have: `ExamplesTests` compares every
example's output with the Python example's, so a moved placement shows up there.

## Pull requests

- One logical change per pull request.
- Commit messages in imperative mood, under 72 characters:
  `type(scope): description` with `feat`, `fix`, `refactor`, `chore`, `docs` or `test`.
- Add or update tests. `dotnet test` must pass on `net8.0` and `net10.0`.
- Do not bump the version; releases are cut separately.

## Reporting a bug

Please include the full request that reproduces it, with its items, containers and
configuration, and what you expected instead. A packing bug is nearly impossible to act on
without the exact input.

For anything with security implications, follow [SECURITY.md](SECURITY.md) rather than
opening a public issue.

## How your feedback is handled

Every issue and discussion is read by a maintainer. This is what happens next:

1. **New issues are labelled `needs-triage`.** A maintainer answers with one of: a question
   back to you, a confirmation, or a reason it will not be taken on.
2. **A confirmed bug** gets the `bug` label and is reproduced from your request. A wrong result
   in one package is checked against the others, because all of them implement the same
   contract and must agree.
3. **An accepted feature request or idea** is added to the project's backlog, and the issue is
   labelled `accepted` with a comment saying so. An idea from Discussions is first turned into
   an issue and linked back to the discussion.
4. **When the change ships**, the issue is closed with the release it shipped in. A change to
   the request or result format can only ship in a release that is allowed to change it, so
   it may take more than one release.
5. **Declined** requests are closed with the reason, so you are never left waiting on a
   silent issue.

Questions go to **Discussions → Q&A**; security problems go to **Report a vulnerability**
under the Security tab, never to a public issue.
