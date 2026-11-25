using System.Linq;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Objectives;

/// <summary>The <c>maximum_volume</c> objective (<c>test_objective.py</c>).</summary>
public sealed class MaximumVolumeTests
{
    private static JsonObject Pack(string objective) => JsonApi.PackFromJson(JsonReader.Parse($$$"""
        {"configuration": {"solver_profile": "balanced", "objective": "{{{objective}}}", "time_limit_ms": 60000,
                           "effort_budget": {"max_search_nodes": 100000}},
         "items": [{"id": "big", "quantity": 1, "dimensions": {"length": "100", "width": "100", "height": "90"}},
                   {"id": "small", "quantity": 3, "dimensions": {"length": "100", "width": "100", "height": "40"}}],
         "containers": [{"id": "box", "quantity": 1, "inner_dimensions": {"length": "100", "width": "100", "height": "100"}}]}
        """));

    private static string[] Packed(JsonObject result) =>
        [.. ((JsonArray)((JsonObject)((JsonArray)result.Require("containers")).Items.Single()).Require("placements"))
            .Items.Cast<JsonObject>().Select(p => ((JsonString)p.Require("item_type")).Value)];

    /// <summary>Two small items are more pieces, the large one more volume: the objective decides.</summary>
    [Fact]
    public void FillingTheSpaceBeatsPlacingThePieces()
    {
        Assert.Equal(["small", "small"], Packed(Pack("default")));
        var byVolume = Pack("maximum_volume");
        Assert.Equal(["big"], Packed(byVolume));
        // ceil(120 / 210 x 10^6): the unpacked share leads, rounded up so it never reads complete.
        Assert.Equal(571_429, (long)((JsonInteger)((JsonArray)byVolume.Require("score")).Items[0]).Value);
    }
}
