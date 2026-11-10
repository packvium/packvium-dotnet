using System.Numerics;
using Packvium.Serialization;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Units;

/// <summary>Expected values were produced by the reference engine's Python at v1.0.0.</summary>
public sealed class FloatRenderingTests
{
    [Theory]
    [InlineData(0.0078125, "0.007812")]
    [InlineData(0.5, "0.500000")]
    [InlineData(1.0 / 3, "0.333333")]
    [InlineData(2.0 / 3, "0.666667")]
    [InlineData(0.9999995, "1.000000")]
    [InlineData(1e-7, "0.000000")]
    public void FormatsSixPlacesTiesToEven(double value, string expected) =>
        Assert.Equal(expected, DecimalRendering.FormatFixed(value, 6));

    [Theory]
    [InlineData("1", "3", 0.3333333333333333)]
    [InlineData("1208925819614629174706177", "12157665459056928801", 99437.33224819344)]
    [InlineData("1000000000000000000000000000000", "7", 1.4285714285714285e+29)]
    public void DividesIntegersToTheNearestDouble(string numerator, string denominator, double expected) =>
        Assert.Equal(expected, DecimalRendering.TrueDivide(BigInteger.Parse(numerator), BigInteger.Parse(denominator)));

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(123.0, "123.0")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(12345678901234567.0, "1.2345678901234568e+16")]
    [InlineData(-0.0, "-0.0")]
    public void ReprMatchesPython(double value, string expected) => Assert.Equal(expected, PythonFloat.Repr(value));
}
