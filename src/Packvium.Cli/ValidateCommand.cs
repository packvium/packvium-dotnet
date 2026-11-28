using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Packvium.Serialization;
using Packvium.Validation;

namespace Packvium.Cli;

/// <summary>
/// <c>packvium validate</c>: a test-only subcommand that runs the result validator in a batch.
/// stdin is a JSON list of <c>{"request": "&lt;json text&gt;", "result": "&lt;json text&gt;"}</c>;
/// stdout is one ASCII-only JSON line per case, in order: <c>{"issues": [{"code", "detail"}, ...]}</c>,
/// or <c>{"refusal": {"kind", "message"}}</c>. A refusal is an outcome, not a failure, so the
/// probe exits 0 unless it cannot run at all.
/// </summary>
internal static class ValidateCommand
{
    public static int Run()
    {
        using var stdout = Console.OpenStandardOutput();
        foreach (var entry in ((JsonArray)JsonReader.Parse(Program.ReadStdin())).Items)
        {
            stdout.Write(Encoding.ASCII.GetBytes(AsciiJson(Outcome(entry)) + "\n"));
        }
        return 0;
    }

    /// <summary>One batch entry's outcome; an entry without its two texts is refused like any other case.</summary>
    internal static JsonObject Outcome(JsonValue entry) =>
        entry is JsonObject { } @case && @case.Get("request") is JsonString request && @case.Get("result") is JsonString result
            ? Outcome(request.Value, result.Value)
            : Refusal(MalformedResultException.Shape, "a case must hold the request and result as JSON text");

    /// <summary>
    /// One case's outcome. Each document is parsed as the reference parses it, request first; text
    /// that is not JSON, and any failure the validator does not name as a contract break, is a
    /// <c>shape</c> refusal, as every failure but <c>MalformedResult</c> is in the reference.
    /// </summary>
    internal static JsonObject Outcome(string requestText, string resultText)
    {
        try
        {
            var request = JsonReader.Parse(requestText);
            var result = JsonReader.Parse(resultText);
            var issues = ResultValidation.Validate(request, result).Select(issue => (JsonValue)new JsonObject([
                Member("code", new JsonString(issue.Code)),
                Member("detail", new JsonString(issue.Detail)),
            ]));
            return new JsonObject([Member("issues", new JsonArray([.. issues]))]);
        }
        catch (MalformedResultException refusal)
        {
            return Refusal(refusal.Kind, refusal.Message);
        }
#pragma warning disable CA1031 // Any other failure is the reference's shape refusal, so a single case cannot end the batch.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return Refusal(MalformedResultException.Shape, $"{failure.GetType().Name}: {failure.Message}");
        }
    }

    private static JsonObject Refusal(string kind, string message) => new([
        Member("refusal", new JsonObject([Member("kind", new JsonString(kind)), Member("message", new JsonString(message))])),
    ]);

    private static KeyValuePair<string, JsonValue> Member(string key, JsonValue value) => new(key, value);

    /// <summary>
    /// The outcome as JSON with every UTF-16 unit outside printable ASCII escaped as <c>\uXXXX</c>,
    /// a lone surrogate included, so no line separator a reader splits on can appear inside a line.
    /// </summary>
    private static string AsciiJson(JsonValue value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonValue value)
    {
        switch (value)
        {
            case JsonString text:
                WriteString(builder, text.Value);
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Items.Length; index++)
                {
                    builder.Append(index == 0 ? string.Empty : ", ");
                    Write(builder, array.Items[index]);
                }
                builder.Append(']');
                break;
            case JsonObject record:
                builder.Append('{');
                var first = true;
                foreach (var member in record.Members)
                {
                    builder.Append(first ? string.Empty : ", ");
                    first = false;
                    WriteString(builder, member.Key);
                    builder.Append(": ");
                    Write(builder, member.Value);
                }
                builder.Append('}');
                break;
            default:
                throw new ArgumentException($"an outcome holds only strings, lists and objects, not {value.GetType().Name}", nameof(value));
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var unit in text)
        {
            builder.Append(unit switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                < ' ' or > '~' => $"\\u{(int)unit:x4}",
                _ => unit.ToString(),
            });
        }
        builder.Append('"');
    }
}
