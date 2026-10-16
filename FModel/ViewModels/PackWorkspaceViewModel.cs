using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using FModel.Framework;
using FModel.Framework.Packing;
using FModel.Localization;
using FModel.Services;
using Serilog;

namespace FModel.ViewModels;

/// <summary>A folder in the tree of a pack source.</summary>
public sealed class PackFolderNode : ViewModel
{
    private bool _isExpanded;
    private bool _isSelected;

    public string Name { get; init; } = "";

    /// <summary>The folder path inside the source ("" for the root).</summary>
    public string Path { get; init; } = "";

    public ObservableCollection<PackFolderNode> Children { get; } = new();
    public List<PackSourceFile> Files { get; } = new();

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public int TotalFiles => Files.Count + Children.Sum(o => o.TotalFiles);

    public override string ToString() => Name;
}

public sealed class PackWorkspaceViewModel : ViewModel
{
    private static string L(string key) => LocalizationManager.Instance.Get(key);

    private PackProject _project = PackProject.CreateNew();
    private PackSource? _selectedSource;
    private PackFolderNode? _root;
    private PackFolderNode? _selectedFolder;
    private string _status = "";
    private string _packFilter = "";
    private string _browseFilter = "";
    private bool _isBusy;

    public PackWorkspaceViewModel()
    {
        PackView = CollectionViewSource.GetDefaultView(_project.Items);
        PackView.Filter = PackFilterPredicate;
        BrowserFiles = new ObservableCollection<PackSourceFile>();
        Status = L("Pack_StatusReady");
    }

    public PackProject Project
    {
        get => _project;
        private set
        {
            if (SetProperty(ref _project, value))
            {
                PackView = CollectionViewSource.GetDefaultView(_project.Items);
                PackView.Filter = PackFilterPredicate;
                RaisePropertyChanged(nameof(PackView));
                RaisePropertyChanged(nameof(Title));
                RaisePropertyChanged(nameof(Summary));
            }
        }
    }

    public ICollectionView PackView { get; private set; }

    public ObservableCollection<PackSource> Sources { get; } = new();
    public ObservableCollection<PackSourceFile> BrowserFiles { get; }

    /// <summary>A single root node (the tree control binds to a list).</summary>
    public ObservableCollection<PackFolderNode> Roots { get; } = new();

