using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;
using Packvium.Packing;
using Packvium.Serialization;
using Packvium.Units;
using Packvium.Validation;
using Xunit;
using Json = Packvium.Serialization.JsonObject;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using Node = System.Text.Json.Nodes.JsonObject;
using NodeArray = System.Text.Json.Nodes.JsonArray;

namespace Packvium.Tests.Packing;

/// <summary>
/// Fixed placements: items already in a known place before the solve (<c>test_fixed_placements.py</c>).
/// Requests are written as <see cref="JsonNode"/> trees, so each case edits one field, and are
/// packed through the ordinary JSON entry point.
/// </summary>
public sealed class FixedPlacementTests
{
    private static Node Cube(int quantity, string weight = "1000") => new Node
    {
        ["id"] = "cube", ["quantity"] = quantity, ["weight"] = weight,
        ["dimensions"] = new Node { ["length"] = "100", ["width"] = "100", ["height"] = "100" },
    };

    private static Node Box(int quantity = 3) => new Node
    {
        ["id"] = "box", ["quantity"] = quantity,
        ["inner_dimensions"] = new Node { ["length"] = "200", ["width"] = "100", ["height"] = "200" },
    };

    private static Node Fixed(string x = "0", string y = "0", string z = "0", int instance = 1) =>
        new Node
        {
            ["item_type"] = "cube", ["container_type"] = "box", ["container_instance"] = instance,
            ["position"] = new Node { ["x"] = x, ["y"] = y, ["z"] = z },
            ["orientation"] = "LWH",
        };

    private static Node Request(JsonNode[]? items = null, JsonNode[]? containers = null,
        JsonNode[]? placements = null, JsonNode? configuration = null)
    {
        var data = new Node
        {
            ["items"] = new NodeArray(items ?? [Cube(5)]),
            ["containers"] = new NodeArray(containers ?? [Box()]),
            ["fixed_placements"] = new NodeArray(placements ?? [Fixed("100")]),
        };
        if (configuration is not null)
        {
            data["configuration"] = configuration;
        }
        return data;
    }

    private static Json Pack(JsonNode request) =>
        JsonApi.PackFromJson(JsonReader.Parse(request.ToJsonString()));

    private static string Refusal(JsonNode request) =>
        Assert.Throws<FixedPlacementException>(() => Pack(request)).Message;

    private static IEnumerable<Json> Containers(Json result) =>
        ((JsonArray)result.Require("containers")).Items.Cast<Json>();

    private static IEnumerable<Json> Placements(Json container) =>
        ((JsonArray)container.Require("placements")).Items.Cast<Json>();

    private static string Value(Json placement, string axis) =>
        ((JsonString)((Json)((Json)placement.Require("position")).Require(axis)).Require("value")).Value;

    private static string Text(Json record, string key) => ((JsonString)record.Require(key)).Value;

    private static List<(string, string, string, string, string)> FixedRows(Json result) =>
    [
        .. Containers(result).SelectMany(container => Placements(container)
            .Where(placement => placement.Get("fixed") is JsonBool { Value: true })
            .Select(placement => (Text(container, "id"), Text(placement, "item_id"), Value(placement, "x"),
                Value(placement, "y"), Value(placement, "z")))),
    ];

    private static bool HasWarnings(Json result) => result.Get("warnings") is JsonArray { Items.IsEmpty: false };

    [Theory]
    [InlineData("fast")]
    [InlineData("balanced")]
    [InlineData("quality")]
    [InlineData("exact_small")]
    public void AFixedItemIsReportedWhereTheRequestPutIt(string profile)
    {
        var result = Pack(Request(configuration: new Node { ["solver_profile"] = profile }));
        Assert.Contains(Text(result, "status"), (string[])["feasible", "optimal"]);
        Assert.Equal([("box#1", "cube#1", "100", "0", "0")], FixedRows(result));
        Assert.Equal(5, Containers(result).Sum(container => Placements(container).Count()));
    }

    [Fact]
    public void OnlyFixedPlacementsCarryTheFlag()
    {
        var flags = Containers(Pack(Request())).SelectMany(Placements).Select(placement => placement.Get("fixed")).ToList();
        Assert.Equal(1, flags.Count(flag => flag is JsonBool { Value: true }));
        Assert.Equal(4, flags.Count(flag => flag is null));
    }

    [Fact]
    public void ARequestWithoutFixedPlacementsAnswersAsBefore()
    {
        var absent = Request();
        absent.Remove("fixed_placements");
        var empty = Request(placements: []);
        Assert.Equal(WithoutDurations(Pack(absent)), WithoutDurations(Pack(empty)));
    }

