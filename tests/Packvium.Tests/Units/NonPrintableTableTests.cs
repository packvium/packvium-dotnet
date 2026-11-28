using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Units;

/// <summary>
/// The code points <c>repr</c> escapes come from a table generated off Python's Unicode 14.0.0
/// database, never from the runtime's own classification, which differs between .NET releases.
/// The digest pins the table to the one every other Packvium package prints from.
/// </summary>
public sealed class NonPrintableTableTests
{
    /// <summary>SHA-256 of the ranges written as <c>first-last</c> in lowercase hex, comma-separated, as uppercase hex.</summary>
    private const string Digest = "06C81E09F439B3815FD6D3A64E144F16D48739392841390A3DADB54FAF7B8EB4";

    [Fact]
    public void TheTableIsTheUnicodeFourteenTable()
    {
        var ranges = Enumerable.Range(0, NonPrintable.RangeCount).Select(NonPrintable.Range).ToList();
        Assert.Equal("14.0.0", NonPrintable.UnicodeVersion);
        Assert.Equal(700, ranges.Count);
        Assert.Equal(NonPrintable.CodePointCount, ranges.Sum(range => range.Last - range.First + 1));
        Assert.All(ranges, range => Assert.True(range.First <= range.Last));
        Assert.All(ranges.Zip(ranges.Skip(1)), pair => Assert.True(pair.First.Last + 1 < pair.Second.First));
        var written = string.Join(",", ranges.Select(range => string.Create(CultureInfo.InvariantCulture, $"{range.First:x}-{range.Last:x}")));
        Assert.Equal(Digest, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(written))));
    }

    [Theory]
    [InlineData(0x7F, true)]
    [InlineData(0xA0, true)]
    [InlineData(0xA1, false)]
    [InlineData(0xAD, true)]
    [InlineData(0x378, true)]
    [InlineData(0x200B, true)]
    [InlineData(0xD800, true)]
    [InlineData(0xDFFF, true)]
    [InlineData(0xE000, true)]
    [InlineData(0xE0001, true)]
    [InlineData(0x1F600, false)]
    [InlineData(0x10FFFF, true)]
    public void ProbesReadAsPythonReadsThem(int codePoint, bool escaped) => Assert.Equal(escaped, NonPrintable.Contains(codePoint));

    /// <summary><c>repr</c> walks UTF-16 itself: a pair is one code point, a lone surrogate prints as its escape.</summary>
    [Fact]
    public void ReprEscapesPerCodePoint()
    {
        Assert.Equal("'\\ud800x'", PythonText.Repr("\ud800x"));
        Assert.Equal("'\\udc00\\ud800'", PythonText.Repr("\udc00\ud800"));
        Assert.Equal("'\U0001F600'", PythonText.Repr("\U0001F600"));
        Assert.Equal("'\\x7f\\xa0\\xad'", PythonText.Repr("\u007F ­"));
    }
}
