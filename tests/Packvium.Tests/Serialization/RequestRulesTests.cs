using System;
using System.Collections.Generic;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Units;
using Xunit;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using Node = System.Text.Json.Nodes.JsonObject;

namespace Packvium.Tests.Serialization;

/// <summary>Structured request errors: one type, a closed reason, and the pointer of the bad value (<c>test_request_errors.py</c>).</summary>
public sealed class RequestRulesTests
{
    private static Node Request() => (Node)JsonNode.Parse("""
        {"items": [{"id": "cube", "quantity": 2, "weight": "1000",
                    "dimensions": {"length": "100", "width": "100", "height": "100"}}],
         "containers": [{"id": "box", "quantity": 2,
                         "inner_dimensions": {"length": "200", "width": "100", "height": "200"}}],
         "configuration": {"solver_profile": "fast"}}
        """)!;

    private static JsonValue Parse(JsonNode request) => JsonReader.Parse(request.ToJsonString());

    private static T Refusal<T>(Action<Node> edit) where T : InvalidRequestException
    {
        var data = Request();
        edit(data);
        return Assert.Throws<T>(() => JsonApi.PackFromJson(Parse(data)));
    }

    private static JsonNode Item(Node d) => d["items"]![0]!;

    private static JsonNode Container(Node d) => d["containers"]![0]!;

    private static JsonNode Width(string json) => JsonNode.Parse(json)!;

    /// <summary>The reference's edits, by name.</summary>
    private static readonly Dictionary<string, Action<Node>> Edits = new(StringComparer.Ordinal)
    {
        ["quantity-zero"] = d => Item(d)["quantity"] = 0,
        ["quantity-text"] = d => Item(d)["quantity"] = "2",
        ["quantity-beyond-exact"] = d => Item(d)["quantity"] = 9_007_199_254_740_992L,
        ["quantity-far-below"] = d => Item(d)["quantity"] = -(1L << 60),
        ["width-negative"] = d => Item(d)["dimensions"]!["width"] = "-1",
        ["width-unknown-unit"] = d => Item(d)["dimensions"]!["width"] = Width("""{"value": "1", "unit": "furlong"}"""),
        ["width-without-value"] = d => Item(d)["dimensions"]!["width"] = Width("""{"unit": "mm"}"""),
        ["width-fraction"] = d => Item(d)["dimensions"]!["width"] = Width("1.5"),
        ["height-missing"] = d => Item(d)["dimensions"]!.AsObject().Remove("height"),
        ["id-repeated"] = d => d["items"]!.AsArray().Add(Item(d).DeepClone()),
        ["support-above-one"] = d => Item(d)["minimum_support_ratio"] = 2,
        ["support-text"] = d => Item(d)["minimum_support_ratio"] = "x",
        ["compression-negative"] = d => Item(d)["compression_ratio"] = -1,
        ["tag-limit-escaped"] = d => Container(d)["tag_limits"] = Width("""{"a/b~c": 0}"""),
        ["price-negative"] = d => Container(d)["rate_table"] = Width("""{"prices_minor": [1, -2]}"""),
        ["obstacle-origin-negative"] = d => Container(d)["obstacles"] = Width(
            """[{"id": "o", "origin": {"z": "-1"}, "dimensions": {"length": "1", "width": "1", "height": "1"}}]"""),
        ["units-number"] = d => d["units"] = Width("""{"length": 5}"""),
        ["items-not-list"] = d => d["items"] = Width("""{"cube": 1}"""),
        ["item-not-object"] = d => d["items"]!.AsArray().Add("slab"),
        ["id-number"] = d => Item(d)["id"] = 7,
        ["width-unparsable"] = d => Item(d)["dimensions"]!["width"] = "ten",
        ["width-value-list"] = d => Item(d)["dimensions"]!["width"] = Width("""{"value": [1]}"""),
        ["width-unit-number"] = d => Item(d)["dimensions"]!["width"] = Width("""{"value": "1", "unit": 3}"""),
        ["brackets-not-list"] = d => Container(d)["rate_table"] = Width("""{"weight_brackets_g": 5}"""),
        ["minimum-charge-negative"] = d => Container(d)["rate_table"] = Width("""{"minimum_charge_minor": -1}"""),
        ["obstacle-height-missing"] = d => Container(d)["obstacles"] = Width(
            """[{"id": "o", "dimensions": {"length": "1", "width": "1"}}]"""),
        ["profile-unknown"] = d => d["configuration"]!["solver_profile"] = "fastest",
        ["restarts-zero"] = d => d["configuration"]!["effort_budget"] = Width("""{"max_restarts": 0}"""),
        ["configuration-keys-unknown"] = d =>
        {
            d["configuration"]!["top_k"] = 2;
            d["configuration"]!["profile"] = "balanced";
        },
        ["effort-key-unknown"] = d => d["configuration"]!["effort_budget"] = Width("""{"max_nodes": 1}"""),
        ["parallel"] = d => d["configuration"]!["parallel"] = true,
        ["objective-unknown"] = d => d["configuration"]!["objective"] = "cheapest",
        ["access-direction-unknown"] = d => Container(d)["access_directions"] = Width("""["upwards"]"""),
    };