    [Fact]
    public void FixedItemsTakeTheFirstInstancesOfTheirTypeInListedOrder() =>
        Assert.Equal([("box#1", "cube#1", "100", "0", "0"), ("box#1", "cube#2", "0", "0", "0")],
            FixedRows(Pack(Request(placements: [Fixed("100"), Fixed("0")]))));

    [Fact]
    public void FixedContainersOpenFirstAndFreeOnesAreNumberedAfterThem()
    {
        var ids = Containers(Pack(Request(items: [Cube(9)], placements: [Fixed(), Fixed(instance: 2)])))
            .Select(container => Text(container, "id")).ToList();
        Assert.Equal(["box#1", "box#2", "box#3"], ids);
    }

    [Fact]
    public void AFixedContainerIsKeptWhenNoFreeItemFitsInIt()
    {
        var tray = new Node
        {
            ["id"] = "tray", ["quantity"] = 1,
            ["inner_dimensions"] = new Node { ["length"] = "100", ["width"] = "100", ["height"] = "100" },
        };
        var entry = Fixed();
        entry["container_type"] = "tray";
        var result = Pack(Request(items: [Cube(3)], containers: [tray, Box()], placements: [entry]));
        var first = Containers(result).First();
        Assert.Equal("tray#1", Text(first, "id"));
        Assert.Equal(["cube#1"], Placements(first).Select(placement => Text(placement, "item_id")));
    }

    [Fact]
    public void AFixedContainerSurvivesASearchThatRunsOutOfEffort()
    {
        var configuration = JsonNode.Parse("""{"effort_budget": {"max_search_nodes": 1}}""");
        var result = Pack(Request(items: [Cube(40)], containers: [Box(20)], placements: [Fixed(), Fixed(instance: 2)],
            configuration: configuration));
        Assert.Equal(["box#1", "box#2"], FixedRows(result).Select(row => row.Item1).Distinct().Order(StringComparer.Ordinal));
        Assert.False(HasWarnings(result));
    }

    [Fact]
    public void AFreeItemRestingOnAFixedOneLoadsIt()
    {
        var container = Box(1);
        container["inner_dimensions"]!["length"] = "100";
        var result = Pack(Request(items: [Cube(2)], containers: [container], placements: [Fixed()],
            configuration: JsonNode.Parse("""{"solvers": ["extreme_points"]}""")));
        var byId = Placements(Containers(result).Single()).ToDictionary(placement => Text(placement, "item_id"));
        Assert.Equal("100", Value(byId["cube#2"], "z"));
        Assert.Equal("1000", Text((Json)byId["cube#1"].Require("top_load"), "value"));
    }

    [Fact]
    public void FixedWeightCountsAgainstPayload()
    {
        var container = Box(2);
        container["max_payload"] = "1000";
        var result = Pack(Request(items: [Cube(3)], containers: [container], placements: [Fixed()]));
        Assert.Equal([1, 1], Containers(result).Select(packed => Placements(packed).Count()));
        var unpacked = ((JsonArray)result.Require("unpacked_items")).Items.Cast<Json>()
            .Select(item => Text(item, "item_id")).ToList();
        Assert.True(unpacked.SequenceEqual(["cube#2"]) || unpacked.SequenceEqual(["cube#3"]), string.Join(",", unpacked));
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("homogeneous_blocks")]
    [InlineData("maximal_spaces")]
    [InlineData("layer")]
    public void EverySolverPacksAroundFixedItems(string solver)
    {
        var result = Pack(Request(configuration: JsonNode.Parse($$"""{"solvers": ["{{solver}}"]}""")));
        Assert.Equal([("box#1", "cube#1", "100", "0", "0")], FixedRows(result));
        Assert.False(HasWarnings(result));
        Assert.Equal(5, Containers(result).Sum(container => Placements(container).Count()));
    }

    [Fact]
    public void TheLatticeStillServesContainersWithoutFixedItems()
    {
        var result = Pack(Request(items: [Cube(12)], containers: [Box(4)],
            configuration: JsonNode.Parse("""{"solver_profile": "fast", "require_placement_coordinates": false}""")));
        var containers = Containers(result).ToList();
        Assert.True(Placements(containers[0]).First().Get("fixed") is JsonBool { Value: true });
        Assert.Contains(containers.Skip(1), container => container.Contains("lattice_summary"));
        Assert.False(HasWarnings(result));
    }

