using System;

namespace Packvium.Domain;

/// <summary>
/// The request is not one any engine may answer; nothing was solved. <see cref="Code"/> is
/// <c>invalid_request</c> (a subclass names its own), <see cref="Reason"/> one of a closed set
/// shared by every engine, <see cref="Field"/> the RFC 6901 pointer to the offending value
/// (<c>/items/0/quantity</c>, or empty for the request as a whole) and <see cref="Detail"/> the
/// fixed text of the rule, so the message is the same in every engine:
/// <c>invalid_request: /items/0/quantity: must be at least 1</c>.
/// </summary>
public class InvalidRequestException : ArgumentException
{
    /// <summary>The closed set of reasons, in the reference's order.</summary>
    public static readonly string[] Reasons =
    [
        "missing_field", "wrong_type", "below_minimum", "above_maximum", "negative_measure", "invalid_unit",
        "duplicate_id", "not_allowed", "invalid_value",
    ];

    public InvalidRequestException(string reason, string field, string detail)
        : this(reason, field, detail, field.Length > 0 ? $"invalid_request: {field}: {detail}" : $"invalid_request: {detail}")
    {
    }

    protected InvalidRequestException(string reason, string field, string detail, string message) : base(message)
    {
        Reason = reason;
        Field = field;
        Detail = detail;
    }

    public virtual string Code => "invalid_request";

    public string Reason { get; }

    public string Field { get; }

    public string Detail { get; }
}

/// <summary>
/// The request's fixed placements are malformed (<see cref="InvalidRequestException.Reason"/>
/// <c>malformed</c>, the field the bad value) or cannot all hold (<c>cannot_hold</c>, the field
/// <c>/fixed_placements</c>), so no search is attempted. The message is
/// <c>invalid_fixed_placement: </c> and the detail, with no field.
/// </summary>
public sealed class FixedPlacementException(string detail, string reason = "cannot_hold", string field = "/fixed_placements")
    : InvalidRequestException(reason, field, detail, $"invalid_fixed_placement: {detail}")
{
    public override string Code => "invalid_fixed_placement";
}

/// <summary>
/// A model refused a value while being built on its own, before it is part of a request, so there
/// is no field to name: <see cref="InvalidRequestException.Field"/> is empty and the message is the
/// detail alone, the text the model has always refused with. <see cref="Packing.Packer"/> names the
/// field for what it checks itself; the JSON path reports a refusal its rules did not name as
/// <c>invalid_value</c> with this detail, the form every engine shares.
/// </summary>
public sealed class ObjectRefusal(string reason, string detail)
    : InvalidRequestException(reason, string.Empty, detail, detail);
