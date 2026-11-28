using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;
using Packvium.Validation;
using Packvium.Validation.ResultChecks;
using Xunit;

namespace Packvium.Tests.Validation;

/// <summary>
/// <see cref="ResultValidation.Validate"/>, the reference validator. Issue-for-issue agreement with
/// the reference over the whole corpus is the conformance run's job; these pin the entry point
/// and the Python semantics the issue details depend on.
/// </summary>
public sealed class ResultValidationTests
{
    private static readonly JsonValue Request = JsonReader.Parse("""
        {"items": [{"id": "cube", "quantity": 2, "dimensions": {"length": "10", "width": "10", "height": "10"}}],
         "containers": [{"id": "box", "inner_dimensions": {"length": "20", "width": "10", "height": "10"}}]}
        """);

    private static readonly string[] IntTexts = ["2.7", "-2.7", "true", "1e19", "\" 1_2 \""];

    private static readonly string[] SizedTexts = ["[1, 2]", "{\"a\": 1, \"b\": 2}", "\"a\\ud83d\\ude00b\""];

    [Fact]
    public void AResultTheEnginePackedIsValid() =>
        Assert.Empty(ResultValidation.Validate(Request, JsonApi.PackFromJson(Request)));

    [Fact]
    public void TwoItemsInOnePlaceAreACollisionNamedAsTheReferenceNamesIt()
    {
        var result = JsonApi.PackFromJson(Request);
        var first = Placement(result, 0);
        var moved = WithPlacement(result, 1, placement => placement.With("position", first.Require("position")));
        var issue = Assert.Single(ResultValidation.Validate(Request, moved));
        Assert.Equal("collision: cube#1 with cube#2 in box#1", issue.ToString());
    }

    /// <summary>The audit's scene: B rests on exactly half its base at a ratio that rounds down to 500000 ppm.</summary>
    [Fact]
    public void TheAuditReproductionIsPackedAndValid()
    {
        var request = JsonReader.Parse("""
            {"configuration": {"minimum_support_ratio": 0.5000001, "solver_profile": "fast"},
             "items": [
               {"id": "A", "quantity": 1, "priority": 10, "allowed_rotations": ["LWH"], "dimensions": {"length": "100", "width": "50", "height": "50"}},
               {"id": "B", "quantity": 1, "allowed_rotations": ["LWH"], "dimensions": {"length": "100", "width": "100", "height": "50"}}],
             "containers": [{"id": "box", "inner_dimensions": {"length": "100", "width": "100", "height": "100"}}]}
            """);
        var result = JsonApi.PackFromJson(request);
        Assert.Equal(new JsonString("feasible"), result.Require("status"));
        Assert.Empty(ResultValidation.Validate(request, result));
    }

    /// <summary>
    /// Support is judged by the solvers' ppm rule, <c>floor(base * ppm / 10^6)</c> with
    /// <c>ppm = floor(ratio * 10^6 + 0.5)</c> in doubles, not by the exact rational. On a 10^7 tick
    /// base each ratio's ppm floor is admitted and one tick less is refused.
    /// </summary>
    [Theory]
    [InlineData(0.5000001, 5_000_000)]
    [InlineData(0.0001245, 1_240)]
    [InlineData(0.9999996, 10_000_000)]
    [InlineData(0.25, 2_500_000)]
    public void SupportIsJudgedByTheSolversPpmRule(double ratio, int threshold)
    {
        Assert.Empty(InsufficientSupport(ratio, threshold));
        Assert.Single(InsufficientSupport(ratio, threshold - 1));
    }

    [Fact]
    public void InsufficientSupportPrintsBothRatiosToSixPlaces() =>
        Assert.Equal(["upper#1: 0.500000 < 0.500000"], InsufficientSupport(0.5000001, 4_999_999));

