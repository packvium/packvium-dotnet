using System.Linq;
using Packvium.Packing;

namespace Packvium.Serialization;

/// <summary>The JSON entry point: <c>serialization.pack_from_dict</c>.</summary>
public static class JsonApi
{
    public static JsonObject PackFromJson(JsonValue request)
    {
        var call = RequestReader.Read(request);
        var result = new Packer(call.Configuration, new ExtensionRegistry(call.PlacementConstraints)).Pack(call.Items, call.Containers)
            .WithCatalogVersionsUsed(call.CatalogVersionsUsed.Cast<object>());
        return ResultWriter.Write(result, call.OutputLengthUnit, call.OutputWeightUnit);
    }
}
