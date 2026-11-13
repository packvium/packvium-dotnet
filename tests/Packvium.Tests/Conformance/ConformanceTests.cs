using System;
using System.IO;
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
        return directory is null ? string.Empty : Path.Combine(directory.FullName, ".corpus", "v1.1.0");
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
