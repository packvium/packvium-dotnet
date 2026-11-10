using Packvium.Units;

namespace Packvium.Domain;

/// <summary>
/// One item instance at one position. <see cref="Position"/> and <see cref="Dimensions"/> are the
/// physical item; the envelope is the item plus any clearance, which is what collides.
/// </summary>
public sealed record Placement(
    ItemInstance Instance,
    Point Position,
    Rotation Rotation,
    Dimensions Dimensions,
    Point EnvelopeOrigin,
    Dimensions EnvelopeDimensions,
    double SupportRatio = 1.0,
    Weight TopLoad = default)
{
    public AxisAlignedBox Box => new(Position, Dimensions);

    public AxisAlignedBox EnvelopeBox => new(EnvelopeOrigin, EnvelopeDimensions);

    /// <summary>
    /// This placement's rotated hull, or null when its box is the honest answer: every rigid
    /// cuboid, and any hull whose envelope a clearance inflated past the physical box.
    /// </summary>
    public HullShape? HullShape
    {
        get
        {
            var item = Instance.Item;
            if (!item.HullCollisionIsExact(EnvelopeDimensions == Dimensions) || item.HullVertices is not { } vertices)
            {
                return null;
            }
            return Hull.ShapeFor(vertices, Rotation);
        }
    }
}

/// <summary>
/// One definition of "collides", so the solver, the sequence check and the final validation
/// cannot disagree: the axis-aligned envelope test is the mandatory broad phase, refined only
/// when a hull is one of the two solids.
/// </summary>
public static class Collision
{
    public static bool PlacementsCollide(Placement left, Placement right)
    {
        var leftBox = left.EnvelopeBox;
        var rightBox = right.EnvelopeBox;
        if (!leftBox.Intersects(rightBox))
        {
            return false;
        }
        var leftShape = left.HullShape;
        var rightShape = right.HullShape;
        if (leftShape is null && rightShape is null)
        {
            return true;
        }
        return Hull.Collide(leftShape ?? BoxShape(leftBox), leftBox.Origin.AsVertex,
            rightShape ?? BoxShape(rightBox), rightBox.Origin.AsVertex);
    }

    /// <summary>Whether a placed item overlaps a plain box -- an obstacle, or any other fixed solid.</summary>
    public static bool PlacementHitsBox(Placement placement, AxisAlignedBox box)
    {
        var envelope = placement.EnvelopeBox;
        if (!envelope.Intersects(box))
        {
            return false;
        }
        if (placement.HullShape is not { } shape)
        {
            return true;
        }
        return Hull.Collide(shape, envelope.Origin.AsVertex, BoxShape(box), box.Origin.AsVertex);
    }

    private static HullShape BoxShape(AxisAlignedBox box) =>
        Domain.HullShape.Box(box.Dimensions.Length.Ticks, box.Dimensions.Width.Ticks, box.Dimensions.Height.Ticks);
}