    public PackSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
                _ = LoadSourceAsync(value);
        }
    }

    public PackFolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
                RefreshBrowser();
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string PackFilter
    {
        get => _packFilter;
        set { if (SetProperty(ref _packFilter, value)) PackView.Refresh(); }
    }

    public string BrowseFilter
    {
        get => _browseFilter;
        set { if (SetProperty(ref _browseFilter, value)) RefreshBrowser(); }
    }

    public string Title => (Project.FilePath != null ? Path.GetFileNameWithoutExtension(Project.FilePath) : Project.Name);

    public string Summary => string.Format(L("Pack_Summary"), Project.Items.Count, FormatSize(Project.Items.Sum(o => o.Size)));

    private bool PackFilterPredicate(object o) =>
        string.IsNullOrWhiteSpace(_packFilter) || o is PackItem item && item.PakPath.Contains(_packFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    #region sources

    /// <summary>Adds the loaded game as a source (once).</summary>
    public void EnsureGameSource()
    {
        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        if (cue4?.Provider == null || cue4.Provider.Files.Count == 0)
            return;
        if (Sources.OfType<ArchiveSource>().Any(o => o.Name == GameSourceName(cue4)))
            return;
        var version = cue4.Provider.MountedVfs.OfType<CUE4Parse.UE4.Pak.PakFileReader>().FirstOrDefault() is { } pak ? (int) pak.Info.Version : (int?) null;
        var source = new ArchiveSource(GameSourceName(cue4), cue4.Provider, null, version);
        Sources.Insert(0, source);
        SelectedSource ??= source;
    }

    private static string GameSourceName(CUE4ParseViewModel cue4) => $"{L("Pack_SourceGame")} ({cue4.Provider.ProjectName})";

    /// <summary>The source of the loaded game (null before a game is loaded).</summary>
    public ArchiveSource? GameSource()
    {
        EnsureGameSource();
        return Sources.OfType<ArchiveSource>().FirstOrDefault(o => o.Name.StartsWith(L("Pack_SourceGame"), StringComparison.Ordinal));
    }

    public void AddFolderSource(string folder)
    {
        var source = new FolderSource(folder);
        Sources.Add(source);
        SelectedSource = source;
    }

    public async Task AddPakSourceAsync(string pakFile)
    {
        IsBusy = true;
        Status = string.Format(L("Pack_StatusOpening"), Path.GetFileName(pakFile));
        try
        {
            var cue4 = ApplicationService.ApplicationView.CUE4Parse;
            var source = await Task.Run(() => PakFileSource.Open(pakFile, cue4.Provider.Versions, cue4.Provider.Keys));
            Sources.Add(source);
            SelectedSource = source;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to open {Pak} as a pack source", pakFile);
            Status = string.Format(L("Pack_StatusFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSourceAsync(PackSource? source)
    {
        Roots.Clear();
        BrowserFiles.Clear();
        SelectedFolder = null;
        if (source == null)
            return;

        IsBusy = true;
        try
        {
            var root = await Task.Run(() => BuildTree(source));
            if (!ReferenceEquals(source, SelectedSource))
                return;
            root.IsExpanded = true;
            Roots.Add(root);
            root.IsSelected = true;
            SelectedFolder = root;
            Status = string.Format(L("Pack_StatusSource"), source.Name, source.Files.Count);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to list {Source}", source.Name);
            Status = string.Format(L("Pack_StatusFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static PackFolderNode BuildTree(PackSource source)
    {
        var root = new PackFolderNode { Name = source.Name, Path = "" };
        var nodes = new Dictionary<string, PackFolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        var children = new Dictionary<PackFolderNode, List<PackFolderNode>>();

        PackFolderNode Node(string path)
        {
            if (nodes.TryGetValue(path, out var existing))
                return existing;
            var slash = path.LastIndexOf('/');
            var parent = Node(slash < 0 ? "" : path[..slash]);
            var node = new PackFolderNode { Name = slash < 0 ? path : path[(slash + 1)..], Path = path };
            nodes[path] = node;
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<PackFolderNode>();
            list.Add(node);
            return node;
        }

        foreach (var file in source.Files)
        {
            var slash = file.Path.LastIndexOf('/');
            Node(slash < 0 ? "" : file.Path[..slash]).Files.Add(file);
        }
        foreach (var (parent, list) in children)
            foreach (var child in list.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                parent.Children.Add(child);
        return root;
    }

    private void RefreshBrowser()
    {
        BrowserFiles.Clear();
        if (SelectedFolder == null || SelectedSource == null)
            return;

        var filter = BrowseFilter.Trim();
        IEnumerable<PackSourceFile> files = filter.Length == 0
            ? SelectedFolder.Files
            : SelectedSource.Files.Where(o => o.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(2000);
        foreach (var file in files.OrderBy(o => o.Path, StringComparer.OrdinalIgnoreCase))
            BrowserFiles.Add(file);
    }

    /// <summary>All files under a folder of the selected source.</summary>
    public IEnumerable<string> FilesUnder(PackFolderNode folder)
    {
        foreach (var file in folder.Files)
            yield return file.Path;
        foreach (var child in folder.Children)
            foreach (var path in FilesUnder(child))
                yield return path;
    }

    #endregion

    #region adding files

    /// <summary>Adds files of a source to the pak unchanged, with the package parts that belong to them.</summary>
    public async Task<int> AddFromSourceAsync(PackSource source, IEnumerable<string> paths, string targetFolder = "")
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return 0;

        IsBusy = true;
        Status = string.Format(L("Pack_StatusAdding"), list.Count);
        var added = 0;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extracted = await Task.Run(() =>
            {
                var result = new List<(string Path, byte[] Data, string Origin)>();
                foreach (var path in list)
                {
                    try
                    {
                        foreach (var (name, data) in source.Extract(path))
                            if (seen.Add(name))
                                result.Add((name, data, source.Describe(name)));
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "Could not add {Path} from {Source}", path, source.Name);
                    }
                }
                return result;
            });

            foreach (var (name, data, origin) in extracted)
            {
                var target = string.IsNullOrEmpty(targetFolder) ? name : $"{targetFolder.Trim('/')}/{Path.GetFileName(name)}";
                Project.AddFile(target, data, origin);
                added++;
            }
            Status = string.Format(L("Pack_StatusAdded"), added);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to add files to the pack");
            Status = string.Format(L("Pack_StatusFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            PackView.Refresh();
            RaisePropertyChanged(nameof(Summary));
        }
        return added;
    }

    /// <summary>Adds files and folders from disk; the pak path is the target folder plus the path relative to what was dropped.</summary>
    public async Task<int> AddDiskPathsAsync(IEnumerable<string> paths, string targetFolder)
    {
        IsBusy = true;
        var added = 0;
        try
        {
            added = await Task.Run(() =>
            {
                var count = 0;
                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? path;
                        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            var relative = Path.GetRelativePath(parent, file).Replace('\\', '/');
                            Project.AddFile(Join(targetFolder, relative), File.ReadAllBytes(file), file);
                            count++;
                        }
                    }
                    else if (File.Exists(path))
                    {
                        Project.AddFile(Join(targetFolder, Path.GetFileName(path)), File.ReadAllBytes(path), path);
                        count++;
                        if (PackPaths.IsPackagePart(path))
                        {
                            foreach (var part in PackPaths.PackageParts)
                            {
                                var sibling = Path.ChangeExtension(path, part);
                                if (File.Exists(sibling) && !paths.Contains(sibling, StringComparer.OrdinalIgnoreCase) && !string.Equals(sibling, path, StringComparison.OrdinalIgnoreCase))
                                {
                                    Project.AddFile(Join(targetFolder, Path.GetFileName(sibling)), File.ReadAllBytes(sibling), sibling);
                                    count++;
                                }
                            }
                        }
                    }
                }
                return count;
            });
            Status = string.Format(L("Pack_StatusAdded"), added);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to add disk files to the pack");
            Status = string.Format(L("Pack_StatusFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            PackView.Refresh();
            RaisePropertyChanged(nameof(Summary));
        }
        return added;
    }

    private static string Join(string folder, string name) => string.IsNullOrWhiteSpace(folder) ? name : $"{folder.Trim('/')}/{name}";

    public void Remove(IEnumerable<PackItem> items)
    {
        foreach (var item in items.ToList())
            Project.Remove(item);
        PackView.Refresh();
        RaisePropertyChanged(nameof(Summary));
        Status = L("Pack_StatusRemoved");
    }

    public string? Rename(PackItem item, string newPath)
    {
        var error = Project.Rename(item, newPath);
        PackView.Refresh();
        return error;
    }

    #endregion

    #region project

    public void NewProject()
    {
        Project = PackProject.CreateNew();
        Status = L("Pack_StatusNewProject");
    }

    public string? OpenProject(string file)
    {
        try
        {
            Project = PackProject.Load(file);
            Status = string.Format(L("Pack_StatusOpened"), Path.GetFileName(file));
            return null;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to open pack project {File}", file);
            return e.Message;
        }
    }

    public string? SaveProject(string file)
    {
        try
        {
            Project.Name = Path.GetFileNameWithoutExtension(file);
            Project.Save(file);
            RaisePropertyChanged(nameof(Title));
            Status = string.Format(L("Pack_StatusSaved"), Path.GetFileName(file));
            return null;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to save pack project {File}", file);
            return e.Message;
        }
    }

    public async Task<string?> BuildAsync(string output)
    {
        IsBusy = true;
        Status = L("Pack_StatusBuilding");
        try
        {
            var result = await Task.Run(() => PackBuildService.Build(Project, output));
            Project.Settings.OutputPath = output;
            Status = string.Format(L("Pack_StatusBuilt"), result.FileCount, FormatSize(result.PakSize), Path.GetFileName(output));
            return null;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to build {Pak}", output);
            Status = string.Format(L("Pack_StatusFailed"), e.Message);
            return e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds files an editor produced (blueprint...) at their pak paths, marked as edited.</summary>
    public void AddEditedFiles(IEnumerable<(string PakPath, byte[] Data)> files, string origin)
    {
        var count = 0;
        foreach (var (path, data) in files)
        {
            Project.AddFile(path, data, origin, modified: true);
            count++;
        }
        PackView.Refresh();
        RaisePropertyChanged(nameof(Summary));
        Status = string.Format(L("Pack_StatusAdded"), count);
    }

    /// <summary>Replaces the bytes of a file of the pak (it keeps its path) and marks it as edited.</summary>
    public void ReplaceContent(PackItem item, byte[] data, string origin)
    {
        Project.AddFile(item.PakPath, data, origin, modified: true);
        PackView.Refresh();
        RaisePropertyChanged(nameof(Summary));
        Status = string.Format(L("Pack_StatusReplaced"), item.FileName);
    }

    public void RefreshSummary() => RaisePropertyChanged(nameof(Summary));

    #endregion
}
