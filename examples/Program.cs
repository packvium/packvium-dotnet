using System;
using System.Collections.Generic;
using System.Linq;

namespace Packvium.Examples;

/// <summary>
/// Runs one example by name, or every example in turn:
///
///     dotnet run --project examples -- basic
///     dotnet run --project examples
/// </summary>
public static class Program
{
    /// <summary>Every example, in the order the README lists them.</summary>
    public static IReadOnlyDictionary<string, Action> Examples { get; } = new Dictionary<string, Action>(StringComparer.Ordinal)
    {
        ["basic"] = Basic.Run,
        ["objectives"] = Objectives.Run,
        ["constraints"] = Constraints.Run,
        ["units"] = Units.Run,
        ["serialization"] = Serialization.Run,
        ["shapes"] = Shapes.Run,
        ["nested"] = Nested.Run,
        ["extensions"] = Extensions.Run,
        ["commerce"] = Commerce.Run,
        ["execution"] = Execution.Run,
        ["intelligence"] = Intelligence.Run,
    };

    public static int Main(string[] args)
    {
        var chosen = args.Length == 0 ? Examples.Keys.ToList() : [.. args];
        var unknown = chosen.Where(name => !Examples.ContainsKey(name)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"unknown example(s): {string.Join(", ", unknown)}; expected one of {string.Join(", ", Examples.Keys)}");
            return 2;
        }
        foreach (var name in chosen)
        {
            Examples[name]();
        }
        return 0;
    }
}
