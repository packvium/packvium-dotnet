using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Execution;
using Packvium.Locks;
using Packvium.Packing;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Locks;

/// <summary>
/// Operator locks, written against the failure modes rather than the happy path: a lock must not
/// mutate the approved plan, must not place anything the engine would refuse, must come back as a
/// result rather than an exception when it cannot hold, and must be measured rather than assumed.
/// A fifth property was found by the reference: a lock must not cost the load anything it did not
/// ask for. Refusing by type turned eight cubes into one, and demanding the locked slot first
/// emptied a container whose only candidate was the origin; both are regression tests here.
/// </summary>
public sealed partial class LockTests
{
    /// <summary>Millimetres in ticks.</summary>
    private const long Mm = 16_000;

    private const long Mm100 = 100 * Mm;

    /// <summary>
    /// Eight 100 mm cubes that exactly fill a 200 mm box: the corpus's <c>exact-fit.json</c>, the
    /// simplest tiling. The reference's tests read the shared fixture and skip without it; it is
    /// inlined here so the lock tests run wherever the package's source does, and
    /// <see cref="TheInlinedFixtureIsTheCorpusOwn"/> holds the copy to the corpus where it is present.
    /// </summary>
    private const string ExactFit = """
        {"configuration": {"solver_profile": "fast", "time_limit_ms": 300000, "alternatives": 1,
                           "effort_budget": {"max_candidates_evaluated": 1000000, "max_placement_attempts": 1000000,
                                             "max_search_nodes": 1000000}},
         "items": [{"id": "cube", "quantity": 8, "dimensions": {"length": "100", "width": "100", "height": "100"}}],
         "containers": [{"id": "box", "inner_dimensions": {"length": "200", "width": "200", "height": "200"}}]}
        """;

    internal static JsonObject EightCubes() => (JsonObject)JsonReader.Parse(ExactFit);

    [Fact]
    public void TheInlinedFixtureIsTheCorpusOwn()
    {
        var fixture = Path.Combine(Tests.Conformance.ConformanceTests.CorpusDirectory(), "conformance", "fixtures", "exact-fit.json");
        if (!File.Exists(fixture))
        {
            Assert.Skip(Tests.Conformance.ConformanceTests.CorpusAbsent(fixture));
        }
        Assert.Equal(PythonJson.Dumps(JsonReader.Parse(File.ReadAllBytes(fixture)), sortKeys: true),
            PythonJson.Dumps(EightCubes(), sortKeys: true));
    }

    private static PlacementLock Lock(long x, long y = 0, long z = 0, string itemType = "cube", int containerIndex = 0) =>
        new(containerIndex, itemType, "LWH", (x, y, z));

    internal static HashSet<(int, string, string, (long, long, long))> Placed(JsonObject result)
    {
        var placed = new HashSet<(int, string, string, (long, long, long))>();
        var containers = Array(result, "containers");
        for (var index = 0; index < containers.Count; index++)
        {
            foreach (var placement in Array(containers[index], "placements"))
            {
                var position = (JsonObject)placement.Require("position");
                placed.Add((index, Text(placement, "item_type"), Text(placement, "orientation"),
                    (Ticks(position, "x"), Ticks(position, "y"), Ticks(position, "z"))));
            }
        }
        return placed;
    }

    internal static int Count(JsonObject result) => Array(result, "containers").Sum(container => Array(container, "placements").Count);

    // ----------------------------------------------------- a lock binds one instance

    /// <summary>
    /// A lock addresses a placement. Read as a rule about the type, "keep this cube here" becomes
    /// "no cube may be anywhere else", which silently drops seven items from a feasible load.
    /// </summary>
    [Fact]
    public void LockingOneCubeLeavesTheOtherSevenFree()
    {
        var request = EightCubes();
        Assert.True(Count(JsonApi.PackFromJson(request)) == 8, "the fixture no longer tiles; the rest of this test is void");

        var resolve = OperatorLocks.ResolveWithLocks(request, [Lock(0)]);

        Assert.True(resolve.Preserved);
        Assert.Equal(8, Count(resolve.Result));
    }

