using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
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
    private readonly Dictionary<(string Function, WireEditKind Group, uint Offset), WireEdit> _wires = new();
    private readonly Dictionary<(string Function, ObjectEditKind Kind, uint Offset), ObjectEdit> _objects = new();
    private readonly Dictionary<(string Function, string Name), LocalDeclaration> _locals = new();
    private int _saveCounter;

    public GameFile Entry { get; }
    public UAsset Working { get; private set; }
    public CdoEditor? Cdo { get; private set; }

    /// <summary>Number of CDO properties changed, added or removed since loading (or the last discard).</summary>
    public int CdoChangeCount { get; private set; }
    public int ConstantChangeCount => _constants.Count;
    public int WireChangeCount => _wires.Count;
    public int ObjectChangeCount => _objects.Count + _locals.Count;
    public int PendingCount => CdoChangeCount + ConstantChangeCount + WireChangeCount + ObjectChangeCount;

    /// <summary>What the last save or validation added to the package for object edits (names, imports).</summary>
    public IReadOnlyList<string> LastNotes { get; private set; } = [];

    /// <summary>What the last build changes in behavior (control flow of the edited functions against the original), separate from whether it is well-formed.</summary>
    public IReadOnlyList<string> LastBehavior { get; private set; } = [];

    /// <summary>The package of the last build, for comparing edited functions with the original.</summary>
    public UAsset? LastEdited { get; private set; }
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

    public IEnumerable<WireEdit> Wires => _wires.Values;

    public void SetWire(WireEdit edit)
    {
        _wires[edit.Key] = edit;
        HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    public void RemoveWire(WireEdit edit)
    {
        if (_wires.Remove(edit.Key))
            Changed?.Invoke();
    }

    public IEnumerable<ObjectEdit> Objects => _objects.Values;

    public IEnumerable<LocalDeclaration> Locals => _locals.Values;

    public void SetLocal(LocalDeclaration declaration)
    {
        _locals[declaration.Key] = declaration;
        HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    public void RemoveLocal(LocalDeclaration declaration)
    {
        if (_locals.Remove(declaration.Key))
            Changed?.Invoke();
    }

    public void SetObject(ObjectEdit edit)
    {
        _objects[edit.Key] = edit;
        HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    public void RemoveObject(ObjectEdit edit)
    {
        if (_objects.Remove(edit.Key))
            Changed?.Invoke();
    }

    /// <summary>Drops a pending constant change (the bytecode keeps its original value).</summary>
    public void RemoveConstant(string functionName, uint offset)
    {
        if (_constants.Remove((functionName, offset)))
            Changed?.Invoke();
    }

    /// <summary>Drops every pending change and reloads the staged original.</summary>
    public void Discard()
    {
        Working = CUE4ParseViewModel.ReadBlueprintAssetLike(Working, _stagedUassetPath);
        Cdo = CdoEditor.TryCreate(Working);
        _constants.Clear();
        _wires.Clear();
        _objects.Clear();
        _locals.Clear();
        CdoChangeCount = 0;
        HasUnsavedChanges = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Builds the edited package, verifies it and writes the requested outputs.
    /// </summary>
    /// <param name="verify">Checks the re-read package (e.g. re-decompiles the edited graphs); returns an error or null.</param>
    /// <remarks>With neither output requested this is a dry run: the edits are built and verified, nothing is written and they stay unsaved.</remarks>
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
            LastNotes = [];
            if (_locals.Count > 0)
            {
                stage = "patch: declare locals";
                LastNotes = [.. LastNotes, .. KismetLocals.Apply(edited, _locals.Values.ToList())];
            }
            if (_constants.Count > 0 || _wires.Count > 0 || _objects.Count > 0)
            {
                stage = "patch: bytecode";
                LastNotes = [.. LastNotes, .. KismetConstantPatcher.Apply(edited, _constants.Values.ToList(), _wires.Values.ToList(), _objects.Values.ToList())];
                var problems = KismetConstantPatcher.Validate(edited);
                if (problems.Count > 0)
                    return Fail(stage, string.Join(Environment.NewLine, problems.Take(10)));
            }

            // 3. verify with CUE4Parse, the reader the game-side tools and the viewer use
            LastEdited = edited;
            LastBehavior = BehaviorChecks(edited);
            stage = "verify: write the patched package (UAssetAPI)";
            edited.Write(path);
            stage = "verify: read it back (CUE4Parse)";
            var package = _owner.ReadLegacyPackageFromDisk(Entry, path);
            var exports = package.GetExports().ToList();
            stage = "verify: compare bytecode sizes";
            if (CompareBytecodeSizes(edited, exports) is { } sizeError)
                return Fail(stage, sizeError);
            stage = "verify: decompile the edited functions";
            if (verify(package) is { } error)
                return Fail(stage, error);

            // 4. outputs
            string? assetPath = null, pakPath = null;
            if (createPak)
            {
                stage = "pack";
                if (!_owner.AddEditedBlueprintToPack(Entry, edited, out pakPath, out var pakError))
                    return Fail(stage, pakError);
            }
            if (exportAsset)
            {
                stage = "export";
                assetPath = _owner.WriteEditedBlueprint(Entry, edited);
            }

            if (exportAsset || createPak)
                HasUnsavedChanges = false;
            return new BlueprintSaveResult { Success = true, AssetPath = assetPath, PakPath = pakPath };
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to save blueprint edits of {Path} at stage {Stage}", Entry.Path, stage);
            return Fail(stage, ExceptionText.Describe(e));
        }
    }

    /// <summary>
    /// UAssetAPI lays the edited bytecode out with its own size rules; CUE4Parse must read every edited function back to the same size,
    /// or the offsets written into the code would point elsewhere in the game.
    /// </summary>
    private string? CompareBytecodeSizes(UAsset edited, List<UObject> exports)
    {
        foreach (var functionName in EditedFunctions)
        {
            var written = KismetConstantPatcher.FindFunction(edited, functionName);
            var read = exports.OfType<UStruct>().FirstOrDefault(o => o.Name == functionName && o.ScriptBytecode != null);
            if (written == null || read == null)
                return $"{functionName} has no bytecode after saving.";

            if (read.ScriptBytecodeError is { } readError)
                return $"{functionName}: CUE4Parse can't read the saved bytecode ({readError.Message}).";

            // the layout the offsets were computed with, the size written in the header and the size read back must agree
            uint expected = 0;
            foreach (var statement in written.ScriptBytecode)
                expected += statement.GetSize(edited);
            var actual = read.ScriptBytecode.Length == 0 ? 0 : read.ScriptBytecode[^1].EndIndex;
            if (actual != expected || read.BytecodeBufferSize != expected)
                return $"{functionName}: bytecode size mismatch (UAssetAPI layout {expected}, header {read.BytecodeBufferSize}, CUE4Parse {actual}).";
        }
        return null;
    }

    private List<string> BehaviorChecks(UAsset edited)
    {
        var result = new List<string>();
        foreach (var functionName in EditedFunctions)
        {
            try
            {
                var before = KismetConstantPatcher.FindFunction(Working, functionName);
                var after = KismetConstantPatcher.FindFunction(edited, functionName);
                if (before == null || after == null)
                    continue;
                var findings = KismetFlowAudit.NewFindings(KismetFlowAudit.Audit(Working, before), KismetFlowAudit.Audit(edited, after));
                result.AddRange(findings.Select(o => $"{functionName}: {o}"));
            }
            catch (Exception e)
            {
                result.Add($"{functionName}: the control-flow check failed ({e.GetType().Name}: {e.Message})");
            }
        }
        return result;
    }

    /// <summary>Functions whose bytecode the pending edits change.</summary>
    public IEnumerable<string> EditedFunctions =>
        _constants.Values.Select(o => o.FunctionName).Concat(_wires.Values.Select(o => o.FunctionName)).Concat(_objects.Values.Select(o => o.FunctionName))
            .Concat(_locals.Values.Select(o => o.FunctionName)).Distinct(StringComparer.Ordinal);

    private static BlueprintSaveResult Fail(string stage, string message) => new() { Success = false, Message = $"[{stage}] {message}" };

    public void Dispose() => CUE4ParseViewModel.DeleteBlueprintEditDirectory(_workDirectory);
}
