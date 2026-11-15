using System;

namespace Packvium.Artifacts;

/// <summary>
/// The builder or an export was handed something an artifact cannot carry. <see cref="Code"/> is
/// one of a closed set every engine shares: <c>invalid_request</c>, <c>invalid_result</c>,
/// <c>invalid_plan_input</c>, <c>mixed_units</c>, <c>number_out_of_range</c>,
/// <c>invalid_string</c>, <c>invalid_value</c>, <c>unknown_format</c>, <c>invalid_json</c>.
/// </summary>
public sealed class OperationalArtifactException : ArgumentException
{
    public OperationalArtifactException(string code, string message) : base(message) => Code = code;

    public OperationalArtifactException(string code, string message, Exception inner) : base(message, inner) => Code = code;

    public string Code { get; }
}
