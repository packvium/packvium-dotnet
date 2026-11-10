using System;

namespace Packvium.Domain;

/// <summary>A half-open box: two boxes that share only a face do not intersect.</summary>
public sealed record AxisAlignedBox(Point Origin, Dimensions Dimensions)
{
    public long X2 => Origin.X + Dimensions.Length.Ticks;

    public long Y2 => Origin.Y + Dimensions.Width.Ticks;

    public long Z2 => Origin.Z + Dimensions.Height.Ticks;

    public bool Intersects(AxisAlignedBox other) =>
        !(X2 <= other.Origin.X || other.X2 <= Origin.X || Y2 <= other.Origin.Y || other.Y2 <= Origin.Y
          || Z2 <= other.Origin.Z || other.Z2 <= Origin.Z);

    public bool Contains(AxisAlignedBox other) =>
        Origin.X <= other.Origin.X && Origin.Y <= other.Origin.Y && Origin.Z <= other.Origin.Z
        && other.X2 <= X2 && other.Y2 <= Y2 && other.Z2 <= Z2;

    public bool ContainsPoint(Point point) =>
        Origin.X <= point.X && point.X < X2 && Origin.Y <= point.Y && point.Y < Y2
        && Origin.Z <= point.Z && point.Z < Z2;

    public Int128 OverlapAreaXy(AxisAlignedBox other)
    {
        var dx = Math.Max(0, Math.Min(X2, other.X2) - Math.Max(Origin.X, other.Origin.X));
        var dy = Math.Max(0, Math.Min(Y2, other.Y2) - Math.Max(Origin.Y, other.Origin.Y));
        return (Int128)dx * dy;
    }
}