    [Fact]
    public void AResultOfTheWrongShapeIsRefusedRatherThanJudged()
    {
        var refusal = Assert.Throws<MalformedResultException>(() => ResultValidation.Validate(Request, JsonReader.Parse("""{"containers": "none"}""")));
        Assert.Equal(MalformedResultException.Contract, refusal.Kind);
        Assert.Equal("result is missing 'status'", refusal.Message);
    }

    [Fact]
    public void AMissingKeyOutsideTheContractIsAShapeRefusal()
    {
        var result = JsonApi.PackFromJson(Request);
        var broken = WithPlacement(result, 0, placement => new JsonObject(placement.Members.Where(member => member.Key != "item_id")));
        Assert.Equal(MalformedResultException.Shape, Assert.Throws<MalformedResultException>(() => ResultValidation.Validate(Request, broken)).Kind);
    }

    /// <summary>A tick past 64 bits is judged exactly, as the reference judges it, rather than refused.</summary>
    [Fact]
    public void IntegersPastSixtyFourBitsAreJudgedExactly()
    {
        var result = JsonApi.PackFromJson(Request);
        var far = BigInteger.Pow(2, 70);
        var moved = WithPlacement(result, 0, placement =>
        {
            var position = (JsonObject)placement.Require("position");
            return placement.With("position", position.With("x", ((JsonObject)position.Require("x")).With("ticks", new JsonInteger(far))));
        });
        var issues = ResultValidation.Validate(Request, moved);
        Assert.Contains(new ResultIssue("outside_container", "cube#1 in box#1"), issues);
    }

    /// <summary>The reference reads a packed container's id only to name it in an issue.</summary>
    [Fact]
    public void AContainerWithoutAnIdIsJudgedUntilAnIssueMustNameIt()
    {
        var result = JsonApi.PackFromJson(Request);
        var anonymous = WithContainer(result, container => new JsonObject(container.Members.Where(member => member.Key != "id")));
        Assert.Empty(ResultValidation.Validate(Request, anonymous));
        var moved = WithContainer(WithPlacement(result, 1, placement => placement.With("position", Placement(result, 0).Require("position"))),
            container => new JsonObject(container.Members.Where(member => member.Key != "id")));
        Assert.Equal(MalformedResultException.Shape, Assert.Throws<MalformedResultException>(() => ResultValidation.Validate(Request, moved)).Kind);
    }

