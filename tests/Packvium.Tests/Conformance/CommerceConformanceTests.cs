using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Packvium.Commerce;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Conformance;

/// <summary>
/// The tag's commerce corpus, in process: an answered fixture must match its golden document
/// byte for byte in canonical form, a malformed one must be refused as an input error.
/// The suite repository also runs the same corpus through the CLI and the tag's
/// own harness.
/// </summary>
public sealed class CommerceConformanceTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, Func<JsonValue, JsonValue, JsonObject>> Operations =
        new(StringComparer.Ordinal)
        {
            ["quote"] = CommerceApi.Quote,
            ["evaluate_policy"] = CommerceApi.EvaluatePolicy,
            ["catalog_version_info"] = CommerceApi.CatalogVersionInfo,
        };

    [Fact]
    public void EveryCommerceFixtureMatchesTheTag()
    {
        var directory = Path.Combine(ConformanceTests.CorpusDirectory(), "conformance", "commerce");
        if (!Directory.Exists(directory))
        {
            Assert.Skip(ConformanceTests.CorpusAbsent(directory));
        }
        var failures = new List<string>();
        var fixtures = Directory.GetFiles(Path.Combine(directory, "fixtures"), "*.json").Order(StringComparer.Ordinal).ToList();
        foreach (var path in fixtures)
        {
            if (Check(directory, path) is { } failure)
            {
                failures.Add($"{Path.GetFileNameWithoutExtension(path)}: {failure}");
            }
        }
        output.WriteLine($"commerce fixtures passed {fixtures.Count - failures.Count} of {fixtures.Count}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string? Check(string directory, string path)
    {
        var fixture = (JsonObject)JsonReader.Parse(File.ReadAllBytes(path));
        var operation = Operations[((JsonString)fixture.Require("operation")).Value];
        var expectsError = fixture.Get("expects") is JsonString { Value: "input_error" };
        try
        {
            var actual = CommerceApi.CanonicalJson(operation(fixture.Require("document"), fixture.Require("request")));
            if (expectsError)
            {
                return "answered a malformed request instead of refusing it";
            }
            var golden = Path.Combine(directory, "golden", Path.GetFileName(path));
            var expected = PythonJson.Dumps(JsonReader.Parse(File.ReadAllBytes(golden)), sortKeys: true);
            return actual == expected ? null : $"expected {expected}\n    observed {actual}";
        }
        catch (CommerceInputException error)
        {
            return expectsError ? null : $"refused a well-formed request: {error.Message}";
        }
    }
}
