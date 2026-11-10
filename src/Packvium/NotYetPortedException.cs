using System;

namespace Packvium;

/// <summary>
/// Raised where the reference engine has behaviour this port has not reproduced yet. Distinct
/// from every refusal the reference engine itself makes, so a caller -- and the conformance
/// runner -- can tell "not built yet" from "rejected". Removed once the port reaches parity.
/// </summary>
internal sealed class NotYetPortedException(string feature)
    : NotSupportedException($"not yet ported to the .NET engine: {feature}")
{
    public string Feature { get; } = feature;
}
