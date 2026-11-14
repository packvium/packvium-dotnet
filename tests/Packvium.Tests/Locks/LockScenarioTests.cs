using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Locks;
using Packvium.Serialization;
using Packvium.Tests.Conformance;
using Xunit;

namespace Packvium.Tests.Locks;

/// <summary>
/// The operator-lock leg of the tag's execution-plan run (<c>conformance/execution/locks.py</c>).
/// A lock has no wire form, so there is no fixture to hand an engine over a pipe; the six
/// scenarios are driven in process instead, each certifying what the reference certifies: a
/// feasible lock is preserved and costs the load nothing, an unsafe one is refused with a
/// deterministic diagnostic, a self-contradictory set is refused before any solve, the approved
/// plan is unchanged and the request is not mutated. Each unsafe scenario is unsafe for a
/// different reason -- support, top load, route order -- and none of them is known to the lock
/// layer: they are the engine's rules on the ordinary path.
/// </summary>
public sealed class LockScenarioTests(ITestOutputHelper output)
{
    private const long Mm = 16_000;

    /// <summary>One cube in a column twice its height, fully supported or not at all.</summary>
    private const string UnsupportedColumn = """
        {"configuration": {"solver_profile": "fast", "time_limit_ms": 300000, "minimum_support_ratio": 1.0},
         "items": [{"id": "cube", "quantity": 1, "dimensions": {"length": "100", "width": "100", "height": "100"}}],
         "containers": [{"id": "box", "inner_dimensions": {"length": "100", "width": "100", "height": "200"}}]}
        """;

    /// <summary>A base that refuses any load at all, and one item that would sit on it.</summary>
    private const string ZeroTopLoad = """
        {"configuration": {"solver_profile": "balanced", "time_limit_ms": 300000},
         "items": [
           {"id": "base", "quantity": 1, "must_be_on_floor": true,
            "dimensions": {"length": "200", "width": "200", "height": "100"},
            "weight": {"value": "10", "unit": "kg"}, "max_top_load": {"value": "0", "unit": "kg"}},
           {"id": "filler", "quantity": 1, "dimensions": {"length": "200", "width": "200", "height": "100"},
            "weight": {"value": "1", "unit": "kg"}}],
         "containers": [{"id": "van", "inner_dimensions": {"length": "200", "width": "200", "height": "200"}}]}
        """;

    /// <summary>What a scenario expects: the lock held, it did not, or the set is refused before the solve.</summary>
    private enum Expected
    {
        Preserved,
        NotPreserved,
        LockSetRefused,
    }

    [Fact]
    public void EveryLockScenarioOfTheTagPasses()
    {
        var fixtures = Path.Combine(ConformanceTests.CorpusDirectory(), "conformance", "fixtures");
        if (!File.Exists(Path.Combine(fixtures, "exact-fit.json")))
        {
            Assert.Skip(ConformanceTests.CorpusAbsent(fixtures));
        }
        var eightCubes = Fixture(fixtures, "exact-fit.json");
        var route = Fixture(fixtures, "feature-route-unloading-enforced.json");
        var scenarios = new (string Name, JsonObject Request, PlacementLock[] Locks, Expected Expected)[]
        {
            ("feasible", eightCubes, [new(0, "cube", "LWH", (0, 0, 0))], Expected.Preserved),
            ("feasible-far-corner", eightCubes, [new(0, "cube", "LWH", (100 * Mm, 100 * Mm, 100 * Mm))], Expected.Preserved),
            ("colliding", eightCubes, [new(0, "cube", "LWH", (0, 0, 0)), new(0, "cube", "LWH", (50 * Mm, 0, 0))],
                Expected.LockSetRefused),
            ("unsupported", Json(UnsupportedColumn), [new(0, "cube", "LWH", (0, 0, 100 * Mm))], Expected.NotPreserved),
            ("overload", Json(ZeroTopLoad), [new(0, "filler", "LWH", (0, 0, 100 * Mm))], Expected.NotPreserved),
            ("route-incompatible", route, [new(0, "first-stop", "LWH", (0, 0, 0))], Expected.NotPreserved),
        };

        var failures = new List<string>();
        var passed = 0;
        foreach (var (name, request, locks, expected) in scenarios)
        {
            var problems = Check(request, locks, expected).Select(problem => $"{name}: {problem}").ToList();
            failures.AddRange(problems);
            passed += problems.Count == 0 ? 1 : 0;
        }
        output.WriteLine($"lock scenarios: {passed} of {scenarios.Length} pass");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
        Assert.Equal(6, scenarios.Length);
    }

    private static IEnumerable<string> Check(JsonObject request, PlacementLock[] locks, Expected expected)
    {
        var approved = JsonApi.PackFromJson(request);
        var frozen = PythonJson.Dumps(request, sortKeys: true);

        if (expected == Expected.LockSetRefused)
        {
            var refused = false;
            try
            {
                OperatorLocks.ResolveWithLocks(request, locks);
            }
            catch (LockSetException)
            {
                refused = true;
            }
            if (!refused)
            {
                yield return "a self-contradictory lock set was solved anyway";
            }
            yield break;
        }

        var resolve = OperatorLocks.ResolveWithLocks(request, locks);
        var preserve = expected == Expected.Preserved;
        if (resolve.Preserved != preserve)
        {
            yield return $"preserved={resolve.Preserved}, expected {preserve} "
                         + $"(missing: [{string.Join(", ", resolve.Missing.Select(missing => missing.SlotText))}])";
        }
        if (!resolve.Preserved && resolve.Missing.IsEmpty)
        {
            yield return "an unhonoured lock was not named";
        }
        // Re-derived after the locked solve rather than snapshotted before it: solving again proves
        // that locking left no state behind, which a copy cannot.
        if (Semantic(JsonApi.PackFromJson(request)) != Semantic(approved))
        {
            yield return "the approved plan did not survive the locked solve";
        }
        if (PythonJson.Dumps(request, sortKeys: true) != frozen)
        {
            yield return "the request was mutated";
        }
        // An unsafe lock must be refused, never worked around: it may not cost the load placements.
        var feasible = ((JsonString)((JsonObject)resolve.Result.Require("feasibility")).Require("code")).Value == "feasible";
        if (feasible && !preserve && LockTests.Count(resolve.Result) < LockTests.Count(approved))
        {
            yield return $"refusing a lock cost the load {LockTests.Count(approved) - LockTests.Count(resolve.Result)} placement(s)";
        }
        // Through the semantic projection: duration_ms is a wall clock, not the answer.
        var again = OperatorLocks.ResolveWithLocks(request, locks);
        if (Semantic(again.Result) != Semantic(resolve.Result))
        {
            yield return "two identical lock resolves disagreed";
        }
        if (again.Preserved != resolve.Preserved || !again.Missing.SequenceEqual(resolve.Missing))
        {
            yield return "the diagnostic was not deterministic";
        }
    }

    private static string Semantic(JsonObject result) => SemanticResult.Canonicalize(SemanticResult.Project(result));

    private static JsonObject Fixture(string directory, string name) =>
        (JsonObject)JsonReader.Parse(File.ReadAllBytes(Path.Combine(directory, name)));

    private static JsonObject Json(string text) => (JsonObject)JsonReader.Parse(text);
}
