using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Packvium.Serialization;
using Packvium.Tests.Conformance;
using Packvium.Validation;
using Xunit;

namespace Packvium.Tests.Validation;

/// <summary>
/// The validator agrees with the reference on every pair of the shared result-validation corpus
/// (<c>conformance/result_validation/corpus.json</c>): the subset of the cross-language corpus
/// that reaches every arc, issue code and refusal kind the full corpus reaches in the reference,
/// each pair with the reference's outcome. Each document is parsed with <see cref="JsonReader"/>
/// and judged by <see cref="ResultValidation.Validate"/>, which may refuse only with
/// <see cref="MalformedResultException"/>; agreement is the conformance runner's rule: the
/// same issues, code and detail, in order; or, where the reference raised <c>MalformedResult</c>, a
/// <c>contract</c> refusal with the same message (up to a quoted Python exception); or, where it
/// failed another way, any refusal.
/// </summary>
public sealed class ResultValidationCorpusTests
{
    /// <summary>Reference refusals that end by quoting a Python exception compare up to the quote.</summary>
    private static readonly Regex QuotesPythonError =
        new("^(placement is missing tick coordinates: |.*: lattice_summary is missing a canonical field: )", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryPairGetsTheReferenceVerdict()
    {
        var disagreements = Corpus()
            .Select((@case, index) => (index, reason: Disagreement(Expected(@case), Outcome(Text(@case, "request"), Text(@case, "result")))))
            .Where(found => found.reason is not null)
            .Select(found => $"case {found.index}: {found.reason}")
            .ToList();
        Assert.Empty(disagreements);
    }

    [Fact]
    public void TheCorpusReachesEveryIssueCodeAndRefusalKind()
    {
        var cases = Corpus();
        var codes = cases.SelectMany(@case => Expected(@case).Get("issues") is JsonArray issues ? issues.Items : [])
            .Select(issue => Text((JsonObject)issue, "code")).ToHashSet();
        var refusals = cases.Select(@case => Expected(@case).Get("refusal")).OfType<JsonObject>()
            .Select(refusal => Text(refusal, "kind")).Distinct().Order().ToList();
        Assert.True(codes.Count > 70, "the corpus quietly lost issue codes");
        Assert.Equal(["contract", "shape"], refusals);
    }

    /// <summary>The reference's outcome shape for one pair: its issues, or the refusal it was given.</summary>
    private static JsonObject Outcome(string requestText, string resultText)
    {
        try
        {
            var issues = ResultValidation.Validate(JsonReader.Parse(requestText), JsonReader.Parse(resultText));
            return new JsonObject([Member("issues", new JsonArray([.. issues.Select(issue => (JsonValue)new JsonObject([
                Member("code", new JsonString(issue.Code)),
                Member("detail", new JsonString(issue.Detail)),
            ]))]))]);
        }
        catch (MalformedResultException refusal)
        {
            return Refusal(refusal.Kind, refusal.Message);
        }
        catch (JsonException refusal)
        {
            return Refusal(MalformedResultException.Shape, $"not JSON: {refusal.Message}");
        }
    }

    private static JsonObject Refusal(string kind, string message) =>
        new([Member("refusal", new JsonObject([Member("kind", new JsonString(kind)), Member("message", new JsonString(message))]))]);

    private static KeyValuePair<string, JsonValue> Member(string key, JsonValue value) => new(key, value);

    /// <summary>Why <paramref name="actual"/> does not count as the reference's answer, or null when it does.</summary>
    private static string? Disagreement(JsonObject expected, JsonObject actual)
    {
        if (expected.Get("issues") is { } issues)
        {
            if (actual.Get("issues") is { } found && CanonicalJsonWriter.Write(found) == CanonicalJsonWriter.Write(issues))
            {
                return null;
            }
            return actual.Get("refusal") is JsonObject refused
                ? $"refused ({Text(refused, "message")}) where the reference found issues"
                : $"a different issue list: {PythonJson.Dumps(actual, sortKeys: false, spaced: true)}";
        }
        var reference = (JsonObject)expected.Require("refusal");
        if (actual.Get("refusal") is not JsonObject refusal)
        {
            return $"answered where the reference refused ({Text(reference, "message")})";
        }
        return Text(reference, "kind") == "contract" && !SameRefusal(reference, refusal)
            ? $"refused with {Text(refusal, "kind")}: {Text(refusal, "message")} where the reference raised MalformedResult({Text(reference, "message")})"
            : null;
    }

    private static bool SameRefusal(JsonObject expected, JsonObject actual)
    {
        if (Text(actual, "kind") != Text(expected, "kind"))
        {
            return false;
        }
        var quoted = QuotesPythonError.Match(Text(expected, "message"));
        return quoted.Success
            ? Text(actual, "message").StartsWith(quoted.Groups[1].Value, System.StringComparison.Ordinal)
            : Text(actual, "message") == Text(expected, "message");
    }

    private static List<JsonObject> Corpus()
    {
        var path = Path.Combine(ConformanceTests.CorpusDirectory(), "conformance", "result_validation", "corpus.json");
        if (!File.Exists(path))
        {
            Assert.Skip(ConformanceTests.CorpusAbsent(path));
        }
        var corpus = (JsonObject)JsonReader.Parse(File.ReadAllBytes(path));
        Assert.Equal("packvium-result-validation-corpus/v1", Text(corpus, "format"));
        return [.. ((JsonArray)corpus.Require("cases")).Items.Cast<JsonObject>()];
    }

    private static JsonObject Expected(JsonObject @case) => (JsonObject)@case.Require("expected");

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;
}
