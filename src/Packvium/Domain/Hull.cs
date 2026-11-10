using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

/// <summary>A flat or duplicated vertex set: no interior, so it is refused rather than repaired.</summary>
public sealed class DegenerateHullException(string message) : ArgumentException(message);

/// <summary>
/// Exact separating-axis geometry for <c>convex_hull</c> items, fixed by
/// <c>docs/IRREGULAR-ITEMS.md</c> and ported from the reference engine's <c>hull</c> module.
/// </summary>
/// <remarks>
/// Building a shape is O(v^4): O(v^3) vertex triples, each checked against O(v) vertices. It is
/// paid once per item and rotation (<see cref="ShapeFor"/> memoises it). The collision predicate
/// is O((f_a + f_b + e_a * e_b) * (a + b)) for f face axes and e edge directions, reached only
/// after the axis-aligned broad phase has said the envelopes overlap.
/// </remarks>
public static class Hull
{
    /// <summary>Smallest number of vertices that can enclose a volume.</summary>
    public const int MinimumHullVertices = 4;

    /// <summary>Largest absolute vertex coordinate a hull may carry, in ticks (6.25 m), shared with every engine.</summary>
    public const long MaxCoordinate = 100_000_000;

    /// <summary>How many rotated hulls stay resident; eviction changes cost, never an answer.</summary>
    public const int ShapeCacheEntries = 1024;

    private static readonly ShapeCache Cache = new(ShapeCacheEntries);

    /// <summary>Canonicalise an authored vertex list or refuse a hull with no interior.</summary>
    public static ImmutableArray<Vertex> Validate(IEnumerable<Vertex> vertices)
    {
        var points = vertices.ToImmutableArray();
        if (points.Length < MinimumHullVertices)
        {
            throw new DegenerateHullException(
                $"a convex hull needs at least {MinimumHullVertices} vertices, got {points.Length}");
        }
        if (points.Distinct().Count() != points.Length)
        {
            throw new DegenerateHullException("convex hull vertices must be unique");
        }
        if (points.Any(point => Math.Abs(point.X) > MaxCoordinate || Math.Abs(point.Y) > MaxCoordinate
                                || Math.Abs(point.Z) > MaxCoordinate))
        {
            throw new DegenerateHullException($"convex hull coordinates must stay within {MaxCoordinate} ticks");
        }
        if (EnclosesVolume(points))
        {
            return points;
        }
        throw new DegenerateHullException("convex hull vertices are coplanar and enclose no volume");
    }

    /// <summary>Inclusive lower and upper corners of a hull's axis-aligned envelope.</summary>
    public static (Vertex Lower, Vertex Upper) BoundingExtent(IReadOnlyList<Vertex> vertices) => (
        new Vertex(vertices.Min(v => v.X), vertices.Min(v => v.Y), vertices.Min(v => v.Z)),
        new Vertex(vertices.Max(v => v.X), vertices.Max(v => v.Y), vertices.Max(v => v.Z)));

    /// <summary>
    /// Reorient a hull the way <see cref="Dimensions.Rotated"/> reorients its box. Odd axis
    /// permutations flip one axis so every rotation stays proper (determinant +1) rather than
    /// handing back the item's mirror image. The result is translated so its bounding box
    /// starts at the origin.
    /// </summary>
    public static ImmutableArray<Vertex> Rotate(IReadOnlyList<Vertex> vertices, Rotation rotation)
    {
        var axes = rotation.SourceAxes();
        var sign = PermutationIsOdd(axes) ? -1 : 1;
        var turned = vertices
            .Select(vertex => new Vertex(sign * Component(vertex, axes.First), Component(vertex, axes.Second),
                Component(vertex, axes.Third)))
            .ToArray();
        var (lower, _) = BoundingExtent(turned);
        return turned.Select(vertex => vertex - lower).ToImmutableArray();
    }

    /// <summary>The rotated hull of one item in one orientation, built at most once per cache lifetime.</summary>
    public static HullShape ShapeFor(ImmutableArray<Vertex> vertices, Rotation rotation) =>
        Cache.GetOrAdd(vertices, rotation, () => HullShape.Of(Rotate(vertices, rotation)));

    /// <summary>Both hulls' face normals plus every edge-against-edge direction, deduplicated and sorted.</summary>
    public static ImmutableArray<Axis> SeparatingAxes(HullShape left, HullShape right)
    {
        var axes = new SortedSet<Axis>(left.FaceAxes);
        axes.UnionWith(right.FaceAxes);
        foreach (var leftEdge in left.EdgeDirections)
        {
            foreach (var rightEdge in right.EdgeDirections)
            {
                if (Primitive(leftEdge.Cross(rightEdge)) is { } axis)
                {
                    axes.Add(axis);
                }
            }
        }
        return [.. axes];
    }

