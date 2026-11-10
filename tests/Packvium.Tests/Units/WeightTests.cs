using System;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Units;

/// <summary>Expected values were produced by the reference engine at v1.0.0, not derived by hand.</summary>
public sealed class WeightTests
{
    [Theory]
    [InlineData("1 kg", 8_000_000_000)]
    [InlineData("2 lbs", 7_257_477_920)]
    [InlineData("3oz", 680_388_555)]
    [InlineData("5 mg", 40_000)]
    [InlineData("7 Kg", 56_000_000_000)]
    [InlineData("1/3", 2_666_667)]
    public void ParsesWhatTheReferenceAccepts(string text, long ticks) =>
        Assert.Equal(ticks, Weight.Parse(text).Ticks);

    [Theory]
    [InlineData(0, "0", "0", "0", "0")]
    [InlineData(1, "0.00000012", "0", "0", "0")]
    [InlineData(8_000_000, "1", "0.03527396", "0.00220462", "0.001")]
    [InlineData(226_796_185, "28.34952312", "1", "0.0625", "0.02834952")]
    [InlineData(123_456_789_123, "15432.09864038", "544.35126024", "34.02195377", "15.43209864")]
    public void RendersDecimalsAsTheReference(long ticks, string grams, string ounces, string pounds, string kilograms)
    {
        var weight = new Weight(ticks);
        Assert.Equal(grams, weight.DecimalText("g"));
        Assert.Equal(ounces, weight.DecimalText("oz"));
        Assert.Equal(pounds, weight.DecimalText("lb"));
        Assert.Equal(kilograms, weight.DecimalText("kg"));
    }

    [Fact]
    public void RefusesANegativeTickCount() => Assert.Throws<ArgumentException>(() => new Weight(-1));
}
