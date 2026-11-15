using System.Collections.Generic;
using System.Numerics;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Serialization;

/// <summary>
/// RFC 8785 as the reference's <c>_canonical_json.py</c> writes it. Every expected spelling here
/// is the tag's Python output for the same value.
/// </summary>
public sealed class CanonicalJsonWriterTests
{
    private static string Write(JsonValue value) => CanonicalJsonWriter.Write(value);

    private static string Code(JsonValue value) => Assert.Throws<CanonicalJsonException>(() => Write(value)).Code;

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(100.0, "100")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.25, "0.25")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1.5e-6, "0.0000015")]
    [InlineData(2.5e-7, "2.5e-7")]
    [InlineData(1.23e-18, "1.23e-18")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(-0.0, "0")]
    [InlineData(4.35, "4.35")]
    [InlineData(0.3333333333333333, "0.3333333333333333")]
    [InlineData(123456789012345.6, "123456789012345.6")]
    [InlineData(9007199254740991.0, "9007199254740991")]
    public void FloatsAreWrittenAsEcmaScriptWritesThem(double value, string expected) =>
        Assert.Equal(expected, Write(new JsonFloat(value)));

    [Theory]
    [InlineData(9007199254740992.0)]
    [InlineData(1e21)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void AFloatBeyondWhatEveryEngineHoldsIsRefused(double value) =>
        Assert.Equal("number_out_of_range", Code(new JsonFloat(value)));

    [Fact]
    public void IntegersUpToTwoToTheFiftyThirdMinusOneAreWritten()
    {
        Assert.Equal("9007199254740991", Write(new JsonInteger(9007199254740991)));
        Assert.Equal("-9007199254740991", Write(new JsonInteger(-9007199254740991)));
        Assert.Equal("number_out_of_range", Code(new JsonInteger(9007199254740992)));
        Assert.Equal("number_out_of_range", Code(new JsonInteger(-BigInteger.Pow(2, 64))));
    }

    /// <summary>UTF-16 order puts an astral key (a high surrogate, U+D83D) before U+FFFD; code-point order would not.</summary>
    [Fact]
    public void KeysAreSortedByUtf16CodeUnits()
    {
        var value = JsonReader.Parse("{\"\uFFFD\":1,\"\U0001F600\":2,\"z\":3,\"a\":{},\"0\":[]}");
        Assert.Equal("{\"0\":[],\"a\":{},\"z\":3,\"\U0001F600\":2,\"\uFFFD\":1}", Write(value));
    }

    [Fact]
    public void OnlyQuotesBackslashesAndControlsAreEscaped() =>
        Assert.Equal("\"\u2028 \\u0000\\u001f\u007f\\b\\t\\n\\f\\r\\\"\\\\ \u00e9\U0001F4E6\"",
            Write(new JsonString("\u2028 \u0000\u001f\u007f\b\t\n\f\r\"\\ \u00e9\U0001F4E6")));

    /// <summary>Built from code units, because an attribute argument cannot carry a lone surrogate.</summary>
    [Fact]
    public void ALoneSurrogateIsRefused()
    {
        string[] texts = [new(['\uD800']), new(['a', '\uDC00']), new(['\uDE00', '\uD83D'])];
        foreach (var text in texts)
        {
            Assert.Equal("invalid_string", Code(new JsonString(text)));
            Assert.Equal("invalid_string", Code(new JsonObject([new KeyValuePair<string, JsonValue>(text, JsonNull.Instance)])));
        }
    }

    /// <summary>Python's <c>json</c> keeps an escaped lone surrogate, so the reader does too, and the writer refuses it.</summary>
    [Fact]
    public void AnEscapedLoneSurrogateIsReadAndThenRefused()
    {
        var value = (JsonObject)JsonReader.Parse("{\"bad\":\"\\ud800\",\"pair\":\"\\ud83d\\ude00\"}");
        Assert.Equal("\uD800", ((JsonString)value.Require("bad")).Value);
        Assert.Equal("\U0001F600", ((JsonString)value.Require("pair")).Value);
        Assert.Equal("invalid_string", Code(value));
    }

    [Fact]
    public void AnIntegerLiteralStaysAnIntegerAndAFractionAFloat()
    {
        var value = (JsonObject)JsonReader.Parse("{\"whole\":1.0,\"n\":9007199254740992,\"tiny\":1e-7}");
        Assert.IsType<JsonFloat>(value.Require("whole"));
        Assert.Equal(new BigInteger(9007199254740992), ((JsonInteger)value.Require("n")).Value);
        Assert.Equal("number_out_of_range", Code(value));
        Assert.Equal("{\"tiny\":1e-7,\"whole\":1}", Write(JsonReader.Parse("{\"whole\":1.0,\"tiny\":1e-7}")));
    }

    [Fact]
    public void LiteralsAndContainersHaveNoWhitespace() =>
        Assert.Equal("[null,true,false,[],{},\"\"]", Write(JsonReader.Parse("[ null , true , false , [ ] , { } , \"\" ]")));
}
