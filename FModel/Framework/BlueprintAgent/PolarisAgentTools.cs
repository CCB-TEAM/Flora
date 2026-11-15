using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Runs the separately installed Polaris compiler on exported package copies.</summary>
public static class PolarisAgentTools
{
    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "polaris_generate_sdk",
            Description = "Generate the Polaris C# SDK from a jmap into a new output directory. Defaults to kards,Engine,CoreUObject modules; supply filter explicitly for others. Requires a separately built Polaris CLI and .NET 10. Writes files; it does not modify the loaded game.",
            InputSchema = Object(("cli", String("Absolute path to the built polaris.dll"), true),
                ("jmap", String("Absolute path to the game jmap"), true),
                ("output", String("Output directory; Polaris.Sdk must not already exist inside it"), true),
                ("filter", String("Comma-separated module names; default kards,Engine,CoreUObject"), false)),
            Access = AgentToolAccess.Write, OffUiThread = true,
            Run = async (args, ct) =>
            {
                var cli = ExistingFile(args.Str("cli"), ".dll");
                var jmap = ExistingFile(args.Str("jmap"), ".jmap");
                var output = FullPath(args.Str("output"));
                if (Directory.Exists(Path.Combine(output, "Polaris.Sdk")))
                    throw new AgentToolException("Output already contains Polaris.Sdk; choose a fresh directory. Polaris otherwise deletes it.");
                Directory.CreateDirectory(output);
                var filter = args.Str("filter", false);
                return await Run(cli, ["sdk", "--jmap", jmap, "--filter", string.IsNullOrWhiteSpace(filter) ? "kards,Engine,CoreUObject" : filter, "--out", output], ct);
            },
        },
        new()
        {
            Name = "polaris_apply",
            Description = "Compile a Polaris C# mod against exported cooked assets and write outputs to a separate directory. Dry-run defaults to true. This does not stage changes in Flora or install a pak. Existing function bodies may be replaced; original ubergraph logic may be lost.",
            InputSchema = Object(("cli", String("Absolute path to built polaris.dll"), true),
                ("mod", String("C# mod source file with Polaris annotations"), true),
                ("content", String("Directory containing exported Content assets"), true),
                ("usmap", String("Mapping file compatible with exported assets"), true),
                ("sdk_source", String("Generated Polaris.Sdk source directory"), true),
                ("output", String("Separate output directory"), true),
                ("dry_run", Boolean("Compile and validate without writing packages (default true)"), false),
                ("pak", Boolean("Also generate a pak in output directory (default false)"), false)),
            Access = AgentToolAccess.Write, OffUiThread = true,
            Run = async (args, ct) =>
            {
                var cli = ExistingFile(args.Str("cli"), ".dll");
                var mod = ExistingFile(args.Str("mod"), ".cs");
                var content = ExistingDirectory(args.Str("content"));
                var usmap = ExistingFile(args.Str("usmap"), ".usmap");
                var sdk = ExistingDirectory(args.Str("sdk_source"));
                var output = FullPath(args.Str("output"));
                if (Within(output, content) || Within(content, output))
                    throw new AgentToolException("Output and Content must be separate directories.");
                if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                    throw new AgentToolException("Output directory is not empty; choose a fresh directory to avoid overwriting files.");
                Directory.CreateDirectory(output);
                var dry = args.Bool("dry_run", true);
                var pak = args.Bool("pak", false);
                if (dry && pak) throw new AgentToolException("pak cannot be requested during dry-run.");
                var argv = new List<string> { "apply", "--mod", mod, "--content", content, "--usmap", usmap,
                    "--sdk-source", sdk, "--out", output, "--work", Path.Combine(output, ".polaris-work") };
                if (dry) argv.Add("--dry-run");
                if (pak) { argv.Add("--pak"); argv.Add(Path.Combine(output, "mod.pak")); }
                return await Run(cli, argv, ct);
            },
        },
    ];

    private static string FullPath(string path) => Path.GetFullPath(path);
    private static bool Within(string child, string parent)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
    private static string ExistingDirectory(string path)
    {
        path = FullPath(path);
        return Directory.Exists(path) ? path : throw new AgentToolException($"Directory not found: {path}");
    }
    private static string ExistingFile(string path, string extension)
    {
        path = FullPath(path);
        return File.Exists(path) && Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
            ? path : throw new AgentToolException($"Expected an existing {extension} file: {path}");
    }
    private static async Task<string> Run(string cli, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AgentToolException("Unable to launch dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var result = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new AgentToolException($"Polaris exited {process.ExitCode}:\n{result}");
        return result;
    }
}
