using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Cli;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Conformance;

/// <summary>
/// The operational-artifact run of the tag (<c>conformance/artifacts/run.py</c>), in process: the
/// tag's own corpus cases and edge cases, each answered by the same code the CLI's batch probe
/// runs. Every export must be the Python reference's byte for byte, or the case refused with the
/// reference's error code; a case the tag only requires a refusal for must be refused.
/// <c>conformance/artifact_reference.py</c> wrote the cases and reference outcomes from the tag's
/// own <c>packvium.artifacts</c> and refused any reference JSON the tag's schema or consumer does
/// not accept, so a byte-identical JSON export here is schema-valid and consumable too.
/// </summary>
public sealed class ArtifactConformanceTests(ITestOutputHelper output)
{
    private static readonly string[] Exports = ["json", "csv", "html"];

    [Fact]
    public void EveryArtifactCaseGivesTheReferenceOutcome()
    {
        var corpus = ConformanceTests.CorpusDirectory();
        var cases = Path.Combine(corpus, "artifacts", "cases.json");
        if (!File.Exists(cases))
        {
            Assert.Skip(ConformanceTests.CorpusAbsent(cases));
        }
        var records = ((JsonArray)JsonReader.Parse(File.ReadAllBytes(cases))).Items.Cast<JsonObject>().ToList();
        var failures = records.Select(record => Failure(record, corpus)).OfType<string>().ToList();
        var edges = records.Count(IsEdge);
        output.WriteLine($"operational artifacts: {records.Count - failures.Count} of {records.Count} cases identical to the reference "
                         + $"({records.Count - edges} corpus results, {edges} edge cases, 3 exports each)");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>An edge case is written by the run itself, into <c>artifacts/edge</c>; a corpus case reads the tag's own files.</summary>
    private static bool IsEdge(JsonObject record) =>
        ((JsonString)(record.Get("artifact_path") ?? record.Require("request_path"))).Value
        .StartsWith("artifacts/", StringComparison.Ordinal);

    private static string? Failure(JsonObject record, string corpus)
    {
        var name = ((JsonString)record.Require("name")).Value;
        var reference = (JsonObject)record.Require("reference");
        var produced = ArtifactsCommand.Outcome(record, corpus);
        var error = produced.Require("error");
        if (record.Get("expected_refusal") is JsonString { Value: "*" })
        {
            return error is JsonNull ? $"{name}: accepted what every engine must refuse" : null;
        }
        if (error != reference.Require("error"))
        {
            return $"{name}: error {Describe(error)}, python {Describe(reference.Require("error"))}";
        }
        var differing = Exports.Where(export => produced.Require(export) != reference.Require(export)).ToList();
        return differing.Count == 0 ? null : $"{name}: {string.Join(", ", differing)} differ from python";
    }

    private static string Describe(JsonValue error) => error is JsonString code ? code.Value : "none";
}
