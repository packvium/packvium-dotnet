using System.Linq;
using Packvium.Domain;
using Packvium.Serialization;
using Packvium.Units;
using Xunit;

namespace Packvium.Tests.Domain;

/// <summary>Flat bags (<c>test_flat_bag.py</c>), admitted by the girth rule a + c &lt;= L and b + c &lt;= W.</summary>
public sealed class FlatBagTests
{
    private static long Mm(long millimetres) => millimetres * Length.TicksPerMm;

    [Fact]
    public void TheWorkedExamplesOfTheOracle()
    {
        var inner = Dimensions.Mm("240", "140", "100");
        Assert.True(FlatBag.GirthAdmits(inner, Mm(200), Mm(100), Mm(40)));
        Assert.False(FlatBag.GirthAdmits(inner, Mm(200), Mm(100), Mm(40) + 1));
        Assert.False(FlatBag.GirthAdmits(inner, Mm(240) + 1, 0, 0));
        // A box that fits the rigid frame can still fail the girth sum alone.
        Assert.True(Dimensions.Mm("200", "100", "60").FitsInside(inner));
        Assert.False(FlatBag.FitsAlone(inner, Dimensions.Mm("200", "100", "60")));
    }

    [Fact]
    public void ABagRefusesEveryWallAnchoredField()
    {
        Assert.Contains("unknown deformable_boundary kind 'gusseted'", Assert.Throws<System.ArgumentException>(() =>
            new Container("mailer", Dimensions.Mm("240", "140", "100"), deformableBoundary: "gusseted")).Message);
        Assert.Contains("access_directions cannot be combined with deformable_boundary",
            Assert.Throws<System.ArgumentException>(() => new Container("mailer", Dimensions.Mm("240", "140", "100"),
                accessDirections: ["-x"], deformableBoundary: FlatBag.Kind)).Message);
    }

    /// <summary>Three books: the third would make the package too thick, so a second bag opens, each billed on its own package.</summary>
    [Fact]
    public void ThreeBooksOpenASecondBag()
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse("""
            {"configuration": {"solver_profile": "fast", "time_limit_ms": 60000, "effort_budget": {"max_search_nodes": 100000}},
             "items": [{"id": "book", "quantity": 3, "weight": "300 g", "allowed_rotations": ["LWH"],
                        "dimensions": {"length": "200", "width": "100", "height": "20"}}],
             "containers": [{"id": "mailer", "inner_dimensions": {"length": "240", "width": "140", "height": "100"},
                             "deformable_boundary": {"kind": "flat_bag"}}]}
            """));
        var achieved = ((JsonArray)result.Require("containers")).Items.Cast<JsonObject>()
            .Select(container => ((JsonString)((JsonObject)((JsonObject)container.Require("achieved_dimensions"))
                .Require("height")).Require("value")).Value).ToList();
        Assert.Equal(["40", "20"], achieved);
    }
}