    /// <summary>The edits of the reference's parametrised case, by name.</summary>
    private static readonly Dictionary<string, Action<Node>> Edits = new(StringComparer.Ordinal)
    {
        ["second-at-the-same-place"] = d => Placement(d).Parent!.AsArray().Add(Fixed("100")),
        ["unknown-item"] = d => Placement(d)["item_type"] = "crate",
        ["unknown-container"] = d => Placement(d)["container_type"] = "crate",
        ["instance-gap"] = d => Placement(d)["container_instance"] = 2,
        ["beyond-the-wall"] = d => Placement(d)["position"]!["x"] = "150",
        ["more-than-requested"] = d =>
        {
            d["items"]![0]!["quantity"] = 1;
            Placement(d).Parent!.AsArray().Add(Fixed());
        },
        ["forbidden-orientation"] = d =>
        {
            d["items"]![0]!["keep_upright"] = true;
            Placement(d)["orientation"] = "HWL";
        },
        ["obstacle"] = d => d["containers"]![0]!["obstacles"] = JsonNode.Parse(
            """[{"id": "pillar", "origin": {"x": "150"}, "dimensions": {"length": "10", "width": "10", "height": "10"}}]"""),
        ["payload"] = d => d["containers"]![0]!["max_payload"] = "500",
        ["clearance"] = d => d["configuration"] = JsonNode.Parse("""{"clearance": "1"}"""),
        ["support"] = d =>
        {
            d["configuration"] = JsonNode.Parse("""{"minimum_support_ratio": 1}""");
            Placement(d)["position"]!["z"] = "50";
        },
        ["inventory"] = d =>
        {
            d["containers"]![0]!["quantity"] = 1;
            Placement(d).Parent!.AsArray().Add(Fixed(instance: 2));
        },
        ["max-containers"] = d =>
        {
            d["configuration"] = JsonNode.Parse("""{"max_containers": 1}""");
            Placement(d).Parent!.AsArray().Add(Fixed(instance: 2));
        },
    };

    private static JsonNode Placement(Node d) => d["fixed_placements"]![0]!;

    public static TheoryData<string, string> CannotHold() => new()
    {
        { "second-at-the-same-place", "collision" },
        { "unknown-item", "unknown item type" },
        { "unknown-container", "unknown container type" },
        { "instance-gap", "[2] are not numbered 1..1" },
        { "beyond-the-wall", "outside_container" },
        { "more-than-requested", "2 cube fixed, 1 requested" },
        { "forbidden-orientation", "orientation HWL" },
        { "obstacle", "obstacle_collision" },
        { "payload", "payload_exceeded" },
        { "clearance", "outside_container" },
        { "support", "insufficient_support" },
        { "inventory", "2 box named, 1 available" },
        { "max-containers", "max_containers is 1" },
    };

    [Theory]
    [MemberData(nameof(CannotHold))]
    public void AFixedSetThatCannotHoldIsRefusedBeforeSearch(string edit, string fragment)
    {
        var data = Request();
        Edits[edit](data);
        var message = Refusal(data);
        Assert.StartsWith("invalid_fixed_placement: ", message, StringComparison.Ordinal);
        Assert.Contains(fragment, message, StringComparison.Ordinal);
    }

    private static (Item[] Items, Container[] Containers) DomainRequest(int quantity = 5, int boxes = 3,
        string weight = "1000") =>
        ([new Item("cube", Dimensions.Mm("100", "100", "100"), Weight.Of(weight), quantity)],
         [new Container("box", Dimensions.Mm("200", "100", "200"), quantity: boxes)]);

    private static FixedPlacement At(long xMm, long yMm, long zMm, int instance = 1) =>
        new("cube", "box", new Point(xMm * Length.TicksPerMm, yMm * Length.TicksPerMm, zMm * Length.TicksPerMm), Rotation.LWH,
            instance);

