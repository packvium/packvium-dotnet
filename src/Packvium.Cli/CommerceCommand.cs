using System;
using System.Collections.Generic;
using Packvium.Commerce;
using Packvium.Serialization;

namespace Packvium.Cli;

/// <summary>
/// <c>packvium commerce</c>: one case object (<c>operation</c>, <c>document</c>, <c>request</c>)
/// on stdin, one canonical result document on stdout. An input error exits 1 with
/// <c>input error: ...</c> on stderr; a rejection is an answer and exits 0.
/// </summary>
internal static class CommerceCommand
{
    private static readonly Dictionary<string, Func<JsonValue, JsonValue, JsonObject>> Operations =
        new(StringComparer.Ordinal)
        {
            ["quote"] = CommerceApi.Quote,
            ["evaluate_policy"] = CommerceApi.EvaluatePolicy,
            ["catalog_version_info"] = CommerceApi.CatalogVersionInfo,
        };

    public static int Run()
    {
        var fields = (JsonObject)JsonReader.Parse(Program.ReadStdin());
        var name = ((JsonString)fields.Require("operation")).Value;
        if (!Operations.TryGetValue(name, out var operation))
        {
            Console.Error.WriteLine($"unknown operation '{name}'");
            return 2;
        }
        string encoded;
        try
        {
            encoded = CommerceApi.CanonicalJson(operation(fields.Require("document"), fields.Require("request")));
        }
        catch (CommerceInputException error)
        {
            Console.Error.WriteLine($"input error: {error.Message}");
            return 1;
        }
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Program.Utf8.GetBytes(encoded));
        return 0;
    }
}
