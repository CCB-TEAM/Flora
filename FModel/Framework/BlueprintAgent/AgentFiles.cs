using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FModel.Framework.Packing;
using FModel.ViewModels;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Structured descriptions of the files a tool wrote, for the next step to pick up: where, how big, and a hash to check copies against.</summary>
public static class AgentFiles
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>A file on disk: its path, size and SHA-256.</summary>
    public static JObject Disk(string path) => new()
    {
        ["path"] = path,
        ["size"] = new FileInfo(path).Length,
        ["sha256"] = Sha256(path),
    };

    /// <summary>The package files next to a .uasset/.umap (.uexp, .ubulk...), the .uasset first.</summary>
    public static JArray PackageFiles(string uassetPath) =>
        new(PackPaths.PackageParts.Select(o => Path.ChangeExtension(uassetPath, o)).Where(File.Exists).Select(Disk));

    /// <summary>Files of the pak project by their pak paths, with the mount point they will have: path, size, hash, whether edited, where they came from.</summary>
    public static JArray PakItems(PackWorkspaceViewModel workspace, IEnumerable<string> pakPaths) =>
        new(pakPaths.Select(workspace.Project.Find).Where(o => o != null).Select(o => new JObject
        {
            ["pakPath"] = o.PakPath,
            ["size"] = o.Size,
            ["sha256"] = File.Exists(o.DiskPath) ? Sha256(o.DiskPath) : null,
            ["edited"] = o.Modified,
            ["origin"] = o.Origin,
        }));

    public static JObject PakManifest(PackWorkspaceViewModel workspace, string pakFile = null)
    {
        var manifest = new JObject
        {
            ["mountPoint"] = workspace.Project.Settings.MountPoint,
            ["pakVersion"] = ((UAssetAPI.PakVersion) workspace.Project.Settings.PakVersion).ToString(),
            ["compressed"] = workspace.Project.Settings.Compress,
            ["fileCount"] = workspace.Project.Items.Count,
            ["editedCount"] = workspace.Project.Items.Count(o => o.Modified),
            ["files"] = PakItems(workspace, workspace.Project.Items.Select(o => o.PakPath).OrderBy(o => o, StringComparer.OrdinalIgnoreCase)),
        };
        if (pakFile != null && File.Exists(pakFile))
            manifest["pak"] = Disk(pakFile);
        return manifest;
    }

    /// <summary>The pak paths a "a, b, c" list (as the packer reports added files) names.</summary>
    public static IEnumerable<string> SplitPaths(string list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
