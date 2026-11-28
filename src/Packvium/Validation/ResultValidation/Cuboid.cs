using System.Collections.Immutable;
using System.Numerics;

namespace Packvium.Validation.ResultChecks;

/// <summary>A point in ticks; a hull vertex or a separating axis.</summary>
internal readonly record struct TickVector(BigInteger X, BigInteger Y, BigInteger Z)
{
    public static TickVector Zero { get; } = new(0, 0, 0);

    public BigInteger this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

    public static TickVector operator -(TickVector left, TickVector right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static TickVector operator +(TickVector left, TickVector right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public TickVector Negate() => new(-X, -Y, -Z);

    public BigInteger Dot(TickVector other) => X * other.X + Y * other.Y + Z * other.Z;

    public TickVector Cross(TickVector other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

    /// <summary>Lexicographic order, as Python orders tuples.</summary>
    public int CompareTo(TickVector other)
    {
        var order = X.CompareTo(other.X);
        if (order == 0)
        {
            order = Y.CompareTo(other.Y);
        }
        return order == 0 ? Z.CompareTo(other.Z) : order;
    }
}

/// <summary>An axis-aligned box in ticks; its far corner and base area are computed once.</summary>
internal sealed record Cuboid(BigInteger X, BigInteger Y, BigInteger Z, BigInteger Length, BigInteger Width, BigInteger Height)
{
    public BigInteger X2 { get; } = X + Length;

    public BigInteger Y2 { get; } = Y + Width;

    public BigInteger Z2 { get; } = Z + Height;

    public BigInteger BaseArea { get; } = Length * Width;

    public bool Intersects(Cuboid other) =>
        X < other.X2 && other.X < X2 && Y < other.Y2 && other.Y < Y2 && Z < other.Z2 && other.Z < Z2;

    public bool Contains(Cuboid other) =>
        X <= other.X && Y <= other.Y && Z <= other.Z && other.X2 <= X2 && other.Y2 <= Y2 && other.Z2 <= Z2;

    public BigInteger OverlapAreaXy(Cuboid other)
    {
        var dx = BigInteger.Max(0, BigInteger.Min(X2, other.X2) - BigInteger.Max(X, other.X));
        var dy = BigInteger.Max(0, BigInteger.Min(Y2, other.Y2) - BigInteger.Max(Y, other.Y));
        return dx * dy;
    }

    /// <summary>The eight corners, in the reference's order.</summary>
    public ImmutableArray<TickVector> Vertices()
    {
        var corners = ImmutableArray.CreateBuilder<TickVector>(8);
        foreach (var farX in new[] { false, true })
        {
            foreach (var farY in new[] { false, true })
            {
                foreach (var farZ in new[] { false, true })
                {
                    corners.Add(new TickVector(farX ? X2 : X, farY ? Y2 : Y, farZ ? Z2 : Z));
                }
            }
        }
        return corners.MoveToImmutable();
    }
}
