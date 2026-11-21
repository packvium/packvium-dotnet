using System;

namespace Packvium.Revisions;

/// <summary>
/// A revision could not be built. <see cref="Code"/> is one of a closed set shared by every
/// engine: <c>invalid_revision</c>, <c>invalid_event</c>, <c>event_conflict</c>,
/// <c>invalid_artifact</c>, and the canonical-form codes <c>number_out_of_range</c>,
/// <c>invalid_string</c> and <c>invalid_value</c>. A message quotes a value by its canonical
/// JSON, so every engine writes the same text.
/// </summary>
public sealed class PlanRevisionException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>One way a chain fails its audit, anchored on the revision where it shows.</summary>
public sealed record RevisionIssue(string Code, int Revision, string Detail);
