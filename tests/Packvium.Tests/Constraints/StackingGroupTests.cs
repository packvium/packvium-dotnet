using System.Linq;
using Packvium.Constraints;
using Packvium.Domain;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Constraints;

/// <summary>Stacking groups (<c>test_constraints.py</c>,, docs/STACKING-GROUPS.md).</summary>
public sealed class StackingGroupTests
{
    private static Item Item(string id, bool stackable = true, string? group = null, params string[] accepts) =>
        new(id, Dimensions.Mm("10", "10", "10"), stackable: stackable, stackingGroup: group, acceptsStackingGroups: accepts);

    [Fact]
    public void AnEmptyListIsUnconstrainedAndANonEmptyOneIsClosed()
    {
        Assert.Null(LoadPropagation.StackingRefusal(Item("pallet"), "drum"));
        Assert.Null(LoadPropagation.StackingRefusal(Item("pallet", accepts: ["carton"]), "carton"));
        Assert.Equal("stacking_group_incompatible", LoadPropagation.StackingRefusal(Item("pallet", accepts: ["carton"]), "drum"));
        // An item with no group is in none of the lists: a forgotten classification fails closed.
        Assert.Equal("stacking_group_incompatible", LoadPropagation.StackingRefusal(Item("pallet", accepts: ["carton"]), null));
    }

    [Fact]
    public void NotStackableStillWins() =>
        Assert.Equal("non_stackable", LoadPropagation.StackingRefusal(Item("drum", false, "drum", "drum"), "drum"));

    [Fact]
    public void ATypeCarriesItselfOnlyWhenItsListNamesItsOwnGroup()
    {
        Assert.True(Item("crate").CarriesItself);
        Assert.True(Item("crate", group: "crate", accepts: ["crate"]).CarriesItself);
        Assert.False(Item("drum", group: "drum", accepts: ["carton"]).CarriesItself);
        Assert.False(Item("crate", false).CarriesItself);
    }

    /// <summary>Drums carry only cartons, so neither a crate nor a drum may stand on one, while a drum may stand on a crate.</summary>
    [Fact]
    public void NothingTheListRefusesRestsOnIt()
    {
        var result = JsonApi.PackFromJson(JsonReader.Parse("""
            {"configuration": {"solver_profile": "balanced", "time_limit_ms": 60000, "effort_budget": {"max_search_nodes": 100000}},
             "items": [{"id": "drum", "quantity": 2, "weight": "5000", "stacking_group": "drum", "accepts_stacking_groups": ["carton"],
                        "dimensions": {"length": "400", "width": "400", "height": "400"}},
                       {"id": "crate", "quantity": 2, "weight": "1000", "stacking_group": "crate",
                        "dimensions": {"length": "400", "width": "400", "height": "400"}}],
             "containers": [{"id": "bay", "inner_dimensions": {"length": "800", "width": "400", "height": "800"}}]}
            """));
        var placements = ((JsonArray)((JsonObject)((JsonArray)result.Require("containers")).Items.Single()).Require("placements"))
            .Items.Cast<JsonObject>().ToList();
        static (long X, long Z) At(JsonObject placement)
        {
            var position = (JsonObject)placement.Require("position");
            long Axis(string name) => (long)((JsonInteger)((JsonObject)position.Require(name)).Require("ticks")).Value;
            return (Axis("x"), Axis("z"));
        }
        var drums = placements.Where(p => ((JsonString)p.Require("item_type")).Value == "drum").Select(At).ToList();
        Assert.Equal(4, placements.Count);
        // Every cube spans a whole column of the bay, so nothing may stand higher in a drum's column.
        Assert.DoesNotContain(placements.Select(At), other => drums.Any(drum => other.X == drum.X && other.Z > drum.Z));
    }
}