    /// <summary>
    /// Do two placed hulls overlap with positive volume? Touching is contact, not collision,
    /// consistent with the half-open convention <see cref="AxisAlignedBox.Intersects"/> uses.
    /// </summary>
    public static bool Collide(HullShape left, Vertex leftOrigin, HullShape right, Vertex rightOrigin)
    {
        foreach (var axis in SeparatingAxes(left, right))
        {
            var (leftLow, leftHigh) = left.Projection(axis);
            var (rightLow, rightHigh) = right.Projection(axis);
            var leftShift = axis.Dot(leftOrigin);
            var rightShift = axis.Dot(rightOrigin);
            if (leftHigh + leftShift <= rightLow + rightShift || rightHigh + rightShift <= leftLow + leftShift)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Divide out the gcd and make the first non-zero component positive.</summary>
    internal static Axis Reduce(Axis axis)
    {
        var divisor = Gcd(Gcd(Int128.Abs(axis.X), Int128.Abs(axis.Y)), Int128.Abs(axis.Z));
        var reduced = new Axis(axis.X / divisor, axis.Y / divisor, axis.Z / divisor);
        var leading = reduced.X != 0 ? reduced.X : reduced.Y != 0 ? reduced.Y : reduced.Z;
        return leading > 0 ? reduced : reduced.Negated;
    }

    /// <summary><see cref="Reduce"/>, or null for the zero vector, which names no direction.</summary>
    internal static Axis? Primitive(Axis axis) => axis.IsZero ? null : Reduce(axis);

    internal static Axis Cross(Vertex left, Vertex right) => Axis.Of(left).Cross(Axis.Of(right));

    private static bool EnclosesVolume(ImmutableArray<Vertex> points)
    {
        for (var a = 0; a < points.Length; a++)
        {
            for (var b = a + 1; b < points.Length; b++)
            {
                for (var c = b + 1; c < points.Length; c++)
                {
                    var normal = Cross(points[b] - points[a], points[c] - points[a]);
                    for (var d = c + 1; d < points.Length; d++)
                    {
                        if (normal.Dot(points[d] - points[a]) != 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private static long Component(Vertex vertex, int axis) => axis switch
    {
        0 => vertex.X,
        1 => vertex.Y,
        _ => vertex.Z,
    };

    private static bool PermutationIsOdd((int First, int Second, int Third) axes)
    {
        var inversions = (axes.First > axes.Second ? 1 : 0) + (axes.First > axes.Third ? 1 : 0)
            + (axes.Second > axes.Third ? 1 : 0);
        return inversions % 2 == 1;
    }

    private static Int128 Gcd(Int128 left, Int128 right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }

    /// <summary>
    /// A bounded, thread-safe memo of rotated shapes. Keyed by vertex content, so two items that
    /// author the same hull share an entry; eviction is least-recently-used and observable only
    /// as time, because the function it memoises is pure.
    /// </summary>
    private sealed class ShapeCache(int capacity)
    {
        private readonly Dictionary<(VertexKey, Rotation), LinkedListNode<((VertexKey, Rotation) Key, HullShape Shape)>> entries = [];
        private readonly LinkedList<((VertexKey, Rotation) Key, HullShape Shape)> recency = new();
        private readonly object gate = new();

        public HullShape GetOrAdd(ImmutableArray<Vertex> vertices, Rotation rotation, Func<HullShape> build)
        {
            var key = (new VertexKey(vertices), rotation);
            lock (gate)
            {
                if (entries.TryGetValue(key, out var hit))
                {
                    recency.Remove(hit);
                    recency.AddFirst(hit);
                    return hit.Value.Shape;
                }
            }
            var shape = build();
            lock (gate)
            {
                if (!entries.ContainsKey(key))
                {
                    entries[key] = recency.AddFirst((key, shape));
                    if (entries.Count > capacity)
                    {
                        entries.Remove(recency.Last!.Value.Key);
                        recency.RemoveLast();
                    }
                }
            }
            return shape;
        }
    }

    private readonly struct VertexKey(ImmutableArray<Vertex> vertices) : IEquatable<VertexKey>
    {
        private readonly ImmutableArray<Vertex> vertices = vertices;

        public bool Equals(VertexKey other) => vertices.AsSpan().SequenceEqual(other.vertices.AsSpan());

        public override bool Equals(object? obj) => obj is VertexKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var vertex in vertices)
            {
                hash.Add(vertex);
            }
            return hash.ToHashCode();
        }
    }
}
