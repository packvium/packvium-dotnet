using System;

namespace Packvium.Validation.ResultChecks;

/// <summary>Issues as the reference words them: every number in a detail is written in the invariant culture.</summary>
internal static class Issues
{
    public static ResultIssue Of(string code, FormattableString detail) => new(code, FormattableString.Invariant(detail));
}
