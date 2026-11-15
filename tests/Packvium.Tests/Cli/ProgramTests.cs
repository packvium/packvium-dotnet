using System.IO;
using Packvium.Cli;
using Packvium.Serialization;
using Xunit;

namespace Packvium.Tests.Cli;

public sealed class ProgramTests
{
    private const string Request = """
        {"configuration":{"solver_profile":"fast"},
         "items":[{"id":"cube","dimensions":{"length":100,"width":100,"height":100},"quantity":8}],
         "containers":[{"id":"box","inner_dimensions":{"length":200,"width":200,"height":200}}]}
        """;

    [Fact]
    public void WritesTheSortedIndentedResultWithATrailingNewline()
    {
        var input = Path.GetTempFileName();
        var output = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, Request);
            Assert.Equal(0, Program.Main([input, "-o", output]));
            var text = File.ReadAllText(output);
            Assert.StartsWith("{\n  \"algorithm\": {", text);
            Assert.EndsWith("}\n", text);
            Assert.Contains("\"status\": \"feasible\"", text);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void ExitsWithOneOnARefusedRequest()
    {
        var input = Path.GetTempFileName();
        try
        {
            File.WriteAllText(input, """{"items":[],"containers":[]}""");
            Assert.Equal(1, Program.Main([input]));
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void ExitsWithTwoOnAnUnknownArgument() => Assert.Equal(2, Program.Main(["--bogus"]));

    /// <summary>The artifact probe answers a refusal as an outcome with its code and no exports, as <c>probe.php</c> does.</summary>
    [Fact]
    public void TheArtifactProbeAnswersARefusalWithItsCode()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "foreign.json"), """{"format":"packvium-operational-artifact/v2"}""");
            File.WriteAllText(Path.Combine(directory.FullName, "request.json"), "[]");
            File.WriteAllText(Path.Combine(directory.FullName, "result.json"), Request);
            var export = (JsonObject)JsonReader.Parse("""{"name":"x","kind":"export","artifact_path":"foreign.json"}""");
            var build = (JsonObject)JsonReader.Parse(
                """{"name":"y","kind":"build","request_path":"request.json","result_path":"result.json","loading_orders":{"0":[0]}}""");
            Assert.Equal("""{"csv":null,"error":"unknown_format","html":null,"json":null}""",
                CanonicalJsonWriter.Write(ArtifactsCommand.Outcome(export, directory.FullName)));
            Assert.Equal("""{"csv":null,"error":"invalid_request","html":null,"json":null}""",
                CanonicalJsonWriter.Write(ArtifactsCommand.Outcome(build, directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
