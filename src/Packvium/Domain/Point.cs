using System;

namespace Packvium.Domain;

/// <summary>A non-negative position in a container's tick frame, ordered x, then y, then z.</summary>
public readonly record struct Point : IComparable<Point>
{
    public Point(long x, long y, long z)
    {
        if (Math.Min(x, Math.Min(y, z)) < 0)
        {
            throw new ArgumentException("coordinates cannot be negative");
        }
        X = x;
        Y = y;
        Z = z;
    }

    public long X { get; }

    public long Y { get; }

    public long Z { get; }

    public static Point Origin => default;

    public Vertex AsVertex => new(X, Y, Z);

    /// <summary>The reference engine's spelling of a point, in ticks.</summary>
    public override string ToString() => $"Point(x={X}, y={Y}, z={Z})";

    public int CompareTo(Point other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
        {
            return x;
        }
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    public static bool operator <(Point left, Point right) => left.CompareTo(right) < 0;

    public static bool operator >(Point left, Point right) => left.CompareTo(right) > 0;

    public static bool operator <=(Point left, Point right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Point left, Point right) => left.CompareTo(right) >= 0;
}
