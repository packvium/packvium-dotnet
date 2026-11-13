using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Serialization;

/// <summary>The message is the reference engine's at v1.1.0, with this engine's name in it.</summary>
public sealed class UnsupportedFieldsTests
{
    [Fact]
    public void NamesAReservedContainerFieldOnceHoweverManyContainersSetIt()
    {
        const string request = """
            {"items": [], "containers": [{"id": "b", "pallet_overhang_limit": null}, {"id": "c", "pallet_overhang_limit": 1}]}
            """;
        Assert.Equal(
            "unsupported_feature: the .NET engine does not yet implement container.pallet_overhang_limit; "
            + "the request was rejected instead of silently ignoring public fields",
            Assert.Throws<UnsupportedFeatureException>(() => JsonApi.PackFromJson(JsonReader.Parse(request))).Message);
    }

    [Fact]
    public void LetsARequestWithoutReservedFieldsThrough() =>
        Assert.Null(Record.Exception(() =>
            UnsupportedFields.Reject((JsonObject)JsonReader.Parse("""{"items": [], "containers": [{"id": "b"}]}"""))));
}
