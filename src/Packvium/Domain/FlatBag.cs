namespace Packvium.Domain;

/// <summary>
/// The flat-bag girth rule. A bag's inner dimensions are
/// its flat length L, flat width W and largest filled thickness H. With a, b and c the largest
/// envelope x2, y2 and z2 over its placements, a packing is admitted only when the rigid boundary
/// holds and a + c &lt;= L and b + c &lt;= W. Exact integer additions, O(1) per check; the extents
/// only grow, so a verdict never flips when more items follow.
/// </summary>
public static class FlatBag
{
    public const string Kind = "flat_bag";

    /// <summary>The <c>deformable_boundary</c> kinds a request may name.</summary>
    public static readonly string[] Kinds = [Kind];

    /// <summary>Whether a package of extents (a, b, c) passes both girth inequalities; the frame is the caller's check.</summary>
    public static bool GirthAdmits(Dimensions inner, long a, long b, long c) =>
        a + c <= inner.Length.Ticks && b + c <= inner.Width.Ticks;

    /// <summary>Whether one box of these dimensions, alone at the origin, fits a flat bag.</summary>
    public static bool FitsAlone(Dimensions inner, Dimensions dimensions) =>
        dimensions.FitsInside(inner)
        && GirthAdmits(inner, dimensions.Length.Ticks, dimensions.Width.Ticks, dimensions.Height.Ticks);

    /// <summary>(max x2, max y2, max z2) over upper corners, (0, 0, 0) for none. O(p).</summary>
    public static Triple ExtentOf(System.Collections.Generic.IEnumerable<AxisAlignedBox> boxes)
    {
        long x = 0, y = 0, z = 0;
        foreach (var box in boxes)
        {
            x = System.Math.Max(x, box.X2);
            y = System.Math.Max(y, box.Y2);
            z = System.Math.Max(z, box.Z2);
        }
        return new Triple(x, y, z);
    }
}
