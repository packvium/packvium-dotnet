using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Cli;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Conformance;

/// <summary>
/// The plan-revision and request-error runs of the tag (<c>conformance/revisions/run.py</c> and
/// <c>conformance/request_errors/run.py</c>), in process: the tag's own cases, each answered by
/// the same code the CLI's <c>revisions</c> batch probe runs. A revision case must give Python's
/// whole outcome -- bytes, digest, audit or refusal code and message; a malformed request
/// Python's error, reason, field and message; a valid request must be admitted.
/// <c>conformance/revision_reference.py</c> wrote the cases and outcomes from the tag's Python and
/// refused any reference revision the tag's schema does not accept, so a byte-identical revision
/// here is schema-valid too.
/// </summary>
public sealed class RevisionConformanceTests(ITestOutputHelper output)
{
    private static readonly string[] RequestErrorKeys = ["error", "reason", "field", "message"];

    [Fact]
    public void EveryRevisionAndRequestErrorCaseGivesTheReferenceOutcome()
    {
        var cases = Path.Combine(ConformanceTests.CorpusDirectory(), "revisions", "cases.json");
        if (!File.Exists(cases))
        {
            Assert.Skip(ConformanceTests.CorpusAbsent(cases));
        }
        var records = ((JsonArray)JsonReader.Parse(File.ReadAllBytes(cases))).Items.Cast<JsonObject>().ToList();
        var failures = records.Select(Failure).OfType<string>().ToList();
        foreach (var suite in (string[])["revisions", "request_errors"])
        {
            var inSuite = records.Where(record => Text(record, "suite") == suite).ToList();
            var failed = inSuite.Count(record => Failure(record) is not null);
            output.WriteLine($"{suite}: {inSuite.Count - failed} of {inSuite.Count} cases give the reference outcome");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string? Failure(JsonObject record)
    {
        var name = Text(record, "name");
        var produced = RevisionsCommand.Outcome((JsonObject)record.Require("case"));
        switch (Text(record, "compare"))
        {
            case "admit":
                return produced.Require("error") is JsonNull ? null : $"{name}: refused: {Describe(produced.Get("message"))}";
            case "keys":
                var reference = (JsonObject)record.Require("reference");
                // Each engine words an unsupported field its own way, so only the code is compared.
                if (Describe(produced.Get("error")) == UnsupportedFeatureException.Code)
                {
                    produced = new JsonObject([new KeyValuePair<string, JsonValue>("error", produced.Require("error"))]);
                }
                var differing = RequestErrorKeys.Where(key =>
                    CanonicalJsonWriter.Write(produced.Get(key) ?? JsonNull.Instance)
                    != CanonicalJsonWriter.Write(reference.Get(key) ?? JsonNull.Instance)).ToList();
                return differing.Count == 0
                    ? null
                    : $"{name}: {string.Join(", ", differing)} differ: {CanonicalJsonWriter.Write(produced)} against {CanonicalJsonWriter.Write(reference)}";
            default:
                var expected = CanonicalJsonWriter.Write(record.Require("reference"));
                var actual = CanonicalJsonWriter.Write(produced);
                return expected == actual ? null : $"{name}: {actual} against python {expected}";
        }
    }

    private static string Text(JsonObject record, string key) => ((JsonString)record.Require(key)).Value;

    private static string Describe(JsonValue? message) => message is JsonString text ? text.Value : "no message";
}