    [Fact]
    public void TheValidatorCatchesAMovedFixedItem()
    {
        var (items, containers) = DomainRequest();
        var packed = new Packer(new PackingConfig()).Pack(items, containers, [At(100, 0, 0)]);
        var moved = new PackingRequest(items, containers, [At(0, 0, 0)]);
        var report = IndependentSolutionValidator.Validate(moved, packed.Containers, 0.0, null, packed.Unpacked);
        Assert.Equal(["fixed_placement_moved", "unexpected_fixed_placement"],
            report.Issues.Select(issue => issue.Code).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheValidatorCatchesAMissingFixedContainer()
    {
        var (items, containers) = DomainRequest();
        var packed = new Packer(new PackingConfig()).Pack(items, containers);
        var wanted = new PackingRequest(items, containers, [At(0, 0, 0, instance: 3)]);
        var report = IndependentSolutionValidator.Validate(wanted, packed.Containers);
        Assert.Contains("fixed_container_missing", report.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void RebalancingNeverMovesAFixedItem()
    {
        var (items, containers) = DomainRequest(quantity: 5, boxes: 2, weight: "5000");
        FixedPlacement[] fixedItems = [At(0, 0, 0), At(100, 0, 0), At(0, 0, 100), At(0, 0, 0, instance: 2)];
        var config = new PackingConfig();
        var packed = new Packer(config).Pack(items, containers, fixedItems);
        var model = new PackingRequest(items, containers, fixedItems);
        var rebalanced = WeightRebalance.Rebalance(model, packed.Containers, packed.Unpacked, config);
        Assert.Equal(2, packed.Containers.Length);
        Assert.DoesNotContain(rebalanced.Moves, move => move.ItemId is "cube#1" or "cube#2" or "cube#3" or "cube#4");
        var report = IndependentSolutionValidator.Validate(model, rebalanced.Containers, 0.0, null, packed.Unpacked);
        Assert.True(report.Valid, string.Join(", ", report.Issues.Select(issue => issue.Code)));
    }

    public static TheoryData<string, string> Malformed() => new()
    {
        { "\"x\"", "fixed_placements is a list" },
        { "[5]", "fixed_placements[0] is an object" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "note": "strapped"}]""", "fixed_placements[0] cannot carry [\"note\"]" },
        { """[{"item_type": "cube", "container_type": "box"}]""", "fixed_placements[0] needs [\"orientation\"]" },
        { """[{"item_type": "", "container_type": "box", "orientation": "LWH"}]""", "fixed_placements[0].item_type is a non-empty string" },
        { """[{"item_type": "cube", "container_type": 5, "orientation": "LWH"}]""", "fixed_placements[0].container_type is a non-empty string" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "XYZ"}]""", "fixed_placements[0].orientation must be one of [\"LWH\",\"LHW\",\"WLH\",\"WHL\",\"HLW\",\"HWL\"]" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "container_instance": "1"}]""", "fixed_placements[0].container_instance counts from 1" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "container_instance": true}]""", "fixed_placements[0].container_instance counts from 1" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "container_instance": 1.5}]""", "fixed_placements[0].container_instance counts from 1" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "container_instance": 9007199254740992}]""", "fixed_placements[0].container_instance counts from 1" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": ["100", "0", "0"]}]""", "fixed_placements[0].position is a point object" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": null}]""", "fixed_placements[0].position is a point object" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"w": "5"}}]""", "fixed_placements[0].position cannot carry [\"w\"]" },
        { """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"y": true}}]""", "fixed_placements[0].position.y is a measure" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AFixedPlacementThatIsNotTheSchemasShapeIsRefusedNotCoerced(string placements, string detail)
    {
        var data = Request();
        data["fixed_placements"] = JsonNode.Parse(placements);
        Assert.Equal($"invalid_fixed_placement: {detail}", Refusal(data));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""[{"item_type": "cube", "container_type": "box", "container_instance": 1.0, "position": {"x": "100"}, "orientation": "LWH"}]""")]
    public void NullFixedPlacementsAndAnIntegralFloatInstanceAreAccepted(string placements)
    {
        var data = Request();
        data["fixed_placements"] = JsonNode.Parse(placements);
        Assert.NotEqual("error", Text(Pack(data), "status"));
    }

    [Fact]
    public void AnUnknownItemTypeIsQuotedAsJson()
    {
        var entry = Fixed();
        entry["item_type"] = "cr\"ate";
        Assert.Equal("invalid_fixed_placement: unknown item type \"cr\\\"ate\"", Refusal(Request(placements: [entry])));
    }

    /// <summary>A result without its wall-clock field, the only part a clock may change.</summary>
    internal static string WithoutDurations(Json result) => CanonicalJsonWriter.Write(Strip(result));

    private static JsonValue Strip(JsonValue value) => value switch
    {
        Json obj => new Json(obj.Members.Where(member => member.Key != "duration_ms")
            .Select(member => new KeyValuePair<string, JsonValue>(member.Key, Strip(member.Value)))),
        JsonArray array => new JsonArray([.. array.Items.Select(Strip)]),
        _ => value,
    };
}
