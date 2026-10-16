using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FModel.Framework;
using Newtonsoft.Json;

namespace FModel.Framework.Packing;

/// <summary>One file of the pak being built: where it goes in the pak and the copy of its bytes in the workspace.</summary>
public sealed class PackItem : ViewModel
{
    private string _pakPath = "";
    private string _origin = "";
    private long _size;
    private bool _modified;

    /// <summary>Path inside the pak, relative to the mount point (no leading slash).</summary>
    public string PakPath
    {
        get => _pakPath;
        set => SetProperty(ref _pakPath, PackPaths.Normalize(value));
    }

    /// <summary>Where the file came from: an archive path, a disk path or the editor that produced it.</summary>
    public string Origin
    {
        get => _origin;
        set => SetProperty(ref _origin, value ?? "");
    }

    public long Size
    {
        get => _size;
        set => SetProperty(ref _size, value);
    }

    /// <summary>Changed in the workspace since it was added.</summary>
    public bool Modified
    {
        get => _modified;
        set => SetProperty(ref _modified, value);
    }

    /// <summary>The file in the workspace (set by the project).</summary>
    [JsonIgnore] public string DiskPath { get; set; } = "";

    [JsonIgnore] public string Extension => Path.GetExtension(PakPath).TrimStart('.').ToLowerInvariant();

    [JsonIgnore] public string FileName => Path.GetFileName(PakPath);

    [JsonIgnore] public string Folder => Path.GetDirectoryName(PakPath)?.Replace('\\', '/') ?? "";
}

public static class PackPaths
{
    public static string Normalize(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim().Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized;
    }

    public static bool IsValid(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Split('/').Any(o => o is "." or ".." || o.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);

    /// <summary>Extensions that belong to one package and are kept together.</summary>
    public static readonly string[] PackageParts = [".uasset", ".umap", ".uexp", ".ubulk", ".uptnl"];

    public static bool IsPackagePart(string path) => PackageParts.Contains(Path.GetExtension(path).ToLowerInvariant());
}

/// <summary>Pak writer settings saved with the project.</summary>
public sealed class PackSettings
{
    public string MountPoint { get; set; } = "../../../";

    /// <summary>The pak file version number (UAssetAPI.PakVersion).</summary>
    public int PakVersion { get; set; } = (int) UAssetAPI.PakVersion.V11;

    public bool Compress { get; set; }

    public string OutputPath { get; set; } = "";
}

/// <summary>
/// A packing project: the files of the pak to build, kept as copies in a workspace folder, saved as a small json file.
/// </summary>
public sealed class PackProject
{
    private const int FormatVersion = 1;

    private sealed class ItemRecord
    {
        public string PakPath { get; set; } = "";
        public string File { get; set; } = "";
        public string Origin { get; set; } = "";
        public bool Modified { get; set; }
    }

    private sealed class ProjectRecord
    {
        public int Version { get; set; } = FormatVersion;
        public string Name { get; set; } = "";
        public PackSettings Settings { get; set; } = new();
        public List<ItemRecord> Items { get; set; } = [];
    }

    public string Name { get; set; } = "Untitled";
    public PackSettings Settings { get; } = new();
    public RangeObservableCollection<PackItem> Items { get; } = new();

    /// <summary>The project file, or null before the first save.</summary>
    public string? FilePath { get; private set; }

    /// <summary>The folder holding the copies of the files.</summary>
    public string WorkspaceDirectory { get; private set; }

    private PackProject(string workspace) => WorkspaceDirectory = workspace;

    public static PackProject CreateNew()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "FloraPack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return new PackProject(workspace);
    }

    public string DiskPathOf(string pakPath) => Path.Combine(WorkspaceDirectory, "files", pakPath.Replace('/', Path.DirectorySeparatorChar));

