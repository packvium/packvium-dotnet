using System.IO;
using Packvium.Cli;
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
}
