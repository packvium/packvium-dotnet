using System;
using System.Linq;
using Packvium.Artifacts;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;

namespace Packvium.Examples;

/// <summary>
/// Get the same answer on every run: counted work instead of a clock.
///
/// Run it:
///
///     dotnet run --project examples -- reproducibility
///
/// The search tries several starts and keeps the best. Something has to decide when it stops,
/// and there are two choices:
///
/// - <c>time_limit_ms</c>, a wall clock. Its default is one second. When it runs out, the search
///   keeps what it has, and how far it got depends on the machine: its speed, what else is
///   running, the garbage collector. The same request can give a different answer on a loaded
///   server than on your laptop.
/// - <c>effort_budget</c>, counted work: candidates evaluated, placements attempted, search
///   nodes, restarts. The count is the same on every machine, so the answer is too.
///
/// Set an effort budget whenever the answer is stored, compared, audited or replayed, and keep
/// the time limit as a generous fuse against a genuine hang. The result says which one stopped
/// the search, in <c>termination</c>.
///
/// To make a slow machine repeatable here, this example uses the injected clock the
/// <see cref="Packer"/> accepts for testing: a clock that advances a fixed amount each time it is
/// read stands in for a faster or slower host. A real deployment never passes one.
/// </summary>
public static class Reproducibility
{
    private static readonly Item[] Items =
    [
        new("kettle", Dimensions.Mm("310", "220", "140"), Weight.Parse("2 kg"), 5),
        new("toaster", Dimensions.Mm("250", "250", "250"), Weight.Parse("3 kg"), 4),
        new("knife-block", Dimensions.Mm("400", "150", "100"), Weight.Parse("1 kg"), 6),
        new("scale", Dimensions.Mm("180", "120", "90"), Weight.Parse("0.5 kg"), 8),
    ];

    private static readonly Container[] Boxes =
    [
        new("small", Dimensions.Mm("450", "350", "300"), costMinor: 100),
        new("medium", Dimensions.Mm("600", "400", "400"), costMinor: 160),
        new("large", Dimensions.Mm("800", "600", "500"), costMinor: 240),
    ];

    /// <summary>A fuse, not a limit: nothing below comes close to it on a real machine.</summary>
    private const long FuseMs = 60_000;

    public static void Run()
    {
        Python.Section("1. A clock-limited search answers differently on different machines");
        // The same 100 ms limit on four simulated hosts.
        foreach (var (label, step) in new[] { ("fast host", 10_000L), ("busy host", 50_000L),
                     ("slower host", 200_000L), ("overloaded", 1_000_000L) })
        {
            Console.WriteLine($"  {label,-12} {Describe(new Packer(PackingConfig.Balanced(100), clock: Host(step)).Pack(Items, Boxes))}");
        }
        Python.Say("",
            "Same request, four answers. Each is a valid packing of what the search reached",
            "before the clock ran out, and the items it never reached are listed as left out,",
            "but none can be reproduced: run it again on a different day and you get whichever",
            "one the machine allows. `termination` says",
            "`time_limit`, and `ReplayLevel` refuses to promise a replay.");

        Python.Section("2. An effort budget answers the same everywhere");
        var enough = new EffortBudget(MaxCandidatesEvaluated: 5_000);
        foreach (var (label, step) in new[] { ("fast host", 10_000L), ("slower host", 200_000L), ("overloaded", 1_000_000L) })
        {
            Console.WriteLine($"  {label,-12} {Describe(new Packer(Budgeted(enough), clock: Host(step)).Pack(Items, Boxes))}");
        }
        Python.Say("",
            "The budget is large enough that the search finishes, so `termination` is",
            "`complete`, and it would stop at the same count on any machine if it were not.");

        Python.Section("3. A budget that is too small is still reproducible, and says so");
        var tooSmall = new EffortBudget(MaxCandidatesEvaluated: 500);
        foreach (var (label, step) in new[] { ("fast host", 10_000L), ("overloaded", 1_000_000L) })
        {
            Console.WriteLine($"  {label,-12} {Describe(new Packer(Budgeted(tooSmall), clock: Host(step)).Pack(Items, Boxes))}");
        }
        Python.Say("",
            "The search stopped early and left items out -- `termination` is `effort_limit`,",
            "not `complete` -- but it stopped at the same point on both hosts, so the answer",
            "can be replayed exactly. Raise the budget until the answer is good enough, then",
            "keep it fixed.");

        Python.Section("4. Reading `termination`");
        var result = new Packer(Budgeted(new EffortBudget(MaxRestarts: 4))).Pack(Items, Boxes);
        var termination = (JsonObject)ResultWriter.Write(result).Require("termination");
        Console.WriteLine($"  code: {((JsonString)termination.Require("code")).Value}");
        foreach (var start in ((JsonArray)termination.Require("starts")).Items.Cast<JsonObject>())
        {
            var marker = ((JsonBool)start.Require("selected")).Value ? "  <- selected" : string.Empty;
            var completed = ((JsonBool)start.Require("completed")).Value ? "True" : "False";
            Console.WriteLine($"    {((JsonString)start.Require("id")).Value,-28} completed={completed,-5}{marker}".TrimEnd());
        }
        Python.Say("",
            "`max_restarts` caps how many portfolio starts run -- here four -- which bounds the",
            "work of a large request as well as fixing it. `code` describes the selected start:",
            "a losing start that was cut short leaves it `complete`, and shows up in its own",
            "record and in `all_required_starts_completed` instead. Over JSON, the same budget",
            "is `configuration.effort_budget`.");
    }

    private static PackingConfig Budgeted(EffortBudget budget) =>
        new(SolverProfile.Balanced, FuseMs, effortBudget: budget);

    /// <summary>
    /// A monotonic clock that moves <paramref name="nanosecondsPerRead"/> every time it is read: a
    /// larger step is a slower or busier machine doing the same work.
    /// </summary>
    private static Func<long> Host(long nanosecondsPerRead)
    {
        var next = 0L;
        return () =>
        {
            var reading = next;
            next += nanosecondsPerRead;
            return reading;
        };
    }

    private static string Describe(PackingResult result)
    {
        var boxes = string.Join(",", result.Containers.Select(packed => packed.Container.Id));
        return $"{boxes,-6} left out {result.Unpacked.Length,2}  termination {result.Termination.Code,-12}"
               + $" replay {OperationalArtifact.ReplayLevel(ResultWriter.Write(result))}";
    }
}
