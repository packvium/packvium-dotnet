using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Packvium.Units;

namespace Packvium.Domain;

/// <summary>A box's three positive edge lengths.</summary>
public sealed record Dimensions
{
    public Dimensions(Length length, Length width, Length height)
    {
        if (Math.Min(length.Ticks, Math.Min(width.Ticks, height.Ticks)) <= 0)
        {
            throw new ArgumentException("all dimensions must be positive");
        }
        Length = length;
        Width = width;
        Height = height;
    }

    public Length Length { get; }

    public Length Width { get; }

    public Length Height { get; }

    public static Dimensions Of(string length, string width, string height, string unit = "mm",
        Rounding rounding = Rounding.Nearest) =>
        new(Units.Length.Of(length, unit, rounding), Units.Length.Of(width, unit, rounding),
            Units.Length.Of(height, unit, rounding));

    public static Dimensions Mm(string length, string width, string height, Rounding rounding = Rounding.Nearest) =>
        Of(length, width, height, "mm", rounding);

    public static Dimensions Inches(string length, string width, string height, Rounding rounding = Rounding.Nearest) =>
        Of(length, width, height, "in", rounding);

    public static Dimensions OfTicks(long length, long width, long height) =>
        new(new Length(length), new Length(width), new Length(height));

    /// <summary>Cubic ticks. 128-bit: a one-metre cube is already 4.096 x 10^21.</summary>
    public Int128 Volume => checked((Int128)Length.Ticks * Width.Ticks * Height.Ticks);

    public Int128 BaseArea => (Int128)Length.Ticks * Width.Ticks;

    public long MaxEdge => Math.Max(Length.Ticks, Math.Max(Width.Ticks, Height.Ticks));

    public Dimensions Rotated(Rotation rotation) => rotation switch
    {
        Rotation.LWH => this,
        Rotation.LHW => new(Length, Height, Width),
        Rotation.WLH => new(Width, Length, Height),
        Rotation.WHL => new(Width, Height, Length),
        Rotation.HLW => new(Height, Length, Width),
        _ => new(Height, Width, Length),
    };

    /// <summary>Allowed rotations with distinct rotated extents, first spelling of each kept, in order.</summary>
    public ImmutableArray<(Rotation Rotation, Dimensions Dimensions)> UniqueRotations(IEnumerable<Rotation> allowed)
    {
        var seen = new HashSet<(long, long, long)>();
        var result = ImmutableArray.CreateBuilder<(Rotation, Dimensions)>();
        foreach (var rotation in allowed)
        {
            var dimensions = Rotated(rotation);
            if (seen.Add((dimensions.Length.Ticks, dimensions.Width.Ticks, dimensions.Height.Ticks)))
            {
                result.Add((rotation, dimensions));
            }
        }
        return result.ToImmutable();
    }

    public bool FitsInside(Dimensions other) =>
        Length.Ticks <= other.Length.Ticks && Width.Ticks <= other.Width.Ticks && Height.Ticks <= other.Height.Ticks;

    public Dimensions Expand(Length clearance)
    {
        var margin = checked(clearance.Ticks * 2);
        return OfTicks(checked(Length.Ticks + margin), checked(Width.Ticks + margin), checked(Height.Ticks + margin));
    }
}