    public static TheoryData<string, string, string, string> Malformed() => new()
    {
        { "quantity-zero", "below_minimum", "/items/0/quantity", "must be at least 1" },
        { "quantity-text", "wrong_type", "/items/0/quantity", "must be an integer" },
        { "quantity-beyond-exact", "above_maximum", "/items/0/quantity", "must be at most 9007199254740991" },
        { "quantity-far-below", "below_minimum", "/items/0/quantity", "must be at least 1" },
        { "width-negative", "negative_measure", "/items/0/dimensions/width", "cannot be negative" },
        { "width-unknown-unit", "invalid_unit", "/items/0/dimensions/width", "has an unknown unit \"furlong\"" },
        { "width-without-value", "wrong_type", "/items/0/dimensions/width", "must be a measure" },
        { "width-fraction", "wrong_type", "/items/0/dimensions/width", "must be a measure" },
        { "height-missing", "missing_field", "/items/0/dimensions/height", "is required" },
        { "id-repeated", "duplicate_id", "/items/1/id", "repeats the id \"cube\"" },
        { "support-above-one", "above_maximum", "/items/0/minimum_support_ratio", "must be at most 1" },
        { "support-text", "wrong_type", "/items/0/minimum_support_ratio", "must be a number" },
        { "compression-negative", "below_minimum", "/items/0/compression_ratio", "must be at least 0" },
        { "tag-limit-escaped", "below_minimum", "/containers/0/tag_limits/a~1b~0c", "must be at least 1" },
        { "price-negative", "below_minimum", "/containers/0/rate_table/prices_minor/1", "must be at least 0" },
        { "obstacle-origin-negative", "negative_measure", "/containers/0/obstacles/0/origin/z", "cannot be negative" },
        { "units-number", "invalid_unit", "/units/length", "has an unknown unit 5" },
        { "items-not-list", "wrong_type", "/items", "must be a list" },
        { "item-not-object", "wrong_type", "/items/1", "must be an object" },
        { "id-number", "wrong_type", "/items/0/id", "must be a string" },
        { "width-unparsable", "wrong_type", "/items/0/dimensions/width", "must be a measure" },
        { "width-value-list", "wrong_type", "/items/0/dimensions/width", "must be a measure" },
        { "width-unit-number", "invalid_unit", "/items/0/dimensions/width", "has an unknown unit 3" },
        { "brackets-not-list", "wrong_type", "/containers/0/rate_table/weight_brackets_g", "must be a list" },
        { "minimum-charge-negative", "below_minimum", "/containers/0/rate_table/minimum_charge_minor", "must be at least 0" },
        { "obstacle-height-missing", "missing_field", "/containers/0/obstacles/0/dimensions/height", "is required" },
        { "profile-unknown", "not_allowed", "/configuration/solver_profile", "must be one of [\"fast\",\"balanced\",\"quality\",\"exact_small\"]" },
        { "restarts-zero", "below_minimum", "/configuration/effort_budget/max_restarts", "must be at least 1" },
        { "configuration-keys-unknown", "not_allowed", "/configuration/profile", "is not a known field" },
        { "effort-key-unknown", "not_allowed", "/configuration/effort_budget/max_nodes", "is not a known field" },
        { "parallel", "not_allowed", "/configuration/parallel", "is not a known field" },
        { "objective-unknown", "not_allowed", "/configuration/objective",
            "must be one of [\"default\",\"lowest_cost\",\"shipping_cost\",\"lowest_landed_cost\",\"open_dimension_height\",\"maximum_value\"]" },
        { "access-direction-unknown", "not_allowed", "/containers/0/access_directions/0",
            "must be one of [\"+x\",\"-x\",\"+y\",\"-y\",\"+z\",\"-z\"]" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedRequestNamesTheValueAndTheRule(string edit, string reason, string field, string detail)
    {
        var error = Refusal<InvalidRequestException>(Edits[edit]);
        Assert.Equal(("invalid_request", reason, field, detail), (error.Code, error.Reason, error.Field, error.Detail));
        Assert.Equal($"invalid_request: {field}: {detail}", error.Message);
        Assert.Contains(error.Reason, InvalidRequestException.Reasons);
    }

    [Fact]
    public void TheErrorIsStillAnArgumentExceptionForExistingCallers() =>
        Assert.IsAssignableFrom<ArgumentException>(Refusal<InvalidRequestException>(d => Item(d)["quantity"] = 0));

    [Fact]
    public void ARequestThatIsNotAnObjectIsRefusedAsAWhole()
    {
        var error = Assert.Throws<InvalidRequestException>(() =>
            JsonApi.PackFromJson(JsonReader.Parse("[" + Request().ToJsonString() + "]")));
        Assert.Equal((string.Empty, "invalid_request: must be an object"), (error.Field, error.Message));
    }

    [Fact]
    public void WhatTheRulesDoNotNameStillArrivesAsARequestError()
    {
        var error = Refusal<InvalidRequestException>(d => Item(d)["shape_type"] = "blob");
        Assert.Equal(("invalid_request", "invalid_value", string.Empty), (error.Code, error.Reason, error.Field));
    }

    [Theory]
    [InlineData("items", "tags", "[[1]]", "list")]
    [InlineData("items", "incompatible_tags", "[\"a\", {\"k\": 1}]", "dict")]
    [InlineData("items", "eligible_container_tags", "[1, []]", "list")]
    [InlineData("containers", "tags", "[{}]", "dict")]
    public void AListOrObjectTagIsUnhashableAsInPython(string record, string key, string tags, string type)
    {
        var error = Refusal<InvalidRequestException>(d => d[record]![0]![key] = JsonNode.Parse(tags));
        Assert.Equal(("invalid_value", string.Empty, $"invalid_request: unhashable type: '{type}'"),
            (error.Reason, error.Field, error.Message));
    }

    [Fact]
    public void AScalarNonStringTagIsStillRefusedByThePort()
    {
        var error = Refusal<InvalidRequestException>(d => Item(d)["tags"] = JsonNode.Parse("[1]"));
        Assert.Equal("invalid_request: a int member is not accepted where a string is expected", error.Message);
    }

    [Fact]
    public void AFixedPlacementRefusalIsARequestErrorWithItsOwnCode()
    {
        var error = Refusal<FixedPlacementException>(d => d["fixed_placements"] = Width(
            """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"x": "-5"}}]"""));
        Assert.Equal(("invalid_fixed_placement", "malformed", "/fixed_placements/0/position/x"), (error.Code, error.Reason, error.Field));
        Assert.Equal("invalid_fixed_placement: fixed_placements[0].position.x cannot be negative", error.Message);
    }

    [Fact]
    public void AFixedSetThatCannotHoldIsReportedAgainstTheWholeList()
    {
        const string entry = """{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"x": "0"}}""";
        var error = Refusal<FixedPlacementException>(d => d["fixed_placements"] = Width($"[{entry}, {entry}]"));
        Assert.Equal(("cannot_hold", "/fixed_placements"), (error.Reason, error.Field));
    }

    [Fact]
    public void NullOptionalsAreAbsentAndAValidRequestPasses()
    {
        var data = Request();
        Item(data)["max_stacked_items"] = null;
        Item(data)["stop_index"] = null;
        data["units"] = null;
        data["configuration"]!["effort_budget"] = null;
        RequestRules.Check(Parse(data));
        Assert.NotNull(JsonApi.PackFromJson(Parse(data)));
    }

    [Fact]
    public void APointerEscapesTildeAndSlash() =>
        Assert.Equal("/containers/0/tag_limits/a~1b~0c", JsonPointer.Of("containers", 0, "tag_limits", "a/b~c"));

    [Fact]
    public void AUnitsObjectWithoutALengthKeepsMillimetres()
    {
        var data = Request();
        data["units"] = new Node();
        RequestRules.Check(Parse(data));
    }

    [Fact]
    public void AnUnparsableFixedCoordinateIsNamed()
    {
        var error = Refusal<FixedPlacementException>(d => d["fixed_placements"] = Width(
            """[{"item_type": "cube", "container_type": "box", "orientation": "LWH", "position": {"y": {"value": [1]}}}]"""));
        Assert.Equal("invalid_fixed_placement: fixed_placements[0].position.y is a measure", error.Message);
    }

    public static TheoryData<string, string> TypedGuards() => new()
    {
        { "max_containers", "max_containers must be at least 1" },
        { "max_candidate_points", "max_candidate_points must be at least 16" },
        { "minimum_support_ratio", "minimum_support_ratio must be between 0 and 1" },
        { "dimensional_weight_divisor", "dimensional_weight_divisor must be positive" },
        { "time_limit_ms", "positive configuration values required" },
        { "effort_budget", "effort_budget.max_search_nodes must be at least 1" },
    };

    /// <summary>A caller who builds the model in code never passes the JSON rule table.</summary>
    [Theory]
    [MemberData(nameof(TypedGuards))]
    public void TheTypedApiKeepsItsOwnGuards(string field, string message)
    {
        Action build = field switch
        {
            "max_containers" => () => _ = new PackingConfig(maxContainers: 0),
            "max_candidate_points" => () => _ = new PackingConfig(maxCandidatePoints: 15),
            "minimum_support_ratio" => () => _ = new PackingConfig(minimumSupportRatio: 2),
            "dimensional_weight_divisor" => () => _ = new PackingConfig(dimensionalWeightDivisor: 0),
            "time_limit_ms" => () => _ = new PackingConfig(timeLimitMs: 0),
            _ => () => _ = new EffortBudget(MaxSearchNodes: 0),
        };
        Assert.Equal(message, Assert.Throws<ArgumentException>(build).Message);
    }

    [Fact]
    public void AnIntegralFloatIsAWholeMeasureAndAFractionIsRefused()
    {
        Assert.Equal(Measures.Length(new JsonInteger(100), "mm"), Measures.Length(new JsonFloat(100.0), "mm"));
        Assert.Equal(Measures.Weight(new JsonInteger(5)), Measures.Weight(new JsonFloat(5.0)));
        Assert.Throws<ArgumentException>(() => Measures.Length(new JsonFloat(1.5), "mm"));
        Assert.Throws<ArgumentException>(() => Measures.Weight(new JsonFloat(1.5)));
    }

    [Fact]
    public void AnEmptyTagLimitsObjectIsAccepted()
    {
        var data = Request();
        Container(data)["tag_limits"] = new Node();
        RequestRules.Check(Parse(data));
    }

    /// <summary>The known configuration keys are the schema's, where the suite's schema is present.</summary>
    [Fact]
    public void TheKnownConfigurationFieldsAreTheSchemas()
    {
        var schemaPath = System.IO.Path.Combine(Tests.Conformance.ConformanceTests.CorpusDirectory(), "conformance", "schema",
            "packing-request.schema.json");
        if (!System.IO.File.Exists(schemaPath))
        {
            Assert.Skip(Tests.Conformance.ConformanceTests.CorpusAbsent(schemaPath));
        }
        var configuration = JsonNode.Parse(System.IO.File.ReadAllText(schemaPath))!["properties"]!["configuration"]!;
        Assert.False(configuration["additionalProperties"]!.GetValue<bool>());
        var data = Request();
        foreach (var (name, _) in configuration["properties"]!.AsObject())
        {
            data["configuration"]![name] = null;
        }
        data["configuration"]!["solver_profile"] = "fast";
        var budget = new Node();
        foreach (var (name, _) in configuration["properties"]!["effort_budget"]!["properties"]!.AsObject())
        {
            budget[name] = 1_000_000;
        }
        data["configuration"]!["effort_budget"] = budget;
        RequestRules.Check(Parse(data));
        Assert.NotNull(JsonApi.PackFromJson(Parse(data)));
    }
}
