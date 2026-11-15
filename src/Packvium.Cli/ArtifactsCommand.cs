using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Packvium.Artifacts;
using Packvium.Serialization;

namespace Packvium.Cli;

/// <summary>
/// <c>packvium artifacts</c>: the batch probe of the tag's <c>conformance/artifacts/run.py</c>.
/// stdin is a JSON list of cases; stdout is one canonical JSON line per case, in order:
/// <c>{"csv", "error", "html", "json"}</c>. A refusal is an outcome, not a failure, so the probe
/// exits 0 unless it cannot run at all.
/// </summary>
internal static class ArtifactsCommand
{
    public static int Run()
    {
        using var stdout = Console.OpenStandardOutput();
        foreach (var entry in ((JsonArray)JsonReader.Parse(Program.ReadStdin())).Items)
        {
            // LF-separated: the canonical form escapes every LF inside a string.
            stdout.Write(Program.Utf8.GetBytes(CanonicalJsonWriter.Write(Outcome((JsonObject)entry, null)) + "\n"));
        }
        return 0;
    }

    /// <summary>
    /// One case's outcome: the three exports, or the code it was refused with. Paths are read
    /// relative to <paramref name="root"/> when one is given.
    /// </summary>
    internal static JsonObject Outcome(JsonObject @case, string? root)
    {
        try
        {
            var artifact = Artifact(@case, root);
            return Answer(JsonNull.Instance, new JsonString(ArtifactExports.Json(artifact)),
                new JsonString(ArtifactExports.Csv(artifact)), new JsonString(ArtifactExports.WorkOrderHtml(artifact)));
        }
        catch (OperationalArtifactException refusal)
        {
            return Answer(new JsonString(refusal.Code), JsonNull.Instance, JsonNull.Instance, JsonNull.Instance);
        }
    }

    private static JsonValue Artifact(JsonObject @case, string? root)
    {
        if (@case.Require("kind") is JsonString { Value: "export" })
        {
            return OperationalArtifact.Parse(Read(@case, "artifact_path", root));
        }
        return OperationalArtifact.FromJson(Read(@case, "request_path", root), Read(@case, "result_path", root),
            LoadingOrders((JsonObject)@case.Require("loading_orders")));
    }

    /// <summary>A container index, written as a JSON object key, to its order.</summary>
    private static Dictionary<int, IReadOnlyList<int>> LoadingOrders(JsonObject orders) =>
        orders.Members.ToDictionary(
            member => int.Parse(member.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            member => (IReadOnlyList<int>)[.. ((JsonArray)member.Value).Items.Select(index => (int)((JsonInteger)index).Value)]);

    private static string Read(JsonObject @case, string field, string? root)
    {
        var path = ((JsonString)@case.Require(field)).Value;
        return File.ReadAllText(root is null ? path : Path.Combine(root, path), Program.Utf8);
    }

    private static JsonObject Answer(JsonValue error, JsonValue json, JsonValue csv, JsonValue html) => new(
    [
        new KeyValuePair<string, JsonValue>("error", error),
        new KeyValuePair<string, JsonValue>("json", json),
        new KeyValuePair<string, JsonValue>("csv", csv),
        new KeyValuePair<string, JsonValue>("html", html),
    ]);
}
