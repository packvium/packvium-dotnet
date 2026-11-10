using System.Collections.Generic;
using System.Collections.Immutable;
using Packvium.Domain;

namespace Packvium.Constraints;

/// <summary>One candidate placement, as every constraint sees it.</summary>
public sealed record ConstraintContext(
    Container Container,
    ImmutableArray<Placement> Placements,
    ItemInstance Item,
    Point Point,
    Rotation Rotation,
    Dimensions Dimensions,
    Dimensions EnvelopeDimensions,
    bool StackSensitive = true,
    bool RouteSensitive = true)
{
    public AxisAlignedBox EnvelopeBox => new(Point, EnvelopeDimensions);
}

/// <summary>A Specification's verdict: allowed, or refused with a reason code.</summary>
public sealed record ConstraintResult(bool Allowed, string Code = "allowed", string Detail = "")
{
    public static ConstraintResult Allowance { get; } = new(true);

    public static ConstraintResult Allow() => Allowance;

    public static ConstraintResult Reject(string code, string detail = "") => new(false, code, detail);

    public static ConstraintResult Reject((string Code, string Detail) failure) => new(false, failure.Code, failure.Detail);
}

/// <summary>A hard placement predicate.</summary>
public interface IPlacementConstraint
{
    ConstraintResult Evaluate(ConstraintContext context);
}

/// <summary>
/// A constraint that can rule an item out of every offered container without searching, which
/// is what lets an unpacked reason be reported as <c>proven</c>.
/// </summary>
public interface IProvenRejection
{
    (string Reason, string Detail)? ProvesUnplaceable(ItemInstance item, IReadOnlyList<Container> containers);
}
