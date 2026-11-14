using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Tests.Conformance;

public enum FixtureOutcome
{
    Passed,
    Failed,
    /// <summary>Uses behaviour this port has not reached yet; neither a pass nor a failure.</summary>
    Pending,
    /// <summary>
    /// No golden at this tag (deadline-bound, so the result depends on host speed); it must
    /// still return a result the port's own validator accepts.
    /// </summary>
    Validated,
}

/// <param name="RankedAlternatives">The live result carried a non-empty <c>alternatives</c> list that passed its invariants.</param>
public sealed record FixtureResult(string Name, FixtureOutcome Outcome, string Detail, bool RankedAlternatives = false);

public sealed record CorpusReport(string Corpus, ImmutableArray<FixtureResult> Results)
{
    public int Count(FixtureOutcome outcome) => Results.Count(result => result.Outcome == outcome);

    /// <summary>
    /// Counted rather than assumed, as the tag's runner counts it: the goldens store the
    /// projection and carry no <c>alternatives</c>, so only the live results can be checked.
    /// </summary>
    public int RankedAlternativeLists => Results.Count(result => result.RankedAlternatives);

    public string Summary()
    {
        var pending = Results.Where(result => result.Outcome == FixtureOutcome.Pending)
            .GroupBy(result => result.Detail, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"  pending on {group.Key}: {group.Count()}");
        var failures = Results.Where(result => result.Outcome == FixtureOutcome.Failed)
            .Select(result => $"  FAILED {result.Name}: {result.Detail}");
        return string.Join('\n', new[]
        {
            $"corpus {Corpus}",
            $"passed {Count(FixtureOutcome.Passed)}, failed {Count(FixtureOutcome.Failed)}, "
            + $"pending {Count(FixtureOutcome.Pending)}, validated without golden {Count(FixtureOutcome.Validated)}",
            $"alternatives invariants passed: {RankedAlternativeLists} non-empty ranked list(s) checked",
        }.Concat(pending).Concat(failures));
    }
}

/// <summary>
/// Runs every fixture of one tag's corpus through the port and compares canonical JSON with that
/// tag's golden byte for byte. A fixture that reaches a <see cref="NotYetPortedException"/> is
/// pending; any other exception on a fixture the reference answered is a failure, because the
/// reference accepted that request.
/// </summary>
public static class CorpusRunner
{
    public static CorpusReport Run(string corpus)
    {
        var fixtures = Path.Combine(corpus, "conformance", "fixtures");
        var golden = Path.Combine(corpus, "conformance", "golden");
        var results = Directory.GetFiles(fixtures, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => RunOne(path, Path.Combine(golden, Path.GetFileName(path))))
            .Concat(RunRejections(Path.Combine(corpus, "conformance", "rejections")))
            .ToImmutableArray();
        return new CorpusReport(corpus, results);
    }

    /// <summary>Requests whose only conforming outcome is a refusal whose message contains the declared text.</summary>
    private static List<FixtureResult> RunRejections(string directory)
    {
        var manifest = (JsonObject)JsonReader.Parse(File.ReadAllBytes(Path.Combine(directory, "expectations.json")));
        return ((JsonObject)manifest.Require("cases")).Members
            .Select(member => RunRejection(directory, member.Key, ((JsonString)member.Value).Value))
            .ToList();
    }

    private static FixtureResult RunRejection(string directory, string name, string expected)
    {
        var label = $"rejections/{name}";
        try
        {
            JsonApi.PackFromJson(JsonReader.Parse(File.ReadAllBytes(Path.Combine(directory, name))));
            return new FixtureResult(label, FixtureOutcome.Failed, "returned a result instead of rejecting");
        }
        catch (NotYetPortedException pending)
        {
            return new FixtureResult(label, FixtureOutcome.Pending, pending.Feature);
        }
#pragma warning disable CA1031 // The refusal is the expected outcome; its message is what is checked.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)
                ? new FixtureResult(label, FixtureOutcome.Passed, string.Empty)
                : new FixtureResult(label, FixtureOutcome.Failed,
                    $"expected '{expected}', got {error.GetType().Name}: {error.Message}");
        }
    }

    private static FixtureResult RunOne(string fixture, string goldenPath)
    {
        var name = Path.GetFileName(fixture);
        try
        {
            var request = (JsonObject)JsonReader.Parse(File.ReadAllBytes(fixture));
            var result = JsonApi.PackFromJson(request);
            var problems = AlternativesInvariants.Problems(request, result);
            if (problems.Count > 0)
            {
                return new FixtureResult(name, FixtureOutcome.Failed, string.Join("; ", problems));
            }
            var ranked = result.Get("alternatives") is JsonArray { Items.IsEmpty: false };
            if (!File.Exists(goldenPath))
            {
                var status = ((JsonString)result.Require("status")).Value;
                return status == "invalid_result"
                    ? new FixtureResult(name, FixtureOutcome.Failed, "the port's own validator refused its result")
                    : new FixtureResult(name, FixtureOutcome.Validated, status, ranked);
            }
            var actual = SemanticResult.Canonicalize(SemanticResult.Project(result)) + "\n";
            var expected = File.ReadAllText(goldenPath);
            return actual == expected
                ? new FixtureResult(name, FixtureOutcome.Passed, string.Empty, ranked)
                : new FixtureResult(name, FixtureOutcome.Failed, FirstDifference(expected, actual));
        }
        catch (NotYetPortedException pending)
        {
            return new FixtureResult(name, FixtureOutcome.Pending, pending.Feature);
        }
#pragma warning disable CA1031 // Every other exception is recorded as this fixture's failure, not lost.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return new FixtureResult(name, FixtureOutcome.Failed, $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        var index = 0;
        while (index < expected.Length && index < actual.Length && expected[index] == actual[index])
        {
            index++;
        }
        static string Window(string text, int at) => text.Substring(Math.Max(0, at - 40), Math.Min(80, text.Length - Math.Max(0, at - 40)));
        return $"differs at {index}: expected …{Window(expected, index)}… got …{Window(actual, index)}…";
    }
}
