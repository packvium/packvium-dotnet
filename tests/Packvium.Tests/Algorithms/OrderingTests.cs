using System.Linq;
using Packvium.Algorithms;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Algorithms;

/// <summary>Expected values were produced by the reference engine at v1.0.0, not derived by hand.</summary>
public sealed class OrderingTests
{
    [Fact]
    public void DrawsTheReferenceSequence()
    {
        var random = new DeterministicRandom(42);
        Assert.Equal([8L, 1, 9, 4, 1, 9, 1, 7], Enumerable.Range(0, 8).Select(_ => random.NextInt(10)).ToArray());
    }

    [Fact]
    public void ShufflesAsTheReference() =>
        Assert.Equal([7, 0, 2, 1, 4, 5, 6, 3, 9, 8], new DeterministicRandom(42).Shuffled(Enumerable.Range(0, 10)).ToArray());

    [Fact]
    public void ReducesANegativeSeedAsPythonDoes()
    {
        var random = new DeterministicRandom(-7);
        Assert.Equal([411L, 359, 289, 417], Enumerable.Range(0, 4).Select(_ => random.NextInt(1000)).ToArray());
    }

    [Fact]
    public void ComparesStringsByCodePointNotUtf16Unit()
    {
        // U+1F600 is above U+FFFD by code point, below it by UTF-16 code unit.
        Assert.True(SortKey.Of("�") < SortKey.Of("\U0001F600"));
        Assert.True(CodePointComparer.Instance.Compare("�", "\U0001F600") < 0);
    }

    [Fact]
    public void ComparesIntegersAndDoublesByValueAndTuplesByPrefix()
    {
        Assert.True(SortKey.Of(double.NegativeInfinity) < SortKey.Of(-5.0));
        Assert.True(SortKey.Of(1, 2.5) < SortKey.Of(1, 3));
        Assert.True(SortKey.Of(1) < SortKey.Of(1, 0));
        Assert.Equal(SortKey.Of(2L), SortKey.Of(2));
    }

    [Theory]
    [InlineData("ab", "'ab'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("both ' \"", "'both \\' \"'")]
    [InlineData("tab\there", "'tab\\there'")]
    [InlineData("back\\slash", "'back\\\\slash'")]
    [InlineData("é", "'é'")]
    [InlineData("​", "'\\u200b'")]
    [InlineData("\u007f", "'\\x7f'")]
    [InlineData("\U0001F600", "'\U0001F600'")]
    [InlineData("\U000E0001", "'\\U000e0001'")]
    public void QuotesAsPythonRepr(string text, string expected) => Assert.Equal(expected, PythonText.Repr(text));
}