    public PackItem? Find(string pakPath) => Items.FirstOrDefault(o => string.Equals(o.PakPath, pakPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the bytes as a file of the pak, replacing a file with the same path.</summary>
    public PackItem AddFile(string pakPath, byte[] data, string origin, bool modified = false)
    {
        pakPath = PackPaths.Normalize(pakPath);
        if (!PackPaths.IsValid(pakPath))
            throw new ArgumentException($"'{pakPath}' is not a valid path inside a pak.");

        var disk = DiskPathOf(pakPath);
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        File.WriteAllBytes(disk, data);

        var item = Find(pakPath);
        if (item == null)
        {
            item = new PackItem { PakPath = pakPath, DiskPath = disk };
            Items.Add(item);
        }
        item.DiskPath = disk;
        item.Origin = origin;
        item.Size = data.LongLength;
        item.Modified = modified;
        return item;
    }

    /// <summary>Changes the path of a file inside the pak (moves its copy in the workspace).</summary>
    public string? Rename(PackItem item, string newPath)
    {
        newPath = PackPaths.Normalize(newPath);
        if (!PackPaths.IsValid(newPath))
            return $"'{newPath}' is not a valid path inside a pak.";
        if (string.Equals(newPath, item.PakPath, StringComparison.OrdinalIgnoreCase))
        {
            item.PakPath = newPath;
            return null;
        }
        if (Find(newPath) != null)
            return $"'{newPath}' already exists in the pak.";

        var disk = DiskPathOf(newPath);
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        File.Move(item.DiskPath, disk);
        item.PakPath = newPath;
        item.DiskPath = disk;
        return null;
    }

    public void Remove(PackItem item)
    {
        Items.Remove(item);
        try { File.Delete(item.DiskPath); } catch { /* the workspace is cleaned up later */ }
    }

    /// <summary>The files of the pak that belong to the same package as <paramref name="item"/>.</summary>
    public IEnumerable<PackItem> PackageOf(PackItem item)
    {
        var stem = Path.ChangeExtension(item.PakPath, null);
        return Items.Where(o => PackPaths.IsPackagePart(o.PakPath) && string.Equals(Path.ChangeExtension(o.PakPath, null), stem, StringComparison.OrdinalIgnoreCase));
    }

    #region save and load

    private string FilesDirectoryOf(string projectFile) => projectFile + ".files";

    public void Save(string projectFile)
    {
        projectFile = Path.GetFullPath(projectFile);
        var newWorkspace = FilesDirectoryOf(projectFile);
        if (!string.Equals(Path.GetFullPath(newWorkspace), Path.GetFullPath(WorkspaceDirectory), StringComparison.OrdinalIgnoreCase))
            MoveWorkspace(newWorkspace);

        var record = new ProjectRecord { Name = Name, Settings = Settings };
        foreach (var item in Items)
            record.Items.Add(new ItemRecord { PakPath = item.PakPath, File = Path.GetRelativePath(WorkspaceDirectory, item.DiskPath).Replace('\\', '/'), Origin = item.Origin, Modified = item.Modified });

        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        File.WriteAllText(projectFile, JsonConvert.SerializeObject(record, Formatting.Indented));
        FilePath = projectFile;
    }

    private void MoveWorkspace(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var item in Items)
        {
            var disk = Path.Combine(target, "files", item.PakPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            if (File.Exists(item.DiskPath))
                File.Copy(item.DiskPath, disk, true);
            item.DiskPath = disk;
        }
        WorkspaceDirectory = target;
    }

    public static PackProject Load(string projectFile)
    {
        projectFile = Path.GetFullPath(projectFile);
        var record = JsonConvert.DeserializeObject<ProjectRecord>(File.ReadAllText(projectFile))
                     ?? throw new InvalidDataException("The project file is empty.");
        if (record.Version > FormatVersion)
            throw new InvalidDataException($"The project was saved by a newer version of Flora (format {record.Version}).");

        var workspace = projectFile + ".files";
        var project = new PackProject(workspace) { Name = record.Name, FilePath = projectFile };
        project.Settings.MountPoint = record.Settings.MountPoint;
        project.Settings.PakVersion = record.Settings.PakVersion;
        project.Settings.Compress = record.Settings.Compress;
        project.Settings.OutputPath = record.Settings.OutputPath;
        foreach (var entry in record.Items)
        {
            var disk = Path.Combine(workspace, entry.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(disk))
                continue;
            project.Items.Add(new PackItem { PakPath = entry.PakPath, DiskPath = disk, Origin = entry.Origin, Modified = entry.Modified, Size = new FileInfo(disk).Length });
        }
        return project;
    }

    #endregion
}
