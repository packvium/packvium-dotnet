using System.Linq;
using Packvium.Packing;

namespace Packvium.Serialization;

/// <summary>The JSON entry point: <c>serialization.pack_from_dict</c>.</summary>
public static class JsonApi
{
    /// <summary>Packs a JSON request and returns the result document.</summary>
    public static JsonObject PackFromJson(JsonValue request) => PackFromJson(request, null);

    /// <summary>
    /// Packs a JSON request with caller extensions. The caller's placement constraints are
    /// added after the constraints the request's <c>policy</c> compiles to, never substituted
    /// for them: replacing them would let an operator lock silently drop a rule the request
    /// asked for. The caller's orderings, solvers and container selector are used as given.
    /// </summary>
    public static JsonObject PackFromJson(JsonValue request, ExtensionRegistry? extensions)
    {
        var call = RequestReader.Read(request);
        var supplied = extensions ?? new ExtensionRegistry();
        var registry = supplied with { PlacementConstraints = [.. call.PlacementConstraints, .. supplied.PlacementConstraints] };
        var result = new Packer(call.Configuration, registry).Pack(call.Items, call.Containers)
            .WithCatalogVersionsUsed(call.CatalogVersionsUsed.Cast<object>());
        return ResultWriter.Write(result, call.OutputLengthUnit, call.OutputWeightUnit);
    }
}