    /// <summary>A detail is the same text whatever culture the caller runs under.</summary>
    [Fact]
    public void DetailsIgnoreTheCallersCulture()
    {
        var request = JsonReader.Parse("""
            {"items": [{"id": "cube", "quantity": 1, "must_be_on_floor": true, "dimensions": {"length": "10", "width": "10", "height": "10"}}],
             "containers": [{"id": "box", "inner_dimensions": {"length": "20", "width": "10", "height": "10"}}]}
            """);
        var sunk = WithPlacement(JsonApi.PackFromJson(request), 0, placement =>
        {
            var position = (JsonObject)placement.Require("position");
            return placement.With("position", position.With("z", Tick(-16_000)));
        });
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "\u2212";
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            Assert.Contains(new ResultIssue("floor_violation", "cube#1 at z=-16000"), ResultValidation.Validate(request, sunk));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AnIssueReadsAsItsCodeAndDetail() =>
        Assert.Equal("collision: a#1 with a#2", new ResultIssue("collision", "a#1 with a#2").ToString());

    [Theory]
    [InlineData("1_000", 1000, 1)]
    [InlineData(".5", 1, 2)]
    [InlineData("5.", 5, 1)]
    [InlineData("1e3", 1000, 1)]
    [InlineData("1E-2", 1, 100)]
    [InlineData(" 3/16 ", 3, 16)]
    [InlineData("+.5e1", 5, 1)]
    [InlineData("1_0.0_1", 1001, 100)]
    [InlineData("-0.25", -1, 4)]
    public void FractionsParseAsPythonReadsThem(string text, long numerator, long denominator)
    {
        var ratio = Fractions.Parse(text);
        Assert.Equal((new BigInteger(numerator), new BigInteger(denominator)), (ratio.Numerator, ratio.Denominator));
    }

    [Theory]
    [InlineData("1 / 2")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1/0")]
    [InlineData("None")]
    [InlineData("1e99999")]
    [InlineData("6/-1")]
    public void FractionsRefuseWhatPythonRefuses(string text) =>
        Assert.Equal(MalformedResultException.Shape, Assert.Throws<MalformedResultException>(() => Fractions.Parse(text)).Kind);

    [Theory]
    [InlineData("12 3/8 in", 152_400 + 12 * 406_400)]
    [InlineData("-1 1/2 mm", -24_000)]
    [InlineData("10 CM", 1_600_000)]
    [InlineData("0.00003125", 0)]
    [InlineData("0.00009375", 2)]
    public void MeasuresRoundHalfToEven(string measure, long ticks) =>
        Assert.Equal(new BigInteger(ticks), TickUnits.ToTicks(new JsonString(measure), "mm", TickUnits.Length));

    /// <summary>Python lowers the Kelvin sign to an ASCII <c>k</c>, so <c>2 \u212Ag</c> reads as two kilograms.</summary>
    [Fact]
    public void AUnitSuffixLowersAsPythonLowersIt() =>
        Assert.Equal(new BigInteger(2 * TickUnits.TicksPerKilogram), TickUnits.ToTicks(new JsonString("2 \u212Ag"), "g", TickUnits.Weight));

    [Theory]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e15, "1000000000000000.0")]
    public void FloatsPrintAsPythonPrintsThem(double value, string expected) =>
        Assert.Equal(expected, PyFormat.Repr(new JsonFloat(value)));

    [Fact]
    public void ValuesPrintAsPythonPrintsThem()
    {
        Assert.Equal("\"it's\"", PyFormat.StringRepr("it's"));
        Assert.Equal("'a\\nb\\\\'", PyFormat.StringRepr("a\nb\\"));
        Assert.Equal("'\\x07é\\u200b'", PyFormat.StringRepr("\u0007é\u200b"));
        Assert.Equal("'\\U000e0001\\u0600'", PyFormat.StringRepr("\U000e0001\u0600"));
        Assert.Equal("'\\ud800'", PyFormat.StringRepr("\ud800"));
        Assert.Equal("{'a': [1, None, True], 'b': 'x'}", PyFormat.Repr(JsonReader.Parse("""{"a": [1, null, true], "b": "x"}""")));
        Assert.Equal("(7,)", PyFormat.IntTuple([7]));
        Assert.Equal("(1, 2, 3)", PyFormat.IntTuple([1, 2, 3]));
    }

    [Theory]
    [InlineData(0.0078125, "0.007812")]
    [InlineData(0.0234375, "0.023438")]
    [InlineData(2.5e-6, "0.000003")]
    [InlineData(-1e-7, "-0.000000")]
    [InlineData(1234.5, "1234.500000")]
    public void SixPlacesRoundTheExactBinaryValueHalfToEven(double value, string expected) =>
        Assert.Equal(expected, PyFormat.Fixed6(value));

    [Fact]
    public void FloatConversionIsCorrectlyRounded()
    {
        Assert.Equal(2.5e-6, Ratio.Of(5, 2_000_000).ToDouble());
        Assert.Equal(Math.Pow(2, 60), Ratio.Integer((BigInteger.One << 60) + 1).ToDouble());
        Assert.Equal((double)((1L << 54) + 4), Ratio.Integer((BigInteger.One << 54) + 3).ToDouble());
        Assert.Equal(1.0 / 3.0, Ratio.Of(BigInteger.Parse("300000000000000000000", CultureInfo.InvariantCulture),
            BigInteger.Parse("900000000000000000000", CultureInfo.InvariantCulture)).ToDouble());
        Assert.Equal(0.1, Ratio.Of(1, 10).ToDouble());
        Assert.Equal(-0.75, Ratio.Of(3, -4).ToDouble());
        Assert.Equal(3.0e-300, Ratio.Of(3, BigInteger.Pow(10, 300)).ToDouble());
    }

