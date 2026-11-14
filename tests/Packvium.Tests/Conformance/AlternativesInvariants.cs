using System.Collections.Generic;
using System.Linq;
using Packvium.Serialization;

namespace Packvium.Tests.Conformance;

/// <summary>
/// <c>alternatives_problems</c> of the v1.2.0 runner: what <c>alternatives[]</c> owes
/// on any one engine's live result, without asserting agreement between two engines -- the
/// canonical projection drops the field, because engines need not explore the same starts.
/// No alternative may outrank the winner, the list holds at most
/// <c>configuration.alternatives - 1</c> entries (the setting counts the winner, default 3), and
/// every entry carries the score vector both rules are stated in.
/// </summary>
public static class AlternativesInvariants
{
    public static List<string> Problems(JsonObject request, JsonObject result)
    {
        var alternatives = result.GetPresent("alternatives");
        if (alternatives is null)
        {
            return ["omits `alternatives`; the schema requires the field even when the list is empty"];
        }
        if (alternatives is not JsonArray list)
        {
            return [$"reports `alternatives` as {PythonCoercion.TypeName(alternatives)}, not an array"];
        }
        var problems = new List<string>();
        var configuration = request.GetPresent("configuration") as JsonObject;
        var cap = configuration?.Get("alternatives") is { } configured ? PythonCoercion.BigInt(configured) : 3;
        if (list.Items.Length > cap - 1)
        {
            problems.Add($"returned {list.Items.Length} alternatives for `configuration.alternatives: {cap}`; "
                         + $"the setting counts the winner, so at most {cap - 1} runners-up may be reported");
        }
        var winner = result.Get("score") as JsonArray;
        for (var index = 0; index < list.Items.Length; index++)
        {
            if ((list.Items[index] as JsonObject)?.Get("score") is not JsonArray { Items.IsEmpty: false } theirs)
            {
                problems.Add($"alternative[{index}] carries no score vector, so it cannot be ranked against the winner");
                continue;
            }
            if (winner is not null && Lexicographic(theirs, winner) < 0)
            {
                problems.Add($"alternative[{index}] outranks the winner -- then it should have been the winner");
            }
        }
        return problems;
    }

    /// <summary>Python's list order over integer vectors: first difference decides, and a proper prefix sorts first.</summary>
    private static int Lexicographic(JsonArray left, JsonArray right)
    {
        var shared = left.Items.Zip(right.Items, (a, b) => PythonCoercion.BigInt(a).CompareTo(PythonCoercion.BigInt(b)));
        var first = shared.FirstOrDefault(order => order != 0);
        return first != 0 ? first : left.Items.Length.CompareTo(right.Items.Length);
    }
}
