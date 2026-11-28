namespace Packvium.Validation;

/// <summary>
/// One way a result fails to be a sound answer to its request, as <see cref="ResultValidation.Validate"/>
/// reports it: the same code and detail every Packvium package reports for the same documents.
/// </summary>
public sealed record ResultIssue(string Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}