    /// <summary>The ppm rule reads the ratio as a double: <c>0.0001245</c> is 124 ppm where the exact decimal rounds to 125.</summary>
    [Fact]
    public void PartsPerMillionFollowTheDouble()
    {
        Assert.Equal(new BigInteger(124), PartsPerMillion.Of(Fractions.Parse("0.0001245")));
        Assert.Equal(new BigInteger(500_000), PartsPerMillion.Of(Fractions.Parse("0.5000001")));
    }

    [Fact]
    public void JsonValuesCompareAsPythonComparesThem()
    {
        Assert.Equal(1, Json.Compare(Parse("[1, 2]"), Parse("[1]")));
        Assert.Equal(-1, Json.Compare(Parse("[1]"), Parse("[1, 2]")));
        Assert.Equal(-1, Json.Compare(Parse("1.5"), Parse("2")));
        Assert.Equal(1, Json.Compare(Parse("\"\\ud83d\\ude00\""), Parse("\"\\uffff\"")));
        Assert.True(Json.PyEquals(Parse("[1, [2.0]]"), Parse("[true, [2]]")));
        Assert.True(Json.PyEquals(Parse("{\"a\": 1, \"b\": 2}"), Parse("{\"b\": 2.0, \"a\": true}")));
        Assert.False(Json.PyEquals(Parse("[1, 2]"), Parse("[1, 3]")));
        Assert.False(Json.PyEquals(Parse("\"1\""), Parse("1")));
        Assert.Equal(Json.HashKey(Parse("1")), Json.HashKey(Parse("true")));
        Assert.Equal(Json.HashKey(Parse("1")), Json.HashKey(Parse("1.0")));
        Assert.NotEqual(Json.HashKey(Parse("1")), Json.HashKey(Parse("\"1\"")));
        Assert.Equal([new BigInteger(2), -2, 1, BigInteger.Pow(10, 19), 12],
            IntTexts.Select(text => Json.PyInt(Parse(text), "x")));
        Assert.Equal([2, 2, 3], SizedTexts.Select(text => Json.Length(Parse(text), "x")));
    }

    [Fact]
    public void WhatPythonCannotDoIsAShapeRefusal()
    {
        var refusals = new Action[]
        {
            () => Json.Compare(Parse("[1]"), Parse("\"a\"")),
            () => Json.HashKey(Parse("[1]")),
            () => Json.PyInt(Parse("[]"), "x"),
            () => Json.Get(Parse("5"), "x"),
            () => Json.Items(Parse("5"), "x"),
            () => Json.Text(Parse("1"), "x"),
            () => Exact.FloorDiv(1, 0),
            () => Geometry.Axes("XYZ"),
            () => Geometry.RotateHull([], [0, 1, 2]),
            () => TickUnits.Length.PerUnit("furlong"),
            () => Json.Sorted([new JsonInteger(1), new JsonString("a")]),
        };
        Assert.All(refusals, refusal => Assert.Equal(MalformedResultException.Shape, Assert.Throws<MalformedResultException>(refusal).Kind));
    }

    [Fact]
    public void IntegerArithmeticFloorsAsPythonDoes()
    {
        Assert.Equal(new BigInteger(-4), Exact.FloorDiv(-7, 2));
        Assert.Equal(new BigInteger(2), Exact.FloorMod(-7, 3));
        Assert.Equal(new BigInteger(4), Exact.CeilDiv(7, 2));
        Assert.Equal(new BigInteger(-3), Exact.FloorDiv(7, -3));
    }

