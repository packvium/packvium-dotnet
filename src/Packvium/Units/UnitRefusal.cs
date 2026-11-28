using System;

namespace Packvium.Units;

/// <summary>
/// A measure refused as it is read from a caller's value, with the request schema's reason for it
/// (<c>invalid_unit</c>, <c>negative_measure</c>, <c>wrong_type</c>). The message is the detail; a
/// request built from these measures reports the same reason.
/// </summary>
public sealed class UnitRefusal(string reason, string detail) : ArgumentException(detail)
{
    public string Reason { get; } = reason;

    public string Detail { get; } = detail;
}
