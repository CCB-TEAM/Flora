using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FModel.Framework.Mappings;

public class MappingCandidate
{
    public string Path { get; init; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string Kind { get; init; } = string.Empty;       // usmap / jmap
    public string Tool { get; init; } = string.Empty;       // UE4SS / Dumper-7 / FModel / unknown
    public string EngineVersion { get; init; } = string.Empty;
    public DateTime Modified { get; init; }
    public long Size { get; init; }
    public string SizeText => Size >= 1 << 20 ? $"{Size / (double) (1 << 20):0.#} MB" : $"{Size / 1024.0:0.#} KB";
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Looks for mapping files (.usmap / .jmap) written by UE4SS's dumpers, Dumper-7 or earlier FModel imports,
/// next to the game's executable and in Dumper-7's default output folder.
/// </summary>
public static class MappingDiscovery
{
    public const string Dumper7DefaultFolder = @"C:\Dumper-7";

    private static readonly Regex ToolRegex = new("\"tool\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex MajorRegex = new("\"major\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex MinorRegex = new("\"minor\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);

    /// <summary>Folders FModel will search for the given game directory (the one holding the Paks).</summary>
    public static IEnumerable<string> SearchRoots(string gameDirectory, string outputDirectory)
    {
        var roots = new List<string>();

        // Paks live in <Project>/Content/Paks; binaries in <Project>/Binaries/Win64 (a few levels up)
        var dir = string.IsNullOrWhiteSpace(gameDirectory) ? null : new DirectoryInfo(gameDirectory);
        for (var i = 0; dir != null && i < 5; i++, dir = dir.Parent)
        {
            var binaries = System.IO.Path.Combine(dir.FullName, "Binaries");
            if (Directory.Exists(binaries))
                roots.Add(binaries);
        }

        roots.Add(Dumper7DefaultFolder);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            roots.Add(System.IO.Path.Combine(outputDirectory, ".data"));

        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static List<MappingCandidate> Scan(string gameDirectory, string outputDirectory)
    {
        var result = new Dictionary<string, MappingCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SearchRoots(gameDirectory, outputDirectory))
        {
            foreach (var file in EnumerateMappingFiles(root, 4))
            {
                try
                {
                    var candidate = Describe(file);
                    result[candidate.Path] = candidate;
                }
                catch (Exception)
                {
                    // unreadable file, ignore it
                }
            }
        }

        return result.Values.OrderByDescending(c => c.Modified).ToList();
    }

    public static MappingCandidate Describe(string path)
    {
        var info = new FileInfo(path);
        var isJmap = path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase);
        var tool = string.Empty;
        var version = string.Empty;

        if (isJmap)
        {
            var head = ReadHead(path, 8192);
            if (ToolRegex.Match(head) is { Success: true } t)
                tool = t.Groups[1].Value.Contains("UE4SS", StringComparison.OrdinalIgnoreCase) ? "UE4SS" : t.Groups[1].Value;
            if (MajorRegex.Match(head) is { Success: true } major && MinorRegex.Match(head) is { Success: true } minor)
                version = $"{major.Groups[1].Value}.{minor.Groups[1].Value}";
        }
        else if (path.Contains("Dumper-7", StringComparison.OrdinalIgnoreCase))
        {
            tool = "Dumper-7";
        }

        if (string.IsNullOrEmpty(tool))
            tool = path.Contains(System.IO.Path.DirectorySeparatorChar + ".data" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? "FModel"
                : "unknown";

        return new MappingCandidate
        {
            Path = info.FullName,
            Kind = isJmap ? "jmap" : "usmap",
            Tool = tool,
            EngineVersion = version,
            Modified = info.LastWriteTime,
            Size = info.Length
        };
    }

    private static string ReadHead(string path, int count)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static IEnumerable<string> EnumerateMappingFiles(string root, int depth)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root).Where(IsMappingFile).ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files)
            yield return file;

        if (depth <= 0)
            yield break;

        IEnumerable<string> subDirectories;
        try
        {
            subDirectories = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var sub in subDirectories)
        foreach (var file in EnumerateMappingFiles(sub, depth - 1))
            yield return file;
    }

    private static bool IsMappingFile(string path) =>
        path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase);
}