    /// <summary>An odd permutation is mirrored back into a proper rotation, then translated to start at the origin.</summary>
    [Fact]
    public void AHullRotatesWithoutMirroring() =>
        Assert.Equal([new TickVector(1, 0, 0), new TickVector(1, 2, 0), new TickVector(0, 0, 0)],
            Geometry.RotateHull([new TickVector(0, 0, 0), new TickVector(2, 0, 0), new TickVector(0, 1, 0)], Geometry.Axes("WLH")));

    [Fact]
    public void AHullVolumeIsExact()
    {
        var cube = new Cuboid(0, 0, 0, 2, 3, 4).Vertices();
        Assert.Equal(new BigInteger(24), Geometry.HullVolume(cube));
        Assert.Equal(new BigInteger(1), Geometry.HullVolume([new TickVector(0, 0, 0), new TickVector(6, 0, 0), new TickVector(0, 1, 0), new TickVector(0, 0, 1)]));
    }

    private static JsonValue Parse(string text) => JsonReader.Parse(text);

    /// <summary>The <c>insufficient_support</c> details for an upper bar resting on <paramref name="supported"/> ticks of the lower one.</summary>
    private static List<string> InsufficientSupport(double ratio, int supported)
    {
        const int Base = 10_000_000;
        var bar = $$"""{"length": "1", "width": "{{Base}}", "height": "1"}""";
        var request = (JsonObject)JsonReader.Parse($$$"""
            {"units": {"length": "tick"},
             "items": [
               {"id": "lower", "quantity": 1, "allowed_rotations": ["LWH"], "dimensions": {{{bar}}}},
               {"id": "upper", "quantity": 1, "allowed_rotations": ["LWH"], "dimensions": {{{bar}}}}],
             "containers": [{"id": "box", "inner_dimensions": {"length": "1", "width": "{{{2 * Base}}}", "height": "2"}}]}
            """);
        var floor = JsonApi.PackFromJson(request);
        var placements = ((JsonArray)((JsonObject)((JsonArray)floor.Require("containers")).Items[0]).Require("placements")).Items;
        var upper = placements.Select((placement, index) => (placement, index))
            .First(found => ((JsonObject)found.placement).Require("item_type") == new JsonString("upper")).index;
        var raised = WithPlacement(floor, upper, placement =>
        {
            var position = (JsonObject)placement.Require("position");
            return placement.With("position", position
                .With("y", Tick(Base - supported))
                .With("z", Tick(1)));
        });
        var configured = request.With("configuration", JsonReader.Parse(
            $$"""{"minimum_support_ratio": {{ratio.ToString("R", CultureInfo.InvariantCulture)}}}"""));
        return [.. ResultValidation.Validate(configured, raised).Where(issue => issue.Code == "insufficient_support").Select(issue => issue.Detail)];
    }

    private static JsonObject Tick(long ticks) => new([
        new KeyValuePair<string, JsonValue>("ticks", new JsonInteger(ticks)),
        new KeyValuePair<string, JsonValue>("value", new JsonString(ticks.ToString(CultureInfo.InvariantCulture))),
        new KeyValuePair<string, JsonValue>("unit", new JsonString("tick")),
    ]);

    private static JsonObject Placement(JsonObject result, int index) =>
        (JsonObject)((JsonArray)((JsonObject)((JsonArray)result.Require("containers")).Items[0]).Require("placements")).Items[index];

    private static JsonObject WithContainer(JsonObject result, Func<JsonObject, JsonObject> change)
    {
        var containers = ((JsonArray)result.Require("containers")).Items;
        return result.With("containers", new JsonArray(containers.SetItem(0, change((JsonObject)containers[0]))));
    }

    private static JsonObject WithPlacement(JsonObject result, int index, Func<JsonObject, JsonObject> change) =>
        WithContainer(result, container =>
        {
            var placements = ((JsonArray)container.Require("placements")).Items;
            return container.With("placements", new JsonArray(placements.SetItem(index, change((JsonObject)placements[index]))));
        });
}
