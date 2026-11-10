using System.Collections.Generic;
using System.Linq;

namespace Packvium.Examples;

/// <summary>
/// The examples print lists and tuples in Python's notation, so each one's output can be
/// compared line for line with what the Python example prints.
/// </summary>
internal static class Python
{
    /// <summary><c>['a', 'b']</c></summary>
    public static string List(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(Quote))}]";

    /// <summary><c>(0, 1, 2)</c></summary>
    public static string Tuple(IEnumerable<long> values) => $"({string.Join(", ", values)})";

    /// <summary><c>[0, 1, 2]</c></summary>
    public static string IntegerList(IEnumerable<long> values) => $"[{string.Join(", ", values)}]";

    public static string Quote(string value) => $"'{value}'";
}
