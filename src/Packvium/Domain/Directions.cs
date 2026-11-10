using System;
using System.Collections.Immutable;

namespace Packvium.Domain;

/// <summary>A direction outside the six-value vocabulary, refused rather than read as one of the six.</summary>
public sealed class InvalidDirectionException(string direction)
    : ArgumentException($"unknown movement direction '{direction}'; expected one of ('+x', '-x', '+y', '-y', '+z', '-z')")
{
    public const string Code = "invalid_direction";

    public string Direction { get; } = direction;
}

/// <summary>A region between a box face and a container wall, as half-open bounds.</summary>
public readonly record struct Sweep(long X1, long Y1, long Z1, long X2, long Y2, long Z2)
{
    /// <summary>Does <paramref name="box"/> stand anywhere inside this region? Touching a face does not count.</summary>
    public bool Intersects(AxisAlignedBox box) =>
        X1 < box.X2 && box.Origin.X < X2 && Y1 < box.Y2 && box.Origin.Y < Y2 && Z1 < box.Z2 && box.Origin.Z < Z2;
}

public static class Directions
{
    /// <summary>
    /// The six faces a box can leave a container through, in a fixed order: callers pick the
    /// first clear one, and a set would make that choice depend on hash order.
    /// </summary>
    public static ImmutableArray<string> All { get; } = ["+x", "-x", "+y", "-y", "+z", "-z"];

    /// <summary>
    /// The region between <paramref name="box"/>'s own face and the container wall along
    /// <paramref name="direction"/> -- identical for unloading through that wall and loading
    /// through it.
    /// </summary>
    public static Sweep SweptVolume(AxisAlignedBox box, Dimensions container, string direction)
    {
        var sweep = new Sweep(box.Origin.X, box.Origin.Y, box.Origin.Z, box.X2, box.Y2, box.Z2);
        return direction switch
        {
            "+x" => sweep with { X1 = sweep.X2, X2 = container.Length.Ticks },
            "-x" => sweep with { X2 = sweep.X1, X1 = 0 },
            "+y" => sweep with { Y1 = sweep.Y2, Y2 = container.Width.Ticks },
            "-y" => sweep with { Y2 = sweep.Y1, Y1 = 0 },
            "+z" => sweep with { Z1 = sweep.Z2, Z2 = container.Height.Ticks },
            "-z" => sweep with { Z2 = sweep.Z1, Z1 = 0 },
            _ => throw new InvalidDirectionException(direction),
        };
    }
}
