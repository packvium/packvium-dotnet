using System;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Units;

/// <summary>Expected values were produced by the reference engine at v1.0.0, not derived by hand.</summary>
public sealed class LengthTests
{
    [Theory]
    [InlineData("12", 192_000)]
    [InlineData("12.5", 200_000)]
    [InlineData("3/16", 3_000)]
    [InlineData("12 3/8", 198_000)]
    [InlineData(" 1_0 ", 160_000)]
    [InlineData("١٢", 192_000)]
    [InlineData("1e1", 160_000)]
    [InlineData(".5", 8_000)]
    [InlineData("5.", 80_000)]
    [InlineData("12 3/8 in", 5_029_200)]
    [InlineData("10cm", 1_600_000)]
    [InlineData("5 inches", 2_032_000)]
    [InlineData("1 ft", 4_876_800)]
    [InlineData("100 ticks", 100)]
    [InlineData("0.000000000000000001", 0)]
    [InlineData("1/3", 5_333)]
    public void ParsesWhatTheReferenceAccepts(string text, long ticks) =>
        Assert.Equal(ticks, Length.Parse(text).Ticks);

    [Theory]
    [InlineData("-0 1/2", typeof(ArgumentException))]
    [InlineData("0.0000000000000000001", typeof(ArgumentException))]
    [InlineData("nan", typeof(ArgumentException))]
    [InlineData("2K", typeof(ArithmeticException))]
    [InlineData("1 2", typeof(ArithmeticException))]
    [InlineData("1.2.3", typeof(ArithmeticException))]
    [InlineData("abc", typeof(ArithmeticException))]
    [InlineData("inf", typeof(OverflowException))]
    [InlineData("1/0", typeof(DivideByZeroException))]
    public void RefusesWhatTheReferenceRefuses(string text, Type error) =>
        Assert.Throws(error, () => Length.Parse(text));

    [Theory]
    [InlineData(Rounding.Floor, new long[] { 0, 1, 2, 2 })]
    [InlineData(Rounding.Ceil, new long[] { 1, 2, 3, 3 })]
    [InlineData(Rounding.Nearest, new long[] { 0, 2, 2, 2 })]
    public void RoundsHalvesAndThirdsAsTheReference(Rounding rounding, long[] expected)
    {
        string[] values = ["1/2", "3/2", "5/2", "7/3"];
        Assert.Equal(expected, Array.ConvertAll(values, value => Length.Of(value, "tick", rounding).Ticks));
    }

    [Theory]
    [InlineData(0, "0", "0", "0", "0")]
    [InlineData(1, "0.0000625", "0.00000246", "0.00000021", "0.00000006")]
    [InlineData(16_000, "1", "0.03937008", "0.00328084", "0.001")]
    [InlineData(406_400, "25.4", "1", "0.08333333", "0.0254")]
    [InlineData(123_456_789, "7716.0493125", "303.781469", "25.31512242", "7.71604931")]
    [InlineData(7, "0.0004375", "0.00001722", "0.00000144", "0.00000044")]
    [InlineData(1_000_000_000_000_000_000, "62500000000000", "2460629921259.84251969", "205052493438.32020997",
        "62500000000")]
    public void RendersDecimalsAsTheReference(long ticks, string mm, string inches, string feet, string metres)
    {
        var length = new Length(ticks);
        Assert.Equal(mm, length.DecimalText("mm"));
        Assert.Equal(inches, length.DecimalText("in"));
        Assert.Equal(feet, length.DecimalText("ft"));
        Assert.Equal(metres, length.DecimalText("m"));
    }

    [Fact]
    public void RefusesANegativeTickCount() => Assert.Throws<ArgumentException>(() => new Length(-1));

    [Fact]
    public void RefusesAnUnknownUnit() => Assert.Throws<ArgumentException>(() => Length.Of("1", "furlong"));

    [Fact]
    public void ReadsAUnitCaseInsensitively() => Assert.Equal(16_000, Length.Of("1", " MM ").Ticks);
}
