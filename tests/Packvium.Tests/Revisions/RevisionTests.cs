using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Packvium.Artifacts;
using Packvium.Revisions;
using Packvium.Serialization;
using Xunit;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using Node = System.Text.Json.Nodes.JsonObject;
using NodeArray = System.Text.Json.Nodes.JsonArray;

namespace Packvium.Tests.Revisions;

/// <summary>
/// Plan revisions: an append-only, hash-chained record of exceptions (<c>test_revisions.py</c>).
/// Documents are edited as <see cref="JsonNode"/> trees and read back through the port's own
/// reader, so a test changes one field the way the reference's does.
/// </summary>
public sealed class RevisionTests
{
    internal const string CubeRequest = """
        {"items": [{"id": "cube", "quantity": 4, "weight": "1000",
                    "dimensions": {"length": "100", "width": "100", "height": "100"}},
                   {"id": "slab", "quantity": 1, "dimensions": {"length": "200", "width": "100", "height": "50"}}],
         "containers": [{"id": "box", "quantity": 3, "inner_dimensions": {"length": "200", "width": "100", "height": "200"}},
                        {"id": "crate", "inner_dimensions": {"length": "300", "width": "200", "height": "200"}}],
         "configuration": {"solver_profile": "balanced", "minimum_support_ratio": 1.0}}
        """;

    internal static JsonValue J(string json) => JsonReader.Parse(json);

    internal static Node N(JsonValue value) => (Node)JsonNode.Parse(CanonicalJsonWriter.Write(value))!;

    internal static JsonValue V(JsonNode node) => JsonReader.Parse(node.ToJsonString());

    internal static JsonObject Request() => (JsonObject)J(CubeRequest);

    internal static JsonObject ArtifactFor(JsonValue request) => OperationalArtifact.Build(request, JsonApi.PackFromJson(request));

    internal static string Locked(string x = "0", string z = "0", string kind = "placement_locked", int sequence = 1) =>
        $$$"""{"sequence": {{{sequence}}}, "type": "{{{kind}}}", "placement": {"item_type": "cube", "container_type": "box", "position": {"x": "{{{x}}}", "z": "{{{z}}}"}, "orientation": "LWH"}}""";

    private const string LockedPlacement =
        """{"item_type": "cube", "container_type": "box", "position": {"x": "0", "z": "0"}, "orientation": "LWH"}""";

    internal static (List<JsonObject> Revisions, List<JsonObject?> Artifacts) Chain()
    {
        var root = PlanRevision.Root(Request());
        var firstArtifact = ArtifactFor(root.Require("request"));
        var first = PlanRevision.Derive(root, firstArtifact,
            J($$"""[{{Locked(sequence: 1)}}, {"sequence": 2, "type": "item_missing", "item_type": "cube", "quantity": 1}]"""));
        var secondArtifact = ArtifactFor(first.Require("request"));
        var second = PlanRevision.Derive(first, secondArtifact, J($"[{Locked(kind: "placement_verified", sequence: 3)}]"));
        return ([root, first, second], [null, firstArtifact, secondArtifact]);
    }

    private static string Canonical(JsonValue value) => CanonicalJsonWriter.Write(value);

    private static PlanRevisionException Refused(Action action) => Assert.Throws<PlanRevisionException>(action);

    private static JsonArray Chain(IEnumerable<JsonValue> revisions) => new JsonArray([.. revisions]);

    private static JsonArray Artifacts(IEnumerable<JsonValue?> artifacts) =>
        new JsonArray([.. artifacts.Select(artifact => artifact ?? JsonNull.Instance)]);

    private static HashSet<string> Codes(IEnumerable<JsonValue> revisions, IEnumerable<JsonValue?>? artifacts = null) =>
        [.. PlanRevision.VerifyChain(Chain(revisions), artifacts is null ? null : Artifacts(artifacts)).Select(issue => issue.Code)];

    private static List<JsonValue> Edited(List<JsonObject> revisions, int index, Action<Node> edit)
    {
        var node = N(revisions[index]);
        edit(node);
        return [.. revisions.Select((revision, position) => position == index ? V(node) : revision)];
    }

