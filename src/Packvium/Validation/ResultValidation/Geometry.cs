using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Packvium.Serialization;

namespace Packvium.Validation.ResultChecks;

/// <summary>
/// Rotations and convex hulls in ticks, written the slow, obvious way the reference writes them:
/// a validator that took the engine's shortcuts would stop being a second opinion.
/// </summary>
internal static class Geometry
{
    /// <summary>The six orientation codes in the reference's order.</summary>
    public static readonly ImmutableArray<string> Orientations = ["LWH", "LHW", "WLH", "WHL", "HLW", "HWL"];

    /// <summary><c>ROTATIONS[orientation]</c>: the axis permutation it applies to (length, width, height).</summary>
    public static ImmutableArray<int> Axes(string orientation) => orientation switch
    {
        "LWH" => [0, 1, 2],
        "LHW" => [0, 2, 1],
        "WLH" => [1, 0, 2],
        "WHL" => [1, 2, 0],
        "HLW" => [2, 0, 1],
        "HWL" => [2, 1, 0],
        _ => throw MalformedResultException.ShapeBreak($"unknown orientation \"{orientation}\""),
    };

    /// <summary><c>ROTATIONS[value]</c> for any JSON value.</summary>
    public static ImmutableArray<int> Axes(JsonValue orientation)
    {
        Json.HashKey(orientation);
        return Axes(orientation is JsonString text ? text.Value : PyFormat.Repr(orientation));
    }

    /// <summary><c>orientation in ROTATIONS</c>, for any JSON value; an unhashable one is Python's <c>TypeError</c>.</summary>
    public static bool IsRotation(JsonValue orientation)
    {
        Json.HashKey(orientation);
        return orientation is JsonString text && Orientations.Contains(text.Value);
    }

    public static ImmutableArray<BigInteger> Permute(ImmutableArray<BigInteger> sides, ImmutableArray<int> axes) =>
        [sides[axes[0]], sides[axes[1]], sides[axes[2]]];

