using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Revisions;
using Packvium.Serialization;

namespace Packvium.Cli;

/// <summary>
/// <c>packvium revisions</c>: a test-only subcommand that runs plan-revision operations and request
/// admission in a batch. stdin is a JSON list of cases;
/// stdout is one canonical JSON line per case, in order: <c>{"error", "json", "message"}</c>, and
/// for a refused <c>admit</c> also its <c>reason</c> and <c>field</c>. A refusal is an outcome,
/// not a failure, so the probe exits 0 unless it cannot run at all.
/// </summary>
internal static class RevisionsCommand
{
    public static int Run()
    {
        using var stdout = Console.OpenStandardOutput();
        foreach (var entry in ((JsonArray)JsonReader.Parse(Program.ReadStdin())).Items)
        {
            // LF-separated: the canonical form escapes every LF inside a string.
            stdout.Write(Program.Utf8.GetBytes(CanonicalJsonWriter.Write(Outcome((JsonObject)entry)) + "\n"));
        }
        return 0;
    }

    /// <summary>
    /// One case's outcome: the canonical document, issue list or digest; or the code and message
    /// it was refused with. <c>admit</c> packs the request through the ordinary JSON entry point.
    /// </summary>
    internal static JsonObject Outcome(JsonObject @case)
    {
        try
        {
            return Answer(JsonNull.Instance, JsonNull.Instance, new JsonString(Answer(@case)));
        }
        catch (PlanRevisionException refusal)
        {
            return Answer(new JsonString(refusal.Code), new JsonString(refusal.Message), JsonNull.Instance);
        }
        catch (InvalidRequestException refusal) when (Op(@case) == "admit")
        {
            return Answer(new JsonString(refusal.Code), new JsonString(refusal.Message), JsonNull.Instance,
                ("reason", new JsonString(refusal.Reason)), ("field", new JsonString(refusal.Field)));
        }
        catch (UnsupportedFeatureException refusal) when (Op(@case) == "admit")
        {
            return Answer(new JsonString(UnsupportedFeatureException.Code), new JsonString(refusal.Message), JsonNull.Instance);
        }
#pragma warning disable CA1031 // Any other refusal is reported as one, so a single case cannot end the batch.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return Answer(new JsonString("other"), new JsonString(error.Message), JsonNull.Instance);
        }
    }

    private static string Op(JsonObject @case) => ((JsonString)@case.Require("op")).Value;

    private static string Answer(JsonObject @case) => Op(@case) switch
    {
        "root" => PlanRevision.CanonicalJson(PlanRevision.Root(@case.Require("request"))),
        "derive" => PlanRevision.CanonicalJson(PlanRevision.Derive(@case.Require("parent"), @case.Require("artifact"),
            @case.Require("events"))),
        "apply" => PlanRevision.CanonicalJson(PlanRevision.ApplyEvents(@case.Require("request"), @case.Require("events"))),
        "verify" => PlanRevision.CanonicalJson(new JsonArray([
            .. PlanRevision.VerifyChain(@case.Require("revisions"), @case.Get("artifacts")).Select(issue => (JsonValue)new JsonObject([
                new KeyValuePair<string, JsonValue>("code", new JsonString(issue.Code)),
                new KeyValuePair<string, JsonValue>("revision", new JsonInteger(issue.Revision)),
                new KeyValuePair<string, JsonValue>("detail", new JsonString(issue.Detail)),
            ])),
        ])),
        "digest" => PlanRevision.Digest(@case.Require("document")),
        "admit" => Admitted(@case.Require("request")),
        var op => throw new ArgumentException($"unknown op {op}"),
    };

    private static string Admitted(JsonValue request)
    {
        JsonApi.PackFromJson(request);
        return "admitted";
    }

    private static JsonObject Answer(JsonValue error, JsonValue message, JsonValue json,
        params (string Key, JsonValue Value)[] extra) => new(
    [
        new KeyValuePair<string, JsonValue>("error", error),
        new KeyValuePair<string, JsonValue>("message", message),
        new KeyValuePair<string, JsonValue>("json", json),
        .. extra.Select(member => new KeyValuePair<string, JsonValue>(member.Key, member.Value)),
    ]);
}
