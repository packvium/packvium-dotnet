using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Packvium.Domain;

/// <summary>
/// A convex hull's separating axes in its own local frame, computed once and shared between
/// candidates: it describes the shape, and a placement contributes only an offset.
/// </summary>
public sealed class HullShape
{
    private static readonly ImmutableArray<Axis> UnitAxes = [new(0, 0, 1), new(0, 1, 0), new(1, 0, 0)];

    private HullShape(ImmutableArray<Vertex> vertices, ImmutableArray<Axis> faceAxes,
        ImmutableArray<Axis> edgeDirections, Int128 volume)
    {
        Vertices = vertices;
        FaceAxes = faceAxes;
        EdgeDirections = edgeDirections;
        Volume = volume;
    }

    public ImmutableArray<Vertex> Vertices { get; }

    public ImmutableArray<Axis> FaceAxes { get; }

    public ImmutableArray<Axis> EdgeDirections { get; }

    /// <summary>Exact occupied volume in cubic ticks.</summary>
    public Int128 Volume { get; }

    /// <summary>Only supporting planes survive as face axes; edges are the wound faces' real edges.</summary>
    public static HullShape Of(IEnumerable<Vertex> vertices)
    {
        var points = Hull.Validate(vertices);
        var normals = new SortedSet<Axis>();
        for (var a = 0; a < points.Length; a++)
        {
            for (var b = a + 1; b < points.Length; b++)
            {
                for (var c = b + 1; c < points.Length; c++)
                {
                    if (Hull.Primitive(Hull.Cross(points[b] - points[a], points[c] - points[a])) is { } axis
                        && IsSupporting(points, points[a], axis))
                    {
                        normals.Add(axis);
                    }
                }
            }
        }
        ImmutableArray<Axis> faceAxes = [.. normals];
        var faces = WoundFaces(points, faceAxes);
        return new HullShape(points, faceAxes, EdgeDirectionsOf(faces), VolumeOf(faces));
    }

    /// <summary>A cuboid, built in O(1): its face normals and edge directions are the unit axes.</summary>
    public static HullShape Box(long length, long width, long height)
    {
        var vertices = ImmutableArray.CreateBuilder<Vertex>(8);
        foreach (var x in (long[])[0, 1])
        {
            foreach (var y in (long[])[0, 1])
            {
                foreach (var z in (long[])[0, 1])
                {
                    vertices.Add(new Vertex(x * length, y * width, z * height));
                }
            }
        }
        return new HullShape(vertices.MoveToImmutable(), UnitAxes, UnitAxes, checked((Int128)length * width * height));
    }

    /// <summary>Closed projection interval on <paramref name="axis"/>, in local coordinates.</summary>
    public (Int128 Low, Int128 High) Projection(Axis axis)
    {
        var low = axis.Dot(Vertices[0]);
        var high = low;
        foreach (var vertex in Vertices)
        {
            var value = axis.Dot(vertex);
            low = Int128.Min(low, value);
            high = Int128.Max(high, value);
        }
        return (low, high);
    }

    private static bool IsSupporting(ImmutableArray<Vertex> vertices, Vertex origin, Axis axis)
    {
        var seenAbove = false;
        var seenBelow = false;
        var offset = axis.Dot(origin);
        foreach (var vertex in vertices)
        {
            var side = axis.Dot(vertex) - offset;
            seenAbove |= side > 0;
            seenBelow |= side < 0;
            if (seenAbove && seenBelow)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Every face, as its corners in outward cyclic order. Each canonical axis stands for up to
    /// two opposite faces; a plane carrying fewer than three vertices is an edge or a corner.
    /// </summary>
    private static ImmutableArray<ImmutableArray<Vertex>> WoundFaces(ImmutableArray<Vertex> vertices,
        ImmutableArray<Axis> faceAxes)
    {
        var faces = ImmutableArray.CreateBuilder<ImmutableArray<Vertex>>();
        foreach (var axis in faceAxes)
        {
            foreach (var outward in (Axis[])[axis, axis.Negated])
            {
                var extreme = vertices.Max(vertex => outward.Dot(vertex));
                var face = vertices.Where(vertex => outward.Dot(vertex) == extreme).ToImmutableArray();
                if (face.Length >= 3)
                {
                    faces.Add(OrderedFace(face, outward));
                }
            }
        }
        return faces.ToImmutable();
    }

    /// <summary>
    /// Gift-wraps one planar face from its lexicographically smallest vertex, so a vertex lying
    /// inside the face or along an edge is skipped rather than triangulated.
    /// </summary>
    private static ImmutableArray<Vertex> OrderedFace(ImmutableArray<Vertex> face, Axis outward)
    {
        var start = face.Min();
        var ordered = ImmutableArray.CreateBuilder<Vertex>();
        ordered.Add(start);
        var current = start;
        for (var step = 0; step < face.Length; step++)
        {
            Vertex? following = null;
            foreach (var candidate in face)
            {
                if (candidate == current)
                {
                    continue;
                }
                if (following is not { } chosen)
                {
                    following = candidate;
                    continue;
                }
                var turn = Hull.Cross(chosen - current, candidate - current).Dot(outward);
                if (turn < 0 || (turn == 0 && SquareLength(candidate - current) > SquareLength(chosen - current)))
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
        return ordered.ToImmutable();
    }

    private static Int128 SquareLength(Vertex vector) => Axis.Of(vector).Dot(vector);

    /// <summary><c>6V = sum of a . (b x c)</c> over outward surface triangles: an integer, so exact.</summary>
    private static Int128 VolumeOf(ImmutableArray<ImmutableArray<Vertex>> faces)
    {
        Int128 sixVolumes = 0;
        foreach (var ordered in faces)
        {
            var apex = ordered[0];
            for (var index = 1; index + 1 < ordered.Length; index++)
            {
                sixVolumes = checked(sixVolumes + Hull.Cross(ordered[index], ordered[index + 1]).Dot(apex));
            }
        }
        return Int128.Abs(sixVolumes) / 6;
    }

    private static ImmutableArray<Axis> EdgeDirectionsOf(ImmutableArray<ImmutableArray<Vertex>> faces)
    {
        var edges = new SortedSet<Axis>();
        foreach (var ordered in faces)
        {
            for (var index = 0; index < ordered.Length; index++)
            {
                var end = ordered[(index + 1) % ordered.Length];
                edges.Add(Hull.Reduce(Axis.Of(end - ordered[index])));
            }
        }
        return [.. edges];
    }
}
