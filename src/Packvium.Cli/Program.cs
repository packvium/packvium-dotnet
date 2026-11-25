using System;
using System.IO;
using System.Text;
using Packvium.Serialization;

namespace Packvium.Cli;

/// <summary>
/// The Packvium command line: a request on stdin or from a file, the result on stdout as sorted,
/// two-space-indented JSON. A refused request exits non-zero with the reason on stderr, the same
/// contract every Packvium command-line program keeps.
/// </summary>
public static class Program
{
    private const string Usage = "usage: packvium [-h] [-o OUTPUT] [input] | packvium commerce | packvium artifacts | packvium revisions";

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static int Main(string[] args)
    {
        if (args is ["commerce"])
        {
            return CommerceCommand.Run();
        }
        if (args is ["artifacts"])
        {
            return ArtifactsCommand.Run();
        }
        if (args is ["revisions"])
        {
            return RevisionsCommand.Run();
        }
        if (Arguments.Parse(args) is not { } arguments)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine("packvium: error: unrecognized arguments");
            return 2;
        }
        if (arguments.Help)
        {
            Console.Out.WriteLine(Usage);
            Console.Out.WriteLine();
            Console.Out.WriteLine("Pack rigid cuboids into rectangular containers");
            return 0;
        }
        try
        {
            var request = JsonReader.Parse(arguments.Input is null ? ReadStdin() : File.ReadAllBytes(arguments.Input));
            var encoded = PythonJson.Dumps(JsonApi.PackFromJson(request), sortKeys: true, indent: 2) + "\n";
            if (arguments.Output is null)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(Utf8.GetBytes(encoded));
            }
            else
            {
                File.WriteAllText(arguments.Output, encoded, Utf8);
            }
            return 0;
        }
#pragma warning disable CA1031 // Every refusal is reported the same way: its type and message, exit 1.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    internal static byte[] ReadStdin()
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The reference's <c>argparse</c> surface: one optional input path and <c>-o/--output</c>.</summary>
    private sealed record Arguments(string? Input, string? Output, bool Help)
    {
        public static Arguments? Parse(string[] args)
        {
            string? input = null;
            string? output = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "-h" or "--help":
                        return new Arguments(null, null, true);
                    case "-o" or "--output" when index + 1 < args.Length:
                        output = args[++index];
                        break;
                    case var value when value.StartsWith("--output=", StringComparison.Ordinal):
                        output = value["--output=".Length..];
                        break;
                    case var value when !value.StartsWith('-') && input is null:
                        input = value;
                        break;
                    default:
                        return null;
                }
            }
            return new Arguments(input, output, false);
        }
    }
}
