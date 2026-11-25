using System;
using System.Collections.Generic;
using System.Linq;

namespace Packvium.Examples;

/// <summary>
/// The examples print lists, tuples and booleans in the notation shared by every Packvium
/// language (<c>['a', 'b']</c>, <c>(0, 1)</c>, <c>True</c>), and lay out sections and prose
/// the same way, so the output of one example reads the same in every language.
/// </summary>
internal static class Python
{
    private static readonly string Rule = new('=', 78);

    /// <summary>A blank line, then the title between two rules of 78 <c>=</c>.</summary>
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(Rule);
        Console.WriteLine(title);
        Console.WriteLine(Rule);
    }

    /// <summary>Each line indented by two spaces; an empty line stays empty.</summary>
    public static void Say(params string[] lines)
    {
        foreach (var line in lines)
        {
            Console.WriteLine(line.Length == 0 ? string.Empty : $"  {line}");
        }
    }

    /// <summary><c>['a', 'b']</c></summary>
    public static string List(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(Quote))}]";

    /// <summary><c>(0, 1, 2)</c></summary>
    public static string Tuple(IEnumerable<long> values) => $"({string.Join(", ", values)})";

    /// <summary><c>('a', 'b')</c>, and <c>('a',)</c> for one member.</summary>
    public static string StringTuple(IReadOnlyCollection<string> values) =>
        values.Count == 1 ? $"({Quote(values.First())},)" : $"({string.Join(", ", values.Select(Quote))})";

    /// <summary><c>[0, 1, 2]</c></summary>
    public static string IntegerList(IEnumerable<long> values) => $"[{string.Join(", ", values)}]";

    public static string Quote(string value) => $"'{value}'";
}
