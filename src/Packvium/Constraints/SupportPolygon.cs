using System;
using System.Collections.Generic;
using System.Linq;
using Packvium.Domain;

namespace Packvium.Constraints;

/// <summary>A point in the doubled-tick plane, ordered x then y.</summary>
public readonly record struct Point2D(long X, long Y) : IComparable<Point2D>
{
    public int CompareTo(Point2D other)
    {
        var x = X.CompareTo(other.X);
        return x != 0 ? x : Y.CompareTo(other.Y);
    }

    public static bool operator <(Point2D left, Point2D right) => left.CompareTo(right) < 0;

    public static bool operator >(Point2D left, Point2D right) => left.CompareTo(right) > 0;

    public static bool operator <=(Point2D left, Point2D right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Point2D left, Point2D right) => left.CompareTo(right) >= 0;
}

/// <summary>Exact 2-D support geometry: integer cross products only, no trigonometry or division.</summary>
public static class SupportPolygon
{
    /// <summary>
    /// Andrew's monotone chain, counter-clockwise, duplicates collapsed. Degenerate input (0, 1
    /// or collinear points) returns that degenerate shape. O(m log m).
    /// </summary>
    public static List<Point2D> ConvexHull(IEnumerable<Point2D> points)
    {
        var sorted = points.Distinct().Order().ToList();
        if (sorted.Count <= 2)
        {
            return sorted;
        }
        var lower = Chain(sorted);
        sorted.Reverse();
        var upper = Chain(sorted);
        var hull = lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).ToList();
        if (hull.Count == 0)
        {
            sorted.Reverse();
            return sorted;
        }
        return hull;
    }

    /// <summary>Whether a point lies inside or on the boundary of a hull from <see cref="ConvexHull"/>.</summary>
    public static bool PointInHull(Point2D point, IReadOnlyList<Point2D> hull) => hull.Count switch
    {
        0 => false,
        1 => point == hull[0],
        2 => OnSegment(point, hull[0], hull[1]),
        _ => Enumerable.Range(0, hull.Count).All(i => Cross(hull[i], hull[(i + 1) % hull.Count], point) >= 0),
    };

    /// <summary>Doubled-tick corners of every candidate/supporter overlap rectangle.</summary>
    public static List<Point2D> ContactHullPoints(AxisAlignedBox candidate, IEnumerable<AxisAlignedBox> supporters)
    {
        var points = new List<Point2D>();
        foreach (var box in supporters)
        {
            var x1 = Math.Max(candidate.Origin.X, box.Origin.X);
            var y1 = Math.Max(candidate.Origin.Y, box.Origin.Y);
            var x2 = Math.Min(candidate.X2, box.X2);
            var y2 = Math.Min(candidate.Y2, box.Y2);
            if (x2 > x1 && y2 > y1)
            {
                foreach (var x in (long[])[x1, x2])
                {
                    foreach (var y in (long[])[y1, y2])
                    {
                        points.Add(new Point2D(2 * x, 2 * y));
                    }
                }
            }
        }
        return points;
    }

    /// <summary>Twice the box's footprint centroid, exact for odd extents.</summary>
    public static Point2D DoubledCentroid(AxisAlignedBox box) => new(box.Origin.X + box.X2, box.Origin.Y + box.Y2);

    /// <summary>Eight times the true area of a hull over doubled coordinates; zero when degenerate. O(m).</summary>
    public static Int128 EightTimesArea(IReadOnlyList<Point2D> hull)
    {
        if (hull.Count < 3)
        {
            return 0;
        }
        Int128 total = 0;
        for (var index = 0; index < hull.Count; index++)
        {
            var (x1, y1) = (hull[index].X, hull[index].Y);
            var next = hull[(index + 1) % hull.Count];
            total += (Int128)x1 * next.Y - (Int128)next.X * y1;
        }
        return Int128.Abs(total);
    }

    private static List<Point2D> Chain(List<Point2D> points)
    {
        var chain = new List<Point2D>();
        foreach (var point in points)
        {
            while (chain.Count >= 2 && Cross(chain[^2], chain[^1], point) <= 0)
            {
                chain.RemoveAt(chain.Count - 1);
            }
            chain.Add(point);
        }
        return chain;
    }

    private static Int128 Cross(Point2D origin, Point2D a, Point2D b) =>
        (Int128)(a.X - origin.X) * (b.Y - origin.Y) - (Int128)(a.Y - origin.Y) * (b.X - origin.X);

    private static bool OnSegment(Point2D p, Point2D a, Point2D b) =>
        Cross(a, b, p) == 0
        && Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X)
        && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);
}
