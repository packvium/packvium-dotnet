using System;

namespace Packvium.Validation;

/// <summary>
/// The documents do not have the shape the validation contract requires: a contract break, not a
/// packing defect, so no issue list is returned.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is <see cref="Contract"/> where the reference validator names the break
/// itself, with the same message (where that message quotes a Python exception, the text after the
/// quote is this validator's own account); it is <see cref="Shape"/> where the reference fails on
/// the input another way -- a missing key, a value of the wrong type -- and the message is this
/// validator's own.
/// </remarks>
public sealed class MalformedResultException : Exception
{
    public const string Contract = "contract";

    public const string Shape = "shape";

    public MalformedResultException(string message, string kind)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary><see cref="Contract"/> or <see cref="Shape"/>.</summary>
    public string Kind { get; }

    internal static MalformedResultException ContractBreak(string message) => new(message, Contract);

    internal static MalformedResultException ShapeBreak(string message) => new(message, Shape);
}
