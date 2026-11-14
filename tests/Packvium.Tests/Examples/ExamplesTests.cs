using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Packvium.Examples;
using Xunit;

namespace Packvium.Tests.Examples;

[CollectionDefinition(nameof(ConsoleExamples), DisableParallelization = true)]
public sealed class ConsoleExamples;

/// <summary>
/// Every shipped example runs, prints something, is deterministic, and prints what the
/// Python example of the same tag prints. The examples write to the console, so they run
/// alone, outside every parallel collection.
/// </summary>
[Collection(nameof(ConsoleExamples))]
public sealed partial class ExamplesTests
{
    /// <summary>
    /// The only lines allowed to differ from the Python output, each because it is about the
    /// language rather than the packing: a file name, a command line, Python's spelling of
    /// null, and a refusal from Python's internal staging guard, which has no counterpart on
    /// the 1.0.0 surface of the other engines. A null replacement means the Python line has
    /// no .NET counterpart.
    /// </summary>
    private static readonly Dictionary<string, (string Python, string? Dotnet)[]> LanguageLines = new(StringComparer.Ordinal)
    {
        ["basic"] =
        [
            ("the full result as a plain dict is what serialization.py explores:",
             "the full result as JSON is what Serialization.cs explores:"),
        ],
        ["serialization"] =
        [
            ("  what one looks like, from an engine that is not: unsupported_feature: the Python engine does not yet implement item.shape_type=convex_hull; the request was rej",
             null),
            ("the CLI takes exactly the document above:  echo '...' | python3 -m packvium",
             "the CLI takes exactly the document above:  echo '...' | Packvium.Cli"),
        ],
        ["commerce"] =
        [
            ("   nothing matched, so the shipment is allowed with no citation: None",
             "   nothing matched, so the shipment is allowed with no citation: null"),
        ],
        // The reference's section 4 speaks for its four engines and names its JavaScript
        // example; this port's text speaks for this engine instead.
        ["execution"] =
        [
            ("4. One plan, four engines, the same bytes",
             "4. One plan, every engine, the same bytes"),
            ("  Hand the same *result* to all four adapters and they emit the same bytes. That",
             "  Hand the same *result* to any Packvium adapter, this one included, and it emits"),
            ("  is stricter than the packing contract, and it can be: a plan is derived from a",
             "  the same bytes. That is stricter than the packing contract, and it can be: a"),
            ("  result, so there is nothing left to differ about.",
             "  plan is derived from a result, so there is nothing left to differ about."),
            ("  It does not follow that four engines packing the same *request* agree. Python",
             "  It does not follow that every engine packing the same *request* agrees. This"),
            ("  and PHP are held to identical placements and do produce this exact form; Rust",
             "  engine, like Python and PHP, is held to identical placements and produces this"),
            ("  and JavaScript are held to a valid answer at or above the objective floor, and",
             "  exact form; the Rust and JavaScript engines are held to a valid answer at or"),
            ("  `examples/execution.mjs` prints 1280 bytes here rather than 1877 for that",
             "  above the objective floor, so their plan for this request can be a different"),
            ("  reason. Whose packing is better is a question the plan never answers.",
             "  length. Whose packing is better is a question the plan never answers."),
        ],
        ["intelligence"] =
        [
            ("  minimum_confidence=0.9 -> None",
             "  minimum_confidence=0.9 -> null"),
            ("  met. `None` is the whole answer: there is no low-confidence proposal to weigh.",
             "  met. `null` is the whole answer: there is no low-confidence proposal to weigh."),
        ],
    };

    public static TheoryData<string> Names => [.. Program.Examples.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void PrintsWhatTheReferenceExamplePrints(string name) =>
        Assert.Equal(Expected(name, File.ReadAllText(VendoredOutput(name))), Mask(Run(name)));

    /// <summary>
    /// The expected outputs ship with the tests, so the comparison runs wherever the package's
    /// source does. Where the release's corpus is present too, the copies must not have drifted.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void TheVendoredOutputIsTheReleasesOwn(string name)
    {
        var corpusCopy = Path.Combine(Tests.Conformance.ConformanceTests.CorpusDirectory(), "conformance", "examples",
            "expected", "python", $"{name}.out");
        if (!File.Exists(corpusCopy))
        {
            Assert.Skip(Tests.Conformance.ConformanceTests.CorpusAbsent(corpusCopy));
        }
        Assert.Equal(File.ReadAllText(corpusCopy), File.ReadAllText(VendoredOutput(name)));
    }

    /// <summary>The Python example's output at this release, copied from <c>conformance/examples/expected/python</c>.</summary>
    private static string VendoredOutput(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Examples", "expected", $"{name}.out");

    [Theory]
    [MemberData(nameof(Names))]
    public void PrintsSomethingAndTheSameThingTwice(string name)
    {
        var first = Run(name);
        Assert.False(string.IsNullOrWhiteSpace(first), $"{name} printed nothing");
        Assert.Equal(Mask(first), Mask(Run(name)));
    }

    [Fact]
    public void TheReadmeListsEveryExample()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        var missing = Program.Examples.Keys.Where(name => !readme.Contains($"examples/{Capitalised(name)}.cs", StringComparison.Ordinal));
        Assert.Empty(missing);
    }

    private static string Run(string name)
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Program.Examples[name]();
        }
        finally
        {
            Console.SetOut(original);
        }
        return captured.ToString().ReplaceLineEndings("\n");
    }

    private static string Expected(string name, string python)
    {
        var lines = python.ReplaceLineEndings("\n").Split('\n').ToList();
        foreach (var (pythonLine, dotnetLine) in LanguageLines.GetValueOrDefault(name) ?? [])
        {
            var index = lines.IndexOf(pythonLine);
            Assert.True(index >= 0, $"{name}: the declared Python line is not in the expected output: {pythonLine}");
            if (dotnetLine is null)
            {
                lines.RemoveAt(index);
            }
            else
            {
                lines[index] = dotnetLine;
            }
        }
        return Mask(string.Join('\n', lines));
    }

    /// <summary>Elapsed time is a fact about the machine, not the packing, so it is masked as the Python test masks it.</summary>
    private static string Mask(string output) => Timing().Replace(output, "$1$2<masked>$3");

    [GeneratedRegex("(\"(?:duration_ms|elapsed_ms)\":\\s*)\\d+|(\\bin )\\d+( ms\\b)")]
    private static partial Regex Timing();

    private static string Capitalised(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Packvium.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Packvium.slnx not found above the test binary");
    }
}