    public static TheoryData<long, long, long> Positions => new() { { 0, 0, 0 }, { Mm100, 0, 0 }, { Mm100, Mm100, Mm100 } };

    /// <summary>
    /// The far corner is the second regression: an empty container offers only the origin, so an
    /// implementation that filled the locked slot first could never reach (100, 100, 100).
    /// </summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void TheLockedSlotIsTheOneTheOperatorNamed(long x, long y, long z)
    {
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(x, y, z)]);

        Assert.True(resolve.Preserved);
        Assert.Contains((0, "cube", "LWH", (x, y, z)), Placed(resolve.Result));
        Assert.Equal(8, Count(resolve.Result));
    }

    [Fact]
    public void TwoLocksOnOneTypeAreBothHonoured()
    {
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(0), Lock(Mm100, Mm100)]);

        Assert.True(resolve.Preserved, string.Join(", ", resolve.Missing));
        Assert.Equal(8, Count(resolve.Result));
    }

    // ---------------------------------------------------- the approved plan survives

    [Fact]
    public void TheRequestIsNotMutated()
    {
        var request = EightCubes();
        var before = PythonJson.Dumps(request, sortKeys: true);
        OperatorLocks.ResolveWithLocks(request, [Lock(0)]);
        Assert.Equal(before, PythonJson.Dumps(request, sortKeys: true));
    }

    /// <summary>Property 1: the lock produces a candidate beside the approved plan, never over it.</summary>
    [Fact]
    public void TheOriginalResultIsASeparateArtifact()
    {
        var request = EightCubes();
        var approved = JsonApi.PackFromJson(request);
        var snapshot = PythonJson.Dumps(approved, sortKeys: true);

        var resolve = OperatorLocks.ResolveWithLocks(request, [Lock(0)]);

        Assert.Equal(snapshot, PythonJson.Dumps(approved, sortKeys: true));
        Assert.NotSame(approved, resolve.Result);
    }

    // ------------------------------- a self-contradictory set is refused before any solve

    /// <summary>
    /// Whether a lock fits is a question about a request; whether a lock set contradicts itself is
    /// not. Two overlapping boxes cannot both hold under any request, so the set is refused rather
    /// than solved into an emptied container.
    /// </summary>
    [Fact]
    public void TwoLocksClaimingTheSameSpaceAreRefusedWithoutSolving()
    {
        var refusal = Assert.Throws<LockSetException>(() => OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(0), Lock(50 * Mm)]));
        Assert.Equal("two locks claim overlapping space in container 0: cube at LWH@(0, 0, 0) and cube at LWH@(800000, 0, 0)",
            refusal.Message);
    }

    [Fact]
    public void TheSamePlacementLockedTwiceIsRefused()
    {
        var refusal = Assert.Throws<LockSetException>(() => OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(0), Lock(0)]));
        Assert.Equal("the same placement is locked twice: ('LWH', (0, 0, 0))", refusal.Message);
    }

    /// <summary>An operator naming an item that is not in the shipment made a typo, not an infeasible request.</summary>
    [Fact]
    public void ALockOnAnItemTheRequestDoesNotContainIsRefused()
    {
        var refusal = Assert.Throws<LockSetException>(() => OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(0, itemType: "wedge")]));
        Assert.Contains("no item type 'wedge'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>They describe different boxes, so identical coordinates in each are ordinary.</summary>
    [Fact]
    public void LocksInDifferentContainersNeverContradictEachOther()
    {
        var request = WithContainerQuantity(EightCubes(), 2);
        var resolve = OperatorLocks.ResolveWithLocks(request, [Lock(0), Lock(0, containerIndex: 1)]);
        Assert.Equal(2, resolve.Locks.Length);
    }

    // ------------------------------------------- an unsatisfiable lock is an answer

    /// <summary>
    /// Property 3. The lock reserves a box straddling the centre of a container that tiles exactly,
    /// so no cube has anywhere legal to go; the ordinary search reports it in its own vocabulary.
    /// </summary>
    [Fact]
    public void ALockTheSolverCannotSatisfyReturnsAResultAndNotAnException()
    {
        var offGrid = (50 * Mm, 50 * Mm, 50 * Mm);
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [new PlacementLock(0, "cube", "LWH", offGrid)]);

        Assert.False(resolve.Preserved);
        Assert.Equal(offGrid, resolve.Missing[0].PositionTicks);
        Assert.Contains(Text(resolve.Result, "status"), UnhonouredStatuses);
        var unpacked = Array(resolve.Result, "unpacked_items");
        Assert.NotEmpty(unpacked);
        Assert.Contains(Text((JsonObject)unpacked[0].Require("proof"), "level"), ProofLevels);
    }

    private static readonly string[] UnhonouredStatuses = ["best_found", "infeasible"];

    private static readonly string[] ProofLevels = ["proven", "observed", "inferred", "unknown_due_to_limit"];

    [Fact]
    public void AnUnhonouredLockIsNeverSoftenedIntoASuccess()
    {
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(50 * Mm, 50 * Mm, 50 * Mm)]);
        var refusal = Assert.Throws<ArgumentException>(() => new LockedResolve(resolve.Locks, resolve.Result, true, resolve.Missing));
        Assert.Contains("cannot be missing a lock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpreservedResolveMustSayWhichLockFailed()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new LockedResolve([], new JsonObject([]), false, []));
        Assert.Contains("must name the locks", refusal.Message, StringComparison.Ordinal);
    }

    // ------------------------------------- a lock outside the container is inert, not fatal

    /// <summary>
    /// A lock is offered to every container of the solve. One whose box does not fit here must not
    /// refuse every candidate: it is reported missing and the load is untouched.
    /// </summary>
    [Fact]
    public void ALockThatCannotApplyToThisContainerDoesNotEmptyIt()
    {
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(2 * Mm100)]);

        Assert.False(resolve.Preserved);
        Assert.Equal(8, Count(resolve.Result));
        Assert.Equal("feasible", Text(resolve.Result, "status"));
    }

    // -------------------------------------------------------- the constraint itself

    private static readonly Item Cube = new("cube", Dimensions.Mm("100", "100", "100"));

    private static readonly Container Box = new("box", Dimensions.Mm("200", "200", "200"));

    private static ConstraintContext Context(long x, long y, long z, Rotation rotation = Rotation.LWH,
        ImmutableArray<Placement> placements = default)
    {
        var dimensions = Cube.Dimensions.Rotated(rotation);
        return new ConstraintContext(Box, placements.IsDefault ? [] : placements, new ItemInstance(Cube, 1),
            new Point(x, y, z), rotation, dimensions, dimensions);
    }

    private static LockConstraint Constraint(string itemType = "cube") => new([Lock(0, itemType: itemType)]);

    /// <summary>
    /// Property 4, as cheaply as it can be tested: a constraint chain is an AND, so an allow says
    /// nothing; whatever this constraint decides, it cannot be the reason a placement exists.
    /// </summary>
    [Fact]
    public void TheConstraintCanOnlyEverRefuse()
    {
        var verdicts = new[] { 50 * Mm, Mm100 }.Select(x => Constraint().Evaluate(Context(x, 0, 0)).Allowed).ToHashSet();
        Assert.Equal(new HashSet<bool> { true, false }, verdicts);
    }

    /// <summary>What leaves the seven unlocked cubes free: a reservation is about one box.</summary>
    [Fact]
    public void ACandidateThatMissesTheReservedVolumeIsUntouched()
    {
        foreach (var (x, y, z) in new[] { (Mm100, 0L, 0L), (0L, Mm100, 0L), (Mm100, Mm100, Mm100) })
        {
            Assert.True(Constraint().Evaluate(Context(x, y, z)).Allowed, $"({x}, {y}, {z})");
        }
    }

    [Fact]
    public void ACandidateOverlappingTheReservedVolumeIsRefused() =>
        Assert.False(Constraint().Evaluate(Context(50 * Mm, 0, 0)).Allowed);

    [Fact]
    public void TheCandidateThatIsTheLockIsAdmitted() => Assert.True(Constraint().Evaluate(Context(0, 0, 0)).Allowed);

    [Fact]
    public void TheRefusalNamesTheLockAndCarriesTheOperatorCode()
    {
        var verdict = Constraint().Evaluate(Context(50 * Mm, 0, 0));

        Assert.False(verdict.Allowed);
        Assert.Equal(OperatorLocks.LockViolated, verdict.Code);
        Assert.Equal("operator_lock", verdict.Code);
        Assert.Equal("cube is locked to LWH@(0, 0, 0)", verdict.Detail);
    }

    [Fact]
    public void AnUnlockedItemTypeIsUntouched() => Assert.True(Constraint("wedge").Evaluate(Context(0, 0, 0)).Allowed);

    /// <summary>
    /// The reservation lasts exactly as long as the lock is outstanding; once the slot is held the
    /// ordinary overlap rule defends it, which keeps a lock set from accumulating cost.
    /// </summary>
    [Fact]
    public void ASatisfiedLockStopsReserving()
    {
        Assert.False(Constraint().Evaluate(Context(50 * Mm, 0, 0)).Allowed);

        var occupied = new Placement(new ItemInstance(Cube, 1), Point.Origin, Rotation.LWH, Cube.Dimensions, Point.Origin, Cube.Dimensions);
        Assert.True(Constraint().Evaluate(Context(50 * Mm, 0, 0, placements: [occupied])).Allowed);
    }

    /// <summary>Same origin, different rotation: not the locked placement, and it takes the reserved volume.</summary>
    [Fact]
    public void TheOrientationIsPartOfTheLockAndNotDecoration() =>
        Assert.False(Constraint().Evaluate(Context(0, 0, 0, Rotation.WLH)).Allowed);

    // ------------------------------------------------------------ addressing a lock

    /// <summary>
    /// The reference's other three cases -- two axes, a fractional tick and a boolean tick -- cannot
    /// be written here: the position is a tuple of three 64-bit integers.
    /// </summary>
    [Theory]
    [InlineData(-1, "cube", "LWH", 0L, "container index")]
    [InlineData(0, "", "LWH", 0L, "item type")]
    [InlineData(0, "cube", "sideways", 0L, "orientation this engine emits")]
    [InlineData(0, "cube", "LWH", -1L, "cannot be negative")]
    public void AnAddressTheEngineCouldNotHaveEmittedIsRefused(int containerIndex, string itemType, string orientation, long z,
        string message)
    {
        var refusal = Assert.Throws<ArgumentException>(() => new PlacementLock(containerIndex, itemType, orientation, (0, 0, z)));
        Assert.Contains(message, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOrientationIsQuotedAsTheReferenceQuotesIt() =>
        Assert.Equal("'sideways' is not an orientation this engine emits",
            Assert.Throws<ArgumentException>(() => new PlacementLock(0, "cube", "sideways", (0, 0, 0))).Message);

    /// <summary>The plan is where an operator sees a placement, so it is where they point at one.</summary>
    [Fact]
    public void ALockIsReadFromThePlansOwnReference()
    {
        var request = EightCubes();
        var result = JsonApi.PackFromJson(request);
        var plan = ExecutionPlan.Build(request, result);

        var locks = OperatorLocks.LocksFromPlan(plan, containerIndex: 0, itemTypes: ["cube"]);

        Assert.Equal(8, locks.Length);
        var expected = Placed(result).Select(placed => (placed.Item3, placed.Item4)).ToHashSet();
        Assert.Equal(expected, locks.Select(placementLock => placementLock.Slot).ToHashSet());
    }

    /// <summary>Every placement locked is the strongest form of property 1: the approved load comes back.</summary>
    [Fact]
    public void LockingAWholeContainerReproducesIt()
    {
        var request = EightCubes();
        var approved = JsonApi.PackFromJson(request);
        var locks = OperatorLocks.LocksFromPlan(ExecutionPlan.Build(request, approved), containerIndex: 0, itemTypes: ["cube"]);

        var resolve = OperatorLocks.ResolveWithLocks(request, locks);

        Assert.True(resolve.Preserved, string.Join(", ", resolve.Missing));
        Assert.Equal(Placed(approved), Placed(resolve.Result));
    }

    [Fact]
    public void ALockReadFromAnotherContainerIndexIsNotRead()
    {
        var request = EightCubes();
        var plan = ExecutionPlan.Build(request, JsonApi.PackFromJson(request));
        Assert.Empty(OperatorLocks.LocksFromPlan(plan, containerIndex: 1, itemTypes: ["cube"]));
    }

    [Fact]
    public void OnlyTheNamedItemTypesBecomeLocks()
    {
        var request = EightCubes();
        var plan = ExecutionPlan.Build(request, JsonApi.PackFromJson(request));
        Assert.Empty(OperatorLocks.LocksFromPlan(plan, containerIndex: 0, itemTypes: ["wedge"]));
    }

    // ---------------------------------------------------------------- the registry

    [Fact]
    public void AnEmptyLockSetReturnsTheCallersRegistryUnchanged()
    {
        var registry = new ExtensionRegistry();
        Assert.Same(registry, OperatorLocks.LockRegistry([], registry));
    }

    private sealed class Refuses : IPlacementConstraint
    {
        public ConstraintResult Evaluate(ConstraintContext context) => throw new InvalidOperationException("not evaluated in this test");
    }

    /// <summary>An application that locks a placement must not lose its own rules by doing so.</summary>
    [Fact]
    public void LockingDoesNotDropTheCallersOwnConstraints()
    {
        var mine = new Refuses();
        var registry = OperatorLocks.LockRegistry([Lock(0)], new ExtensionRegistry([mine]));

        Assert.Same(mine, registry.PlacementConstraints[0]);
        Assert.IsType<LockConstraint>(registry.PlacementConstraints[1]);
    }

    /// <summary>The instant the policy is evaluated at: reading a clock would make the request pack differently on different days.</summary>
    private const long AsOf = 1_704_067_200_000;

    private static readonly string Segregated = $$$"""
        {
          "configuration": {"solver_profile": "fast", "time_limit_ms": 300000},
          "policy": {
            "as_of": {{{AsOf}}},
            "rules": [{"id": "hazmat-food-segregation", "version": 1, "effective_at": {{{AsOf}}}, "priority": 100,
                       "separate_tags": {"tag": "hazmat", "from_tag": "food"}}]
          },
          "items": [
            {"id": "drum", "quantity": 1, "tags": ["hazmat"], "dimensions": {"length": "100", "width": "100", "height": "100"}},
            {"id": "crate", "quantity": 1, "tags": ["food"], "dimensions": {"length": "100", "width": "100", "height": "100"}}
          ],
          "containers": [{"id": "box", "quantity": 2, "inner_dimensions": {"length": "200", "width": "200", "height": "200"}}]
        }
        """;

    /// <summary>
    /// The request's policy compiles into placement constraints and a lock arrives through the same
    /// door. Replacing that set instead of adding to it would make locking anything a way to switch
    /// a segregation rule off.
    /// </summary>
    [Fact]
    public void LockingDoesNotSwitchOffThePolicyTheRequestCarries()
    {
        var request = (JsonObject)JsonReader.Parse(Segregated);
        Assert.True(Array(JsonApi.PackFromJson(request), "containers").Count == 2, "the rule no longer separates; the test is void");

        var resolve = OperatorLocks.ResolveWithLocks(request, [Lock(0, itemType: "drum")]);

        var containers = Array(resolve.Result, "containers");
        Assert.Equal(2, containers.Count);
        foreach (var container in containers)
        {
            var types = Array(container, "placements").Select(placement => Text(placement, "item_type")).ToHashSet();
            Assert.False(types.SetEquals(["drum", "crate"]), "the lock let the segregation rule lapse");
        }
    }

    // ------------------------------------------------------- the dependency direction

    /// <summary>
    /// A solver that knew about locks could grow a lock-aware path, which is the special case
    /// property 4 forbids. No source below the lock layer names it.
    /// </summary>
    [Fact]
    public void TheLockLayerIsACallerOfTheEngineAndNotAPartOfIt()
    {
        var source = Path.Combine(RepositoryRoot(), "src", "Packvium");
        var below = LayersBelowLocks.SelectMany(layer => Directory.GetFiles(Path.Combine(source, layer), "*.cs"));
        foreach (var path in below)
        {
            Assert.DoesNotMatch(LockLayer(), File.ReadAllText(path));
        }
    }

    private static readonly string[] LayersBelowLocks =
        ["Packing", "Algorithms", "Constraints", "Validation", "Domain", "Objectives", "Serialization"];

    [GeneratedRegex(@"Packvium\.Locks|\bPlacementLock\b|\bLockConstraint\b|\bOperatorLocks\b")]
    private static partial Regex LockLayer();

    /// <summary>
    /// Independent validation covers a locked solve by construction -- it is an ordinary solve --
    /// and this re-derives the cheapest of those guarantees from the placements alone.
    /// </summary>
    [Fact]
    public void ALockedResultIsStillAValidatedResult()
    {
        var resolve = OperatorLocks.ResolveWithLocks(EightCubes(), [Lock(Mm100)]);

        Assert.Equal("feasible", Text((JsonObject)resolve.Result.Require("feasibility"), "code"));
        var boxes = Array(resolve.Result, "containers").SelectMany(container => Array(container, "placements"))
            .Select(placement => (Origin: Vector((JsonObject)placement.Require("position"), "x", "y", "z"),
                Size: Vector((JsonObject)placement.Require("dimensions"), "length", "width", "height"))).ToList();
        for (var index = 0; index < boxes.Count; index++)
        {
            foreach (var other in boxes.Skip(index + 1))
            {
                var overlap = Enumerable.Range(0, 3).All(axis =>
                    boxes[index].Origin[axis] < other.Origin[axis] + other.Size[axis]
                    && other.Origin[axis] < boxes[index].Origin[axis] + boxes[index].Size[axis]);
                Assert.False(overlap, "a locked solve returned overlapping placements");
            }
        }
    }

    internal static JsonObject WithContainerQuantity(JsonObject request, int quantity)
    {
        var containers = Array(request, "containers");
        var first = new JsonObject([.. containers[0].Members.Where(member => member.Key != "quantity"),
            new KeyValuePair<string, JsonValue>("quantity", new JsonInteger(quantity))]);
        return new JsonObject(request.Members.Select(member => member.Key == "containers"
            ? new KeyValuePair<string, JsonValue>("containers", new JsonArray([first, .. containers.Skip(1)]))
            : member));
    }

    private static long[] Vector(JsonObject value, params string[] axes) => [.. axes.Select(axis => Ticks(value, axis))];

    private static long Ticks(JsonObject value, string axis) =>
        (long)((JsonInteger)((JsonObject)value.Require(axis)).Require("ticks")).Value;

    private static List<JsonObject> Array(JsonObject value, string key) => [.. ((JsonArray)value.Require(key)).Items.Cast<JsonObject>()];

    private static string Text(JsonObject value, string key) => ((JsonString)value.Require(key)).Value;

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
