using System;

namespace Packvium.Domain;

/// <summary>
/// A point in an item's local tick frame. Deliberately not <see cref="Point"/>, which forbids
/// negative coordinates: a hull is authored around whatever origin its author chose, and is
/// only moved into container coordinates at placement time.
/// </summary>
public readonly record struct Vertex(long X, long Y, long Z) : IComparable<Vertex>
{
    public int CompareTo(Vertex other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
        {
            return x;
        }
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    public static bool operator <(Vertex left, Vertex right) => left.CompareTo(right) < 0;

    public static bool operator >(Vertex left, Vertex right) => left.CompareTo(right) > 0;

    public static bool operator <=(Vertex left, Vertex right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Vertex left, Vertex right) => left.CompareTo(right) >= 0;

    public static Vertex operator -(Vertex left, Vertex right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
}

/// <summary>
/// A direction in the integer lattice. Components are 128-bit because they are cross products
/// of tick differences, which leave the 64-bit range long before a hull reaches its coordinate
/// cap.
/// </summary>
public readonly record struct Axis(Int128 X, Int128 Y, Int128 Z) : IComparable<Axis>
{
    public static Axis Of(Vertex vertex) => new(vertex.X, vertex.Y, vertex.Z);

    public bool IsZero => X == 0 && Y == 0 && Z == 0;

    public Axis Negated => new(-X, -Y, -Z);

    public Int128 Dot(Vertex point) => checked(point.X * X + point.Y * Y + point.Z * Z);

    public Int128 Dot(Axis other) => checked(other.X * X + other.Y * Y + other.Z * Z);

    public Axis Cross(Axis right) => checked(new(
        Y * right.Z - Z * right.Y,
        Z * right.X - X * right.Z,
        X * right.Y - Y * right.X));

    public int CompareTo(Axis other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
        {
            return x;
        }
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    public static bool operator <(Axis left, Axis right) => left.CompareTo(right) < 0;

    public static bool operator >(Axis left, Axis right) => left.CompareTo(right) > 0;

    public static bool operator <=(Axis left, Axis right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Axis left, Axis right) => left.CompareTo(right) >= 0;
}
