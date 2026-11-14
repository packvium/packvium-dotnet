using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Packvium.Tests.Conformance;

public sealed class ConformanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Why a corpus test skips. The corpus is the suite repository's: a published copy of this
    /// package carries the examples' expected outputs but not the fixtures, so the conformance
    /// half of the suite runs there.
    /// </summary>
    internal static string CorpusAbsent(string looked) =>
        $"the conformance corpus is not part of this repository; full conformance runs in the Packvium suite repository (looked in '{looked}')";

    /// <summary>
    /// The milestone corpus, exported from the suite repository into <c>.corpus/</c>; <c>PACKVIUM_CORPUS</c>
    /// points elsewhere.
    /// </summary>
    internal static string CorpusDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("PACKVIUM_CORPUS");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Packvium.slnx")))
        {
            directory = directory.Parent;
        }
        return directory is null ? string.Empty : Path.Combine(directory.FullName, ".corpus", "v1.2.0");
    }

    [Fact]
    public void NoFixtureTheReferenceAnswersFails()
    {
        var corpus = CorpusDirectory();
        if (!Directory.Exists(Path.Combine(corpus, "conformance", "fixtures")))
        {
            Assert.Skip(CorpusAbsent(corpus));
        }
        var report = CorpusRunner.Run(corpus);
        output.WriteLine(report.Summary());
        Assert.True(report.Count(FixtureOutcome.Failed) == 0, report.Summary());
    }

    /// <summary>
    /// The execution-plan run of the tag (<c>conformance/execution/run.py</c>): for every golden
    /// that is an object with a <c>status</c>, the plan's canonical JSON must be the Python
    /// reference's byte for byte. <c>conformance/execution_reference.py</c> wrote those bytes from
    /// the tag's own <c>packvium.execution</c> and refused any plan its schema does not accept,
    /// so a match is schema-valid too.
    /// </summary>
    [Fact]
    public void EveryGoldenGivesTheReferenceExecutionPlan()
    {
        var reference = Path.Combine(CorpusDirectory(), "execution");
        if (!Directory.Exists(reference))
        {
            Assert.Skip(CorpusAbsent(reference));
        }
        var golden = Path.Combine(CorpusDirectory(), "conformance", "golden");
        var empty = new Packvium.Serialization.JsonObject([]);
        var mismatches = new System.Collections.Generic.List<string>();
        var (goldens, covered) = (0, 0);
        foreach (var path in Directory.GetFiles(golden, "*.json").Order(StringComparer.Ordinal))
        {
            goldens++;
            if (Packvium.Serialization.JsonReader.Parse(File.ReadAllBytes(path)) is not Packvium.Serialization.JsonObject result
                || !result.Contains("status"))
            {
                continue;
            }
            covered++;
            var expected = Path.Combine(reference, Path.GetFileName(path));
            var actual = Packvium.Execution.ExecutionPlan.CanonicalJson(Packvium.Execution.ExecutionPlan.Build(empty, result));
            if (!File.Exists(expected) || actual != File.ReadAllText(expected))
            {
                mismatches.Add(Path.GetFileName(path));
            }
        }
        output.WriteLine($"execution plans: {covered - mismatches.Count} of {covered} byte-identical to the reference "
                         + $"({goldens} goldens, {Directory.GetFiles(reference, "*.json").Length} schema-valid reference plans)");
        Assert.True(covered > 0 && mismatches.Count == 0, string.Join(", ", mismatches));
    }

    /// <summary>
    /// The harness checked against itself: the reference engine's own raw results, projected and
    /// canonicalized here, must reproduce every golden. Proves the comparison before the packer
    /// exists, so a later mismatch is the port's and not the harness's.
    /// </summary>
    [Fact]
    public void HarnessReproducesEveryGoldenFromTheReferenceOutput()
    {
        var raw = Path.Combine(CorpusDirectory(), "raw");
        if (!Directory.Exists(raw))
        {
            Assert.Skip(CorpusAbsent(raw));
        }
        var golden = Path.Combine(CorpusDirectory(), "conformance", "golden");
        var mismatches = new System.Collections.Generic.List<string>();
        var compared = 0;
        foreach (var path in Directory.GetFiles(raw, "*.json"))
        {
            var result = (Packvium.Serialization.JsonObject)Packvium.Serialization.JsonReader.Parse(File.ReadAllBytes(path));
            var actual = SemanticResult.Canonicalize(SemanticResult.Project(result)) + "\n";
            compared++;
            if (actual != File.ReadAllText(Path.Combine(golden, Path.GetFileName(path))))
            {
                mismatches.Add(Path.GetFileName(path));
            }
        }
        output.WriteLine($"harness compared {compared} reference results, {mismatches.Count} differ from golden");
        Assert.True(mismatches.Count == 0, string.Join(", ", mismatches));
    }
}
