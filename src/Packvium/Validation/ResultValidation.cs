using System.Collections.Immutable;
using Packvium.Serialization;
using Packvium.Validation.ResultChecks;

namespace Packvium.Validation;

/// <summary>
/// The reference result validator: decides whether a packing result, as JSON, is a physically
/// and structurally sound answer to its request, as JSON. It shares no code with the engine --
/// units, geometry, support and load are all recomputed from first principles -- so it judges
/// any engine's result, and returns the issues every Packvium package returns for the same
/// documents, in the same order, with the same code and detail.
/// </summary>
public static class ResultValidation
{
    /// <summary>
    /// Every way <paramref name="result"/> fails to be a sound answer to <paramref name="request"/>;
    /// empty means valid.
    /// </summary>
    /// <remarks>
    /// Per container of p placements, O(p²) for the pairwise collision and nesting checks (each
    /// pair of convex hulls of a and b vertices costs O((a³ + b³ + a²b²)(a + b)) by the naive
    /// separating-axis set; a pair of boxes is constant), O(p log p + e) for the support graph and
    /// load propagation over e support edges, and O(s · d² · p) for route unloading over s stops
    /// with d items due at each; everything else is linear in the documents. Integers are exact at
    /// any size. Space is O(p + e) per container beyond the documents.
    /// </remarks>
    /// <exception cref="MalformedResultException">The result does not have the documented shape at all.</exception>
    public static ImmutableArray<ResultIssue> Validate(JsonValue request, JsonValue result) => [.. Validator.Validate(request, result)];
}
