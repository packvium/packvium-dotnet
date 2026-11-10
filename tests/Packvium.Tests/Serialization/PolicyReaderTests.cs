using System.Linq;
using Packvium.Constraints;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Serialization;

/// <summary>Expected messages and resolutions were produced by the reference engine at v1.0.0.</summary>
public sealed class PolicyReaderTests
{
    private const string Rule = "\"id\":\"a\",\"version\":1,\"effective_at\":0,\"priority\":0";

    [Theory]
    [InlineData("{\"rules\":[{}]}", "policy.as_of is required whenever policy.rules is non-empty")]
    [InlineData("{\"rules\":[{\"id\":\"a\"}],\"as_of\":0}",
        "policy.rules[0] must name exactly one rule form (limit_tag_per_container, require_container_tag, separate_tags), not 0")]
    [InlineData("{\"bogus\":1}", "policy has unknown keys: bogus")]
    [InlineData("{\"rules\":[{\"id\":\"\",\"version\":1,\"effective_at\":0,\"priority\":0,\"separate_tags\":{\"tag\":\"x\",\"from_tag\":\"y\"}}],\"as_of\":0}",
        "policy.rules[0].id must be a non-empty string")]
    [InlineData("{\"rules\":[{\"id\":\"a\",\"version\":true,\"effective_at\":0,\"priority\":0,\"separate_tags\":{\"tag\":\"x\",\"from_tag\":\"y\"}}],\"as_of\":0}",
        "policy.rules[0].version must be an integer >= 1")]
    [InlineData("{\"rules\":[{" + Rule + ",\"limit_tag_per_container\":{\"tag\":\"x\"}}],\"as_of\":0}",
        "policy.rules[0].limit_tag_per_container is missing max")]
    [InlineData("{\"rules\":[{" + Rule + ",\"separate_tags\":{\"tag\":\"x\",\"from_tag\":\"y\",\"z\":1}}],\"as_of\":0}",
        "policy.rules[0].separate_tags has unknown keys: z")]
    [InlineData("{\"shipment\":{\"planet\":\"x\"},\"rules\":[{" + Rule + ",\"separate_tags\":{\"tag\":\"x\",\"from_tag\":\"y\"}}],\"as_of\":0}",
        "policy.shipment names unknown shipment facts: planet")]
    public void RefusesAMalformedRuleSetWithTheReferenceMessage(string policy, string message) =>
        Assert.Equal(message, Assert.Throws<PolicyException>(() => PolicyReader.Read(JsonReader.Parse(policy))).Message);

    [Fact]
    public void ResolvesTheLatestEffectiveVersionInCitationOrder()
    {
        const string policy = """
            {"as_of":10,"shipment":{"carrier":"dhl"},"rules":[
              {"id":"b","version":1,"effective_at":0,"priority":1,"separate_tags":{"tag":"x","from_tag":"y"}},
              {"id":"b","version":2,"effective_at":5,"priority":1,"separate_tags":{"tag":"x","from_tag":"z"}},
              {"id":"a","version":1,"effective_at":0,"priority":1,"require_container_tag":{"item_tag":"x","container_tag":"cold"}},
              {"id":"c","version":1,"effective_at":20,"priority":9,"limit_tag_per_container":{"tag":"x","max":1}},
              {"id":"d","version":1,"effective_at":0,"priority":5,"applies_to":{"carrier":"ups"},"limit_tag_per_container":{"tag":"x","max":1}},
              {"id":"e","version":3,"effective_at":0,"priority":0,"applies_to":{"carrier":"dhl"},"limit_tag_per_container":{"tag":"x","max":1}}]}
            """;
        Assert.Equal(["a@1", "b@2", "e@3"], PolicyReader.Read(JsonReader.Parse(policy)).Rules.Select(rule => rule.Citation).ToArray());
    }
}
