using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Objectives;
using Packvium.Packing;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// Extensions: a rule of your own, and a ranking of your own.
///
/// Run it:
///
///     dotnet run --project examples -- extensions
///
/// A custom <see cref="IPlacementConstraint"/> answers about one candidate position and never
/// searches; a custom <see cref="ISolutionScorer"/> ranks finished solutions. Neither exists
/// on the wire, which is the point the example ends on.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// An example must not change answer merely because the host was busy. These solves need
    /// a fraction of the budget; the generous wall-clock value is only a safety fuse, so a
    /// loaded machine cannot cut the multi-start portfolio short and let a different start win.
    /// </summary>
    private const long SafetyFuseMs = 60_000;

    /// <summary>Refuses a fragile-tagged item with its base above the limit -- a warehouse rule with no field.</summary>
    private sealed class FragileHeightLimit(Length limit, string tag = "fragile") : IPlacementConstraint
    {
        // O(1): it reads the candidate's z coordinate and nothing else, because it runs per candidate.
        public ConstraintResult Evaluate(ConstraintContext context)
        {
            if (!context.Item.Item.Tags.Contains(tag) || context.Point.Z <= limit.Ticks)
            {
                return ConstraintResult.Allow();
            }
            return ConstraintResult.Reject("fragile_too_high",
                $"base at {new Length(context.Point.Z).DecimalText("mm")}mm is above the {limit.DecimalText("mm")}mm limit for '{tag}' items");
        }
    }

    /// <summary>
    /// Prefers solutions whose containers weigh about the same. Unpacked count stays first, and
    /// the canonical vector breaks ties, so two equally balanced answers are not ranked by luck.
    /// </summary>
    private sealed class EvenlyLoadedContainers : ISolutionScorer
    {
        public ScoreVector ScoreContainers(IReadOnlyList<PackedContainer> containers, IReadOnlyList<UnpackedItem> unpacked)
        {
            var loads = containers.Select(c => c.Placements.Sum(p => p.Instance.Item.Weight.Ticks)).ToList();
            var spread = loads.Count > 0 ? loads.Max() - loads.Min() : 0;
            var canonical = new DefaultSolutionScorer().ScoreContainers(containers, unpacked).Keys;
            return new ScoreVector([unpacked.Count, spread, .. canonical.Skip(1)]);
        }
    }

    public static void Run()
    {
        // One crate wide, so the column must grow upwards and the rule has something to refuse.
        Item[] items =
        [
            new("crate", Dimensions.Mm("400", "400", "300"), Weight.Parse("12 kg"), quantity: 3),
            new("vase", Dimensions.Mm("400", "400", "200"), Weight.Parse("2 kg"), quantity: 2, tags: ["fragile"]),
        ];
        Container[] containers = [new("column", Dimensions.Mm("400", "400", "1300"), maxPayload: Weight.Parse("200 kg"), quantity: 1)];

        var unrestricted = new Packer(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs)).Pack(items, containers);
        var highest = Vases(unrestricted).Max(p => p.Position.Z);
        Console.WriteLine($"without the rule, the highest vase sits at {new Length(highest).DecimalText("mm")} mm");

        var restricted = new Packer(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs),
            new ExtensionRegistry([new FragileHeightLimit(Length.Parse("400 mm"))])).Pack(items, containers);
        var heights = Vases(restricted).Select(p => new Length(p.Position.Z).DecimalText("mm")).Order(StringComparer.Ordinal);
        Console.WriteLine($"with a 400mm limit, vases sit at {Python.List(heights)} and {restricted.Unpacked.Length} were left behind");
        foreach (var unpacked in restricted.Unpacked)
        {
            Console.WriteLine($"   left behind: {unpacked.Instance.Item.Id} -> {unpacked.Reason} {Python.List(unpacked.Details)}");
        }

        // Two heavy and two light items, two boxes of two slots. Every arrangement uses the same
        // volume, so the built-in objectives are indifferent -- and put both anvils in one box.
        Item[] lopsided =
        [
            new("anvil", Dimensions.Mm("200", "200", "200"), Weight.Parse("10 kg"), quantity: 2),
            new("pillow", Dimensions.Mm("200", "200", "200"), Weight.Parse("1 kg"), quantity: 2),
        ];
        Container[] twoBoxes = [new("box", Dimensions.Mm("400", "200", "200"), maxPayload: Weight.Parse("50 kg"), quantity: 2)];
        Console.WriteLine();
        foreach (var (label, scorer) in (ValueTuple<string, ISolutionScorer?>[])[("default", null), ("evenly loaded", new EvenlyLoadedContainers())])
        {
            var result = new Packer(PackingConfig.Balanced(timeLimitMs: SafetyFuseMs), solutionScorer: scorer).Pack(lopsided, twoBoxes);
            var contents = result.Containers.Select(c => Python.List(c.Placements.Select(p => p.Instance.Item.Id).Order(StringComparer.Ordinal)));
            var weights = result.Containers.Select(c => c.PayloadWeight.DecimalText("kg") + " kg");
            Console.WriteLine($"{label,15}: [{string.Join(", ", contents)}]  ->  {Python.List(weights)}");
        }

        Console.WriteLine();
        Console.WriteLine("Neither rule above exists on the wire. Hand the same request to the Rust or");
        Console.WriteLine("JavaScript engine and you get the unrestricted answer -- which is exactly why a");
        Console.WriteLine("rule everyone must obey belongs in the request as data, not in a class.");
    }

    private static IEnumerable<Placement> Vases(PackingResult result) =>
        result.Containers.SelectMany(c => c.Placements).Where(p => p.Instance.Item.Id == "vase");
}