    /// <summary>The axis reduced by its gcd and signed so its first non-zero component is positive.</summary>
    public static TickVector PrimitiveAxis(TickVector axis)
    {
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(axis.X, axis.Y), axis.Z);
        if (divisor.IsZero)
        {
            return TickVector.Zero;
        }
        var reduced = new TickVector(Exact.FloorDiv(axis.X, divisor), Exact.FloorDiv(axis.Y, divisor), Exact.FloorDiv(axis.Z, divisor));
        var leading = !reduced.X.IsZero ? reduced.X : !reduced.Y.IsZero ? reduced.Y : reduced.Z;
        return leading.Sign < 0 ? reduced.Negate() : reduced;
    }

    /// <summary>
    /// Reorient a hull the way the rotation reorients its box, flipping one axis on an odd
    /// permutation so it stays a proper rotation, then translated so its bounds start at 0. O(v).
    /// </summary>
    public static ImmutableArray<TickVector> RotateHull(ImmutableArray<TickVector> vertices, ImmutableArray<int> axes)
    {
        if (vertices.IsEmpty)
        {
            throw MalformedResultException.ShapeBreak("a hull without vertices");
        }
        var inversions = (axes[0] > axes[1] ? 1 : 0) + (axes[0] > axes[2] ? 1 : 0) + (axes[1] > axes[2] ? 1 : 0);
        var turned = vertices.Select(vertex => new TickVector(
            inversions % 2 == 1 ? -vertex[axes[0]] : vertex[axes[0]], vertex[axes[1]], vertex[axes[2]])).ToImmutableArray();
        var low = new TickVector(turned.Min(vertex => vertex.X), turned.Min(vertex => vertex.Y), turned.Min(vertex => vertex.Z));
        return [.. turned.Select(vertex => vertex - low)];
    }

    /// <summary>
    /// Whether two convex solids share interior volume; touching is contact, not collision.
    /// O((a³ + b³ + a²b²)(a + b)) for hulls of a and b vertices, by the naive separating-axis set.
    /// </summary>
    public static bool SolidsOverlap(ImmutableArray<TickVector> left, ImmutableArray<TickVector> right)
    {
        foreach (var axis in HullAxes(left, right))
        {
            var (leftLow, leftHigh) = Extent(left, axis);
            var (rightLow, rightHigh) = Extent(right, axis);
            if (leftHigh <= rightLow || rightHigh <= leftLow)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Exact volume of a convex hull, <c>6V = Σ a · (b × c)</c> over its outward faces, every vertex
    /// triple tested as a supporting plane. O(v⁴).
    /// </summary>
    public static BigInteger HullVolume(ImmutableArray<TickVector> vertices)
    {
        var normals = new SortedSet<TickVector>(Comparer<TickVector>.Create((a, b) => a.CompareTo(b)));
        foreach (var (first, second, third) in Triples(vertices))
        {
            var axis = PrimitiveAxis((second - first).Cross(third - first));
            if (axis != TickVector.Zero)
            {
                normals.Add(axis);
            }
        }
        var sixVolumes = BigInteger.Zero;
        foreach (var axis in normals)
        {
            foreach (var outward in new[] { axis, axis.Negate() })
            {
                var highest = Extent(vertices, outward).High;
                var face = vertices.Where(vertex => vertex.Dot(outward) == highest).ToList();
                if (face.Count < 3)
                {
                    continue;
                }
                var ordered = WindFace(face, outward);
                for (var index = 1; index + 1 < ordered.Count; index++)
                {
                    sixVolumes += ordered[0].Dot(ordered[index].Cross(ordered[index + 1]));
                }
            }
        }
        return Exact.FloorDiv(BigInteger.Abs(sixVolumes), 6);
    }

    /// <summary>
    /// Corners of one planar convex face in cyclic order seen from outside, by gift-wrapping from
    /// the lexicographically smallest vertex; collinear candidates resolve to the farthest. O(f²).
    /// </summary>
    private static List<TickVector> WindFace(List<TickVector> face, TickVector outward)
    {
        var start = face.Aggregate((least, vertex) => vertex.CompareTo(least) < 0 ? vertex : least);
        var ordered = new List<TickVector> { start };
        var current = start;
        for (var step = 0; step < face.Count; step++)
        {
            TickVector? following = null;
            foreach (var candidate in face)
            {
                if (candidate == current)
                {
                    continue;
                }
                if (following is not { } held)
                {
                    following = candidate;
                    continue;
                }
                var turn = (held - current).Cross(candidate - current).Dot(outward);
                var reach = (candidate - current).Dot(candidate - current);
                var heldReach = (held - current).Dot(held - current);
                if (turn.Sign < 0 || (turn.IsZero && reach > heldReach))
                {
                    following = candidate;
                }
            }
            if (following is not { } next || next == start)
            {
                break;
            }
            ordered.Add(next);
            current = next;
        }
        return ordered;
    }

    /// <summary>The complete separating-axis superset: face normals of each hull and cross products of their edges.</summary>
    private static HashSet<TickVector> HullAxes(ImmutableArray<TickVector> left, ImmutableArray<TickVector> right)
    {
        var axes = new HashSet<TickVector>();
        foreach (var hull in new[] { left, right })
        {
            foreach (var (first, second, third) in Triples(hull))
            {
                axes.Add(PrimitiveAxis((second - first).Cross(third - first)));
            }
        }
        var rightDirections = PairDirections(right);
        foreach (var one in PairDirections(left))
        {
            foreach (var other in rightDirections)
            {
                axes.Add(PrimitiveAxis(one.Cross(other)));
            }
        }
        axes.Remove(TickVector.Zero);
        return axes;
    }

    private static List<TickVector> PairDirections(ImmutableArray<TickVector> points)
    {
        var directions = new List<TickVector>();
        for (var first = 0; first < points.Length; first++)
        {
            for (var second = first + 1; second < points.Length; second++)
            {
                directions.Add(points[second] - points[first]);
            }
        }
        return directions;
    }

    /// <summary>Every 3-combination, in <c>itertools.combinations</c> order.</summary>
    private static IEnumerable<(TickVector First, TickVector Second, TickVector Third)> Triples(ImmutableArray<TickVector> points)
    {
        for (var first = 0; first < points.Length; first++)
        {
            for (var second = first + 1; second < points.Length; second++)
            {
                for (var third = second + 1; third < points.Length; third++)
                {
                    yield return (points[first], points[second], points[third]);
                }
            }
        }
    }

    private static (BigInteger Low, BigInteger High) Extent(IEnumerable<TickVector> points, TickVector axis)
    {
        var values = points.Select(point => point.Dot(axis)).ToList();
        return (values.Min(), values.Max());
    }
}