    [Fact]
    public void TheRootRecordsNothingAgainstTheRequest()
    {
        var expected = J($$"""
            {"format": "packvium-plan-revision/v1", "suite_version": "{{OperationalArtifact.SuiteVersion}}", "revision": 0,
             "parent": null, "approved": null, "events": [], "request": {{CubeRequest}}}
            """);
        Assert.Equal(Canonical(expected), Canonical(PlanRevision.Root(Request())));
    }

    [Fact]
    public void ARevisionLinksItsParentAndApprovedArtifactByDigest()
    {
        var (revisions, artifacts) = Chain();
        var first = revisions[1];
        var approved = (JsonObject)first.Require("approved");
        Assert.Equal(new JsonInteger(1), first.Require("revision"));
        Assert.Equal(new JsonString(PlanRevision.Digest(revisions[0])), first.Require("parent"));
        Assert.Equal(new JsonString(PlanRevision.Digest(artifacts[1]!)), approved.Require("artifact"));
        Assert.Equal(Canonical(((JsonObject)artifacts[1]!.Require("provenance")).Require("replay")), Canonical(approved.Require("replay")));
    }

    [Fact]
    public void ADigestIsSha256OverTheCanonicalBytes()
    {
        var root = PlanRevision.Root(Request());
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlanRevision.CanonicalJson(root)))).ToLowerInvariant();
        Assert.Equal($"sha256:{expected}", PlanRevision.Digest(root));
    }

    [Fact]
    public void TransportSpellingNeverChangesADigest()
    {
        var root = PlanRevision.Root(Request());
        var reparsed = N(root);
        reparsed["request"]!["configuration"]!["minimum_support_ratio"] = 1;
        Assert.Equal(PlanRevision.Digest(root), PlanRevision.Digest(V(reparsed)));
    }

    [Fact]
    public void TheSameInputsDeriveTheSameBytes() =>
        Assert.Equal(Chain().Revisions.Select(Canonical), Chain().Revisions.Select(Canonical));

    [Fact]
    public void AMissingItemLowersItsQuantityAndALastOneRemovesTheType()
    {
        var request = Request();
        var lowered = PlanRevision.ApplyEvents(request, J("""[{"sequence": 1, "type": "item_missing", "item_type": "cube", "quantity": 3}]"""));
        Assert.Equal(new JsonInteger(1), ((JsonObject)((JsonArray)lowered.Require("items")).Items[0]).Require("quantity"));
        var removed = PlanRevision.ApplyEvents(request, J("""[{"sequence": 1, "type": "item_missing", "item_type": "slab", "quantity": 1}]"""));
        Assert.Equal(["cube"], Ids(removed, "items"));
        Assert.Equal(Canonical(Request()), Canonical(request));
    }

    private static IEnumerable<string> Ids(JsonObject request, string key) =>
        ((JsonArray)request.Require(key)).Items.Select(entry => ((JsonString)((JsonObject)entry).Require("id")).Value);

    [Fact]
    public void ASubstitutedContainerIsReplacedWhereItStood()
    {
        var derived = PlanRevision.ApplyEvents(Request(), J("""
            [{"sequence": 1, "type": "container_substituted", "container_type": "box",
              "replacement": {"id": "box-b", "inner_dimensions": {"length": "210", "width": "110", "height": "210"}}}]
            """));
        Assert.Equal(["box-b", "crate"], Ids(derived, "containers"));
    }

    [Fact]
    public void ALockThenAVerificationOfTheSameBoxFixesItOnce()
    {
        var derived = PlanRevision.ApplyEvents(Request(), J($"[{Locked()}, {Locked(kind: "placement_verified", sequence: 2)}]"));
        Assert.Equal(Canonical(J($"[{LockedPlacement}]")), Canonical(derived.Require("fixed_placements")));
    }

    private const string Unholdable =
        """{"item_type": "cube", "container_type": "box", "position": {"x": 1152921504606846976, "z": "0"}, "orientation": "LWH"}""";

    [Fact]
    public void AFixedSetWithNoCanonicalFormStillDeduplicatesByFirstMatch()
    {
        var request = Request().With("fixed_placements", J($"[{LockedPlacement}, {Unholdable}]"));
        var derived = PlanRevision.ApplyEvents(request, J($"[{Locked()}]"));
        Assert.Equal(PythonJson.Dumps(J($"[{LockedPlacement}, {Unholdable}]"), sortKeys: true),
            PythonJson.Dumps(derived.Require("fixed_placements"), sortKeys: true));
    }

    [Fact]
    public void ALockWithNoCanonicalFormIsRecordedForAdmissionToJudge()
    {
        var derived = PlanRevision.ApplyEvents(Request(), J($$"""[{"sequence": 1, "type": "placement_locked", "placement": {{Unholdable}}}]"""));
        Assert.Equal(PythonJson.Dumps(J($"[{Unholdable}]"), sortKeys: true),
            PythonJson.Dumps(derived.Require("fixed_placements"), sortKeys: true));
    }

    [Fact]
    public void ManyDistinctLocksAreAllKept()
    {
        var events = string.Join(",", Enumerable.Range(0, 80).Select(index => Locked(x: index.ToString(System.Globalization.CultureInfo.InvariantCulture), sequence: index + 1)));
        var derived = PlanRevision.ApplyEvents(Request(), J($"[{events}]"));
        Assert.Equal(80, ((JsonArray)derived.Require("fixed_placements")).Items.Length);
    }

    [Fact]
    public void ADerivedRequestSolvesWithItsFixedItemsInPlace()
    {
        var result = JsonApi.PackFromJson(Chain().Revisions[^1].Require("request"));
        var containers = ((JsonArray)result.Require("containers")).Items.Cast<JsonObject>().ToList();
        var placements = containers.SelectMany(container => ((JsonArray)container.Require("placements")).Items.Cast<JsonObject>()
            .Select(placement => (Container: ((JsonString)container.Require("id")).Value, Placement: placement))).ToList();
        Assert.Equal([("box#1", "cube#1")], placements.Where(entry => entry.Placement.Get("fixed") is JsonBool { Value: true })
            .Select(entry => (entry.Container, ((JsonString)entry.Placement.Require("item_id")).Value)));
        Assert.Equal(4, placements.Count);
    }

    [Theory]
    [InlineData("""{"sequence": 1, "type": "item_missing", "item_type": "pallet", "quantity": 1}""", "no item \"pallet\"")]
    [InlineData("""{"sequence": 1, "type": "container_substituted", "container_type": "box", "replacement": {"id": "crate", "inner_dimensions": {"length": 1, "width": 1, "height": 1}}}""", "already crate")]
    public void AnEventThatContradictsTheRequestIsRefused(string @event, string fragment)
    {
        var error = Refused(() => PlanRevision.ApplyEvents(Request(), J($"[{@event}]")));
        Assert.Equal("event_conflict", error.Code);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"sequence": 2, "type": "item_missing", "item_type": "cube", "quantity": 4}""")]
    [InlineData("""{"sequence": 2, "type": "container_substituted", "container_type": "box", "replacement": {"id": "box-b", "inner_dimensions": {"length": 1, "width": 1, "height": 1}}}""")]
    public void FixedItemsCannotGoMissingOrLoseTheirContainer(string @event)
    {
        var withLock = PlanRevision.ApplyEvents(Request(), J($"[{Locked()}]"));
        Assert.Equal("event_conflict", Refused(() => PlanRevision.ApplyEvents(withLock, J($"[{@event}]"))).Code);
    }

    [Fact]
    public void TheLastItemTypeCannotGoMissing()
    {
        var request = N(Request());
        request["items"]!.AsArray().RemoveAt(1);
        var error = Refused(() => PlanRevision.ApplyEvents(V(request), J("""[{"sequence": 1, "type": "item_missing", "item_type": "cube", "quantity": 4}]""")));
        Assert.Contains("no item would remain", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> MalformedEvents() =>
    [
        "[]",
        $"[{Locked(sequence: 2)}]",
        $$"""[{"sequence": 1, "type": "placement_moved", "placement": {{LockedPlacement}}}]""",
        $$"""[{"sequence": 1, "type": "placement_locked", "placement": {{LockedPlacement}}, "note": "strapped"}]""",
        """[{"sequence": 1, "type": "item_missing", "item_type": "cube"}]""",
        """[{"sequence": 1, "type": "item_missing", "item_type": "cube", "quantity": 0}]""",
        """[{"sequence": true, "type": "item_missing", "item_type": "cube", "quantity": 1}]""",
        """[{"sequence": 1, "type": "placement_locked", "placement": {"item_type": "cube", "container_type": "box", "orientation": "XYZ"}}]""",
        """[{"sequence": 1, "type": "placement_locked", "placement": {"item_type": "cube", "container_type": "box", "orientation": "LWH", "container_instance": 0}}]""",
        "[\"item_missing\"]",
        """[{"sequence": 1, "type": "container_substituted", "container_type": "", "replacement": {"id": "b"}}]""",
        """[{"sequence": 1, "type": "container_substituted", "container_type": "box", "replacement": "b"}]""",
        """[{"sequence": 1, "type": "placement_locked", "placement": "box#1"}]""",
        """[{"sequence": 1, "type": "placement_locked", "placement": {"item_type": "cube", "container_type": "box", "orientation": "LWH", "item_id": "cube#1"}}]""",
        """[{"sequence": 1, "type": "placement_locked", "placement": {"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": [0, 0, 0]}}]""",
    ];

    [Theory]
    [MemberData(nameof(MalformedEvents))]
    public void AMalformedEventIsRefused(string events)
    {
        var root = PlanRevision.Root(Request());
        Assert.Equal("invalid_event", Refused(() => PlanRevision.Derive(root, ArtifactFor(root.Require("request")), J(events))).Code);
    }

    [Fact]
    public void AnArtifactBuiltFromAnotherRequestIsRefused()
    {
        var other = N(Request());
        other["items"]![0]!["quantity"] = 3;
        var error = Refused(() => PlanRevision.Derive(PlanRevision.Root(Request()), ArtifactFor(V(other)), J($"[{Locked()}]")));
        Assert.Equal("invalid_artifact", error.Code);
    }

    [Fact]
    public void AParentThatIsNotARevisionIsRefused() =>
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.Derive(Request(), ArtifactFor(Request()), J($"[{Locked()}]"))).Code);

    [Fact]
    public void APlanThatStoppedOnTimeIsMarkedInTheChain()
    {
        var root = PlanRevision.Root(Request());
        var artifact = N(ArtifactFor(root.Require("request")));
        artifact["provenance"]!["solver"]!["time_limit_reached"] = true;
        artifact["provenance"]!["replay"] = JsonNode.Parse("""{"level": "not_guaranteed", "because": "provenance.solver.time_limit_reached"}""");
        var revision = PlanRevision.Derive(root, V(artifact), J($"[{Locked()}]"));
        Assert.Equal(new JsonString("not_guaranteed"), ((JsonObject)((JsonObject)revision.Require("approved")).Require("replay")).Require("level"));
    }

    [Fact]
    public void AnIntactChainVerifiesClean()
    {
        var (revisions, artifacts) = Chain();
        Assert.Empty(PlanRevision.VerifyChain(Chain(revisions), Artifacts(artifacts)));
    }

    [Fact]
    public void ReorderedEventsAreCaught()
    {
        var (revisions, _) = Chain();
        var reordered = Edited(revisions, 1, node =>
        {
            var events = node["events"]!.AsArray();
            var first = events[0]!.DeepClone();
            events[0] = events[1]!.DeepClone();
            events[1] = first;
        });
        Assert.Contains("sequence_gap", Codes(reordered));
    }

    [Fact]
    public void AMissingEventIsCaught()
    {
        var (revisions, _) = Chain();
        Assert.Superset(new HashSet<string>(["sequence_gap", "request_mismatch"]), Codes(Edited(revisions, 1, node => node["events"]!.AsArray().RemoveAt(1))));
    }

    [Fact]
    public void AnAlteredEventIsCaught()
    {
        var (revisions, _) = Chain();
        Assert.Contains("request_mismatch", Codes(Edited(revisions, 1, node => node["events"]![1]!["quantity"] = 2)));
    }

    [Fact]
    public void AnAlteredRequestBreaksTheLinkToTheNextRevision()
    {
        var (revisions, _) = Chain();
        Assert.Superset(new HashSet<string>(["request_mismatch", "parent_mismatch"]),
            Codes(Edited(revisions, 1, node => node["request"]!["items"]![0]!["quantity"] = 9)));
    }

    [Fact]
    public void ADroppedRevisionIsCaught()
    {
        var (revisions, _) = Chain();
        Assert.Superset(new HashSet<string>(["revision_number", "parent_mismatch", "sequence_gap"]), Codes([revisions[0], revisions[2]]));
    }

    [Fact]
    public void ASubstitutedArtifactIsCaught()
    {
        var (revisions, artifacts) = Chain();
        Assert.Contains("artifact_mismatch", Codes(revisions, [artifacts[0], artifacts[1], artifacts[1]]));
    }

    [Fact]
    public void AChainThatIsNotMadeOfRevisionsStopsTheAudit()
    {
        var (revisions, _) = Chain();
        var issues = PlanRevision.VerifyChain(Chain([revisions[0], J("""{"format": "something-else"}"""), revisions[2]]));
        Assert.Equal(["invalid_revision"], issues.Select(issue => issue.Code));
    }

    [Theory]
    [InlineData("""{"format": "packvium-plan-revision/v1", "revision": -1, "request": {}, "events": []}""")]
    [InlineData("""{"format": "packvium-plan-revision/v1", "revision": 0, "request": [], "events": []}""")]
    [InlineData("""{"format": "packvium-plan-revision/v1", "revision": 0, "request": {}, "events": {}}""")]
    public void AMalformedParentIsRefused(string parent) =>
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.Derive(J(parent), ArtifactFor(Request()), J($"[{Locked()}]"))).Code);

    [Fact]
    public void ANonRootParentWithoutEventsIsRefused()
    {
        var parent = J($$"""{"format": "packvium-plan-revision/v1", "revision": 2, "request": {{CubeRequest}}, "events": []}""");
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.Derive(parent, ArtifactFor(Request()), J($"[{Locked()}]"))).Code);
    }

    [Fact]
    public void ARootNeedsARequestObjectWithACanonicalSpelling()
    {
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.Root(J("[]"))).Code);
        Assert.Equal("number_out_of_range", Refused(() =>
            PlanRevision.Root(J("""{"items": [], "containers": [], "configuration": {"seed": 1152921504606846976}}"""))).Code);
    }

    [Theory]
    [InlineData("""{"format": "packvium-execution-plan/v1"}""")]
    [InlineData("""{"format": "packvium-operational-artifact/v1", "provenance": {"request": {}}}""")]
    public void AMalformedArtifactIsRefused(string artifact) =>
        Assert.Equal("invalid_artifact", Refused(() => PlanRevision.Derive(PlanRevision.Root(Request()), J(artifact), J($"[{Locked()}]"))).Code);

    [Fact]
    public void AnItemWithoutAQuantityCountsAsOne()
    {
        var request = N(Request());
        request["items"]![1]!.AsObject().Remove("quantity");
        var derived = PlanRevision.ApplyEvents(V(request), J("""[{"sequence": 1, "type": "item_missing", "item_type": "slab", "quantity": 1}]"""));
        Assert.Equal(["cube"], Ids(derived, "items"));
    }

    [Fact]
    public void ASubstitutionNeedsTheContainerItReplaces() =>
        Assert.Equal("event_conflict", Refused(() => PlanRevision.ApplyEvents(Request(),
            J("""[{"sequence": 1, "type": "container_substituted", "container_type": "pallet", "replacement": {"id": "p2"}}]"""))).Code);

    [Fact]
    public void ARequestWhoseEntriesAreNotObjectsIsRefused()
    {
        var request = Request().With("items", J("[\"cube\"]"));
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.ApplyEvents(request,
            J("""[{"sequence": 1, "type": "item_missing", "item_type": "cube", "quantity": 1}]"""))).Code);
    }

    [Fact]
    public void ARootWithEventsAndARevisionWithoutThemAreCaught()
    {
        var (revisions, _) = Chain();
        var edited = Edited(revisions, 0, node => node["events"] = JsonNode.Parse($"[{Locked()}]"));
        var second = N(revisions[2]);
        second["events"] = new NodeArray();
        edited[2] = V(second);
        var gaps = PlanRevision.VerifyChain(Chain(edited)).Where(issue => issue.Code == "sequence_gap")
            .Select(issue => issue.Revision).ToList();
        Assert.Equal([0, 1], gaps.Take(2));
        Assert.Contains(2, gaps);
    }

    [Fact]
    public void EventsThatNoLongerApplyAreARequestMismatch()
    {
        var (revisions, _) = Chain();
        Assert.Contains("request_mismatch", Codes(Edited(revisions, 1, node => node["events"]![1]!["item_type"] = "pallet")));
    }

    [Fact]
    public void AnArtifactForAnotherRequestOrWithAnotherReplayIsCaught()
    {
        var (revisions, artifacts) = Chain();
        var tampered = N(artifacts[1]!);
        tampered["provenance"]!["replay"] = JsonNode.Parse("""{"level": "not_guaranteed", "because": "provenance.solver"}""");
        var approving = Edited(revisions, 1, node => node["approved"]!["artifact"] = PlanRevision.Digest(V(tampered)));
        Assert.Contains("artifact_mismatch", Codes(approving.Take(2), [null, V(tampered)]));
        tampered["provenance"]!["request"] = JsonNode.Parse("""{"items": []}""");
        approving = Edited(revisions, 1, node => node["approved"]!["artifact"] = PlanRevision.Digest(V(tampered)));
        Assert.Contains("artifact_mismatch", Codes(approving.Take(2), [null, V(tampered)]));
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void AParentWhoseEventsLostTheirSequenceIsRefused(string sequence)
    {
        var (revisions, artifacts) = Chain();
        var parent = N(revisions[1]);
        parent["events"]![0]!["sequence"] = JsonNode.Parse(sequence);
        var error = Refused(() => PlanRevision.Derive(V(parent), artifacts[2]!, J($"[{Locked(sequence: 3)}]")));
        Assert.Equal(("invalid_revision", "a revision's events each carry an integer sequence"), (error.Code, error.Message));
    }

    [Fact]
    public void AnIntegralFloatIsTheIntegerItSpells()
    {
        var (revisions, artifacts) = Chain();
        var withFloat = PlanRevision.Derive(revisions[0], artifacts[1]!,
            J($$"""[{"sequence": 1.0, "type": "placement_locked", "placement": {{LockedPlacement}}}]"""));
        Assert.Equal(new JsonFloat(1.0), ((JsonObject)((JsonArray)withFloat.Require("events")).Items[0]).Require("sequence"));
        Assert.Equal(PlanRevision.CanonicalJson(PlanRevision.Derive(revisions[0], artifacts[1]!, J($"[{Locked()}]"))),
            PlanRevision.CanonicalJson(withFloat));
    }

    [Theory]
    [InlineData("""{"k": 1}""")]
    [InlineData("[5]")]
    [InlineData("\"x\"")]
    public void ARequestWhoseFixedPlacementsAreMalformedCannotBeEdited(string value)
    {
        var error = Refused(() => PlanRevision.ApplyEvents(Request().With("fixed_placements", J(value)), J($"[{Locked()}]")));
        Assert.Equal(("invalid_revision", "request.fixed_placements is a list of objects"), (error.Code, error.Message));
    }

    [Fact]
    public void NullFixedPlacementsAreNoneAndALockCreatesTheList() =>
        Assert.Single(((JsonArray)PlanRevision.ApplyEvents(Request().With("fixed_placements", JsonNull.Instance), J($"[{Locked()}]"))
            .Require("fixed_placements")).Items);

    [Theory]
    [InlineData("\"3\"")]
    [InlineData("2.5")]
    [InlineData("null")]
    public void AnItemQuantityThatIsNotAnIntegerCannotBeLowered(string quantity)
    {
        var request = N(Request());
        request["items"]![0]!["quantity"] = JsonNode.Parse(quantity);
        var error = Refused(() => PlanRevision.ApplyEvents(V(request),
            J("""[{"sequence": 1, "type": "item_missing", "item_type": "cube", "quantity": 1}]""")));
        Assert.Equal(("invalid_revision", "request.items[0].quantity is an integer"), (error.Code, error.Message));
    }

    [Fact]
    public void TheAuditRefusesWhatIsNotAChainOrAnArtifactList()
    {
        var (revisions, artifacts) = Chain();
        Assert.Equal("invalid_revision", Refused(() => PlanRevision.VerifyChain(
            new JsonObject([new KeyValuePair<string, JsonValue>("0", revisions[0])]))).Code);
        Assert.Equal("invalid_artifact", Refused(() => PlanRevision.VerifyChain(Chain(revisions),
            new JsonObject([new KeyValuePair<string, JsonValue>("1", artifacts[1]!)]))).Code);
        Assert.Equal(["artifact_mismatch"], Codes(revisions, [null, new JsonString("x"), artifacts[2]]));
        Assert.Equal(["artifact_mismatch"], Codes(revisions, [null, J("""{"n": 1152921504606846976}"""), artifacts[2]]));
    }

    [Fact]
    public void ABooleanSequenceInAChainIsCaught()
    {
        var (revisions, _) = Chain();
        Assert.Equal(["invalid_revision"], Codes(Edited(revisions, 1, node => node["events"]![0]!["sequence"] = true)));
    }

    [Fact]
    public void AValueIsQuotedByItsJsonSpelling()
    {
        Assert.Equal("unknown event type [\"placement_locked\"]", Refused(() => PlanRevision.ApplyEvents(Request(),
            J($$"""[{"sequence": 1, "type": ["placement_locked"], "placement": {{LockedPlacement}}}]"""))).Message);
        Assert.Equal("placement.position.x is a measure", Refused(() => PlanRevision.ApplyEvents(Request(),
            J("""[{"sequence": 1, "type": "placement_locked", "placement": {"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"x": true}}}]"""))).Message);
    }

    [Theory]
    [InlineData("[1]", "[]", "invalid_revision", "a request is a JSON object")]
    [InlineData(CubeRequest, """{"0": {}}""", "invalid_event", "events are a JSON array")]
    [InlineData(CubeRequest, """[{"type": "item_missing", "item_type": "cube", "quantity": 1}]""", "invalid_event", "event sequence null is not an integer")]
    public void ApplyRefusesWhatIsNotARequestOrAnEventList(string request, string events, string code, string message)
    {
        var error = Refused(() => PlanRevision.ApplyEvents(J(request), J(events)));
        Assert.Equal((code, message), (error.Code, error.Message));
    }

    [Fact]
    public void AValueWithNoJsonSpellingIsNamedByWhatItIs()
    {
        var (revisions, artifacts) = Chain();
        Assert.Equal("event sequence an out-of-range number does not continue the chain at 1", Refused(() =>
            PlanRevision.Derive(revisions[0], artifacts[1]!,
                J($$"""[{"sequence": 9007199254740992, "type": "placement_locked", "placement": {{LockedPlacement}}}]"""))).Message);
        Assert.Equal("the request has no item an unspellable value", Refused(() => PlanRevision.ApplyEvents(Request(),
            J("""[{"sequence": 1, "type": "item_missing", "item_type": "\ud800", "quantity": 1}]"""))).Message);
    }
}
