using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using FModel.ViewModels;
using UAssetAPI;
using UAssetAPI.ExportTypes;

namespace FModel.Framework.BlueprintEditing;

public sealed class BlueprintSaveResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? AssetPath { get; init; }
    public string? PakPath { get; init; }
}

/// <summary>
/// Edits of one blueprint opened in the viewer.
/// Class default changes are applied to <see cref="Working"/> right away; bytecode constant changes stay pending and are
/// patched into a copy at save time, always against the original bytecode offsets the graph was decompiled from.
/// </summary>
public sealed class BlueprintEditSession : IDisposable
{
    private readonly CUE4ParseViewModel _owner;
    private readonly string _stagedUassetPath;
    private readonly string _workDirectory;
    private readonly Dictionary<(string Function, uint Offset), ConstantEdit> _constants = new();
    private int _saveCounter;

    public GameFile Entry { get; }
    public UAsset Working { get; private set; }
    public CdoEditor? Cdo { get; private set; }

    /// <summary>Number of CDO properties changed, added or removed since loading (or the last discard).</summary>
    public int CdoChangeCount { get; private set; }
    public int ConstantChangeCount => _constants.Count;
    public int PendingCount => CdoChangeCount + ConstantChangeCount;
    public bool IsDirty => PendingCount > 0;

    /// <summary>Changes made since the last successful save.</summary>
    public bool HasUnsavedChanges { get; private set; }

    public event Action? Changed;

    private BlueprintEditSession(CUE4ParseViewModel owner, GameFile entry, UAsset asset, string stagedUassetPath, string workDirectory)
    {
        _owner = owner;
        Entry = entry;
        Working = asset;
        _stagedUassetPath = stagedUassetPath;
        _workDirectory = workDirectory;
        Cdo = CdoEditor.TryCreate(asset);
    }

    /// <summary>Loads the package with UAssetAPI (slow: stages the package and its dependencies).</summary>
    public static BlueprintEditSession Open(CUE4ParseViewModel owner, GameFile entry)
    {
        var asset = owner.LoadBlueprintAssetForEditing(entry, out var staged, out var work);
        return new BlueprintEditSession(owner, entry, asset, staged, work);
    }

    public void MarkCdoChanged()
    {
        CdoChangeCount++;
        HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    public void SetConstant(ConstantEdit edit)
    {
        _constants[(edit.FunctionName, edit.Offset)] = edit;
        HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    public IEnumerable<ConstantEdit> Constants => _constants.Values;

    /// <summary>Drops every pending change and reloads the staged original.</summary>
    public void Discard()
    {
        Working = CUE4ParseViewModel.ReadBlueprintAssetLike(Working, _stagedUassetPath);
        Cdo = CdoEditor.TryCreate(Working);
        _constants.Clear();
        CdoChangeCount = 0;
        HasUnsavedChanges = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Builds the edited package, verifies it and writes the requested outputs.
    /// </summary>
    /// <param name="verify">Checks the re-read package (e.g. re-decompiles the edited graphs); returns an error or null.</param>
    public BlueprintSaveResult Save(bool exportAsset, bool createPak, Func<IPackage, string?> verify)
    {
        var stage = "write";
        try
        {
            // 1. serialize the working copy (class defaults) and read it back: this gives a fresh copy to patch
            var directory = Path.Combine(_workDirectory, "_save", (++_saveCounter).ToString());
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(_stagedUassetPath));
            Working.Write(path);

            stage = "reload";
            var edited = CUE4ParseViewModel.ReadBlueprintAssetLike(Working, path);

            // 2. bytecode constants
            stage = "patch";
            if (_constants.Count > 0)
            {
                KismetConstantPatcher.Apply(edited, _constants.Values.ToList());
                var problems = edited.Exports.OfType<StructExport>()
                    .Where(o => o.ScriptBytecode != null)
                    .SelectMany(o => KismetConstantPatcher.Validate(edited, o))
                    .Concat(KismetConstantPatcher.ValidateUbergraphEntryPoints(edited))
                    .ToList();
                if (problems.Count > 0)
                    return Fail(stage, string.Join(Environment.NewLine, problems.Take(10)));
            }

            // 3. verify with CUE4Parse, the reader the game-side tools and the viewer use
            stage = "verify";
            edited.Write(path);
            var package = _owner.ReadLegacyPackageFromDisk(Entry, path);
            _ = package.GetExports().ToList();
            if (verify(package) is { } error)
                return Fail(stage, error);

            // 4. outputs
            string? assetPath = null, pakPath = null;
            if (createPak)
            {
                stage = "pak";
                if (!_owner.CreateEditedBlueprintPatchPak(Entry, edited, out pakPath, out var pakError))
                    return Fail(stage, pakError);
            }
            if (exportAsset)
            {
                stage = "export";
                assetPath = _owner.WriteEditedBlueprint(Entry, edited);
            }

            HasUnsavedChanges = false;
            return new BlueprintSaveResult { Success = true, AssetPath = assetPath, PakPath = pakPath };
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to save blueprint edits of {Path} at stage {Stage}", Entry.Path, stage);
            return Fail(stage, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static BlueprintSaveResult Fail(string stage, string message) => new() { Success = false, Message = $"[{stage}] {message}" };

    public void Dispose() => CUE4ParseViewModel.DeleteBlueprintEditDirectory(_workDirectory);
}
