using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using Serilog;

namespace FModel.Framework.Packing;

public sealed record PackSourceFile(string Path, long Size)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Folder => Path.LastIndexOf('/') is var slash and > 0 ? Path[..slash] : "";
}

/// <summary>Somewhere files can be taken from to put into the pak: the loaded game, another pak or a folder.</summary>
public abstract class PackSource
{
    public abstract string Name { get; }

    /// <summary>Paths use '/', relative to the source root (the mount point for archives).</summary>
    public abstract IReadOnlyList<PackSourceFile> Files { get; }

    /// <summary>The bytes of the file; for packages (.uasset/.umap) the .uexp/.ubulk next to it come with it. Keys are source paths.</summary>
    public abstract IReadOnlyDictionary<string, byte[]> Extract(string path);

    /// <summary>A short description to put in the project ("Game: kards/Content/...").</summary>
    public virtual string Describe(string path) => $"{Name}: {path}";

    /// <summary>Pak version of the source archive, when it has one.</summary>
    public virtual int? PakVersion => null;
}

/// <summary>An archive mounted by a CUE4Parse provider (the game FModel has loaded, or a single pak opened for browsing).</summary>
public sealed class ArchiveSource : PackSource
{
    private readonly AbstractFileProvider _provider;
    private readonly Func<GameFile, bool>? _filter;
    private readonly Lazy<IReadOnlyList<PackSourceFile>> _files;
    private readonly string _name;

    public ArchiveSource(string name, AbstractFileProvider provider, Func<GameFile, bool>? filter = null, int? pakVersion = null)
    {
        _name = name;
        _provider = provider;
        _filter = filter;
        PakVersion = pakVersion;
        _files = new Lazy<IReadOnlyList<PackSourceFile>>(() =>
            provider.Files.Values.Where(o => filter == null || filter(o)).Select(o => new PackSourceFile(o.Path, o.Size)).ToList());
    }

    public override string Name => _name;
    public override IReadOnlyList<PackSourceFile> Files => _files.Value;
    public override int? PakVersion { get; }

    public override IReadOnlyDictionary<string, byte[]> Extract(string path)
    {
        if (!_provider.Files.TryGetValue(path, out var file))
            throw new FileNotFoundException($"{path} is not in {_name}.");

        if (PackPaths.IsPackagePart(path) && _provider.TrySavePackage(file, out var package) && package.Count > 0)
            return package;

        return new Dictionary<string, byte[]> { [path] = file.Read() };
    }
}

/// <summary>A folder on disk. Paths are relative to it.</summary>
public sealed class FolderSource : PackSource
{
    private readonly string _root;
    private readonly Lazy<IReadOnlyList<PackSourceFile>> _files;

    public FolderSource(string root)
    {
        _root = Path.GetFullPath(root);
        _files = new Lazy<IReadOnlyList<PackSourceFile>>(() =>
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Select(o => new PackSourceFile(Path.GetRelativePath(_root, o).Replace('\\', '/'), new FileInfo(o).Length))
                .ToList());
    }

    public override string Name => Path.GetFileName(_root.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : _root;
    public override IReadOnlyList<PackSourceFile> Files => _files.Value;
    public override string Describe(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    public override IReadOnlyDictionary<string, byte[]> Extract(string path)
    {
        var result = new Dictionary<string, byte[]>();
        void Add(string relative)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) result[relative] = File.ReadAllBytes(full);
        }

        Add(path);
        if (PackPaths.IsPackagePart(path))
            foreach (var part in PackPaths.PackageParts)
                Add(Path.ChangeExtension(path, part).Replace('\\', '/'));
        return result;
    }
}

/// <summary>Opens one .pak file for browsing (with the AES keys of the loaded game when it is encrypted).</summary>
public static class PakFileSource
{
    public static ArchiveSource Open(string pakFile, VersionContainer versions, IEnumerable<KeyValuePair<FGuid, FAesKey>>? keys)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(pakFile))!;
        var name = Path.GetFileName(pakFile);
        var provider = new DefaultFileProvider(new DirectoryInfo(directory), SearchOption.TopDirectoryOnly, versions, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        if (keys != null)
            provider.SubmitKeys(keys);

        int? version = provider.MountedVfs.Concat(provider.UnloadedVfs).OfType<PakFileReader>()
            .FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } pak ? (int) pak.Info.Version : null;
        Log.Information("Opened {Pak} for packing: {Count} files", pakFile, provider.Files.Count);
        return new ArchiveSource(name, provider, o => o is VfsEntry entry && string.Equals(entry.Vfs.Name, name, StringComparison.OrdinalIgnoreCase), version);
    }
}
