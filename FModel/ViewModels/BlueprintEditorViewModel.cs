using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Kismet;
using FModel.BlueprintGraph.Decompiler;
using FModel.BlueprintGraph.Nodes;
using FModel.Framework;
using FModel.Framework.BlueprintEditing;
using FModel.Localization;

namespace FModel.ViewModels;

public class CdoPropertyItem : ViewModel
{
    private readonly CdoEditor _editor;

    public CdoProperty Property { get; }
    public string DisplayName => Property.DisplayName;
    public string TypeText => Property.TypeText;
    public string Owner => Property.Owner;
    public bool IsOverridden => Property.IsOverridden;
    public bool IsSimple => Property.IsSimple;

    private string _preview = string.Empty;
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public CdoPropertyItem(CdoEditor editor, CdoProperty property)
    {
        _editor = editor;
        Property = property;
        Refresh();
    }

    public void Refresh()
    {
        Preview = _editor.Preview(Property);
        RaisePropertyChanged(nameof(IsOverridden));
    }
}

/// <summary>A pending change listed in the Changes tab.</summary>
public abstract class EditEntry : ViewModel
{
    public GraphPin Pin { get; init; }
    public string FunctionName { get; init; }

    /// <summary>What the viewer lists for the function the pin lives in (used to jump back to it).</summary>
    public object Source { get; set; }

    public BPNode Node => Pin.ParentNode;
    public abstract string Summary { get; }
    public string Where => $"{FunctionName} / {Pin.ParentNode?.Name}";
}

/// <summary>A literal pin value changed in the graph and not saved yet.</summary>
public class PinEditEntry : EditEntry
{

    public string Original { get; init; }

    private string _current;
    public string Current
    {
        get => _current;
        set
        {
            if (SetProperty(ref _current, value))
                RaisePropertyChanged(nameof(Summary));
        }
    }

    /// <summary>How <see cref="Current"/> (the raw constant) is shown, e.g. an enumerator name.</summary>
    private string _currentDisplay;
    public string CurrentDisplay
    {
        get => _currentDisplay ?? _current;
        set
        {
            if (SetProperty(ref _currentDisplay, value))
                RaisePropertyChanged(nameof(Summary));
        }
    }

    public override string Summary => $"{Pin.PinFriendlyName ?? Pin.PinName}:  {Original}  ->  {CurrentDisplay}";
}

/// <summary>A wire changed in the graph and not saved yet. <see cref="EditEntry.Pin"/> is the input (data) or exec output that was rewired.</summary>
public class WireEditEntry : EditEntry
{
    public List<GraphPin> OriginalLinks { get; init; }
    public string OriginalValue { get; init; }

    private WireEdit _edit;
    public WireEdit Edit
    {
        get => _edit;
        set
        {
            if (SetProperty(ref _edit, value))
                RaisePropertyChanged(nameof(Summary));
        }
    }

    private string _description;
    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
                RaisePropertyChanged(nameof(Summary));
        }
    }

    public override string Summary => $"{Pin.PinFriendlyName ?? Pin.PinName}:  {Description}";
}

/// <summary>
/// Editing side of the blueprint viewer: the UAssetAPI session, class defaults, pin constants and saving.
/// </summary>
public class BlueprintEditorViewModel : ViewModel
{
    private readonly CUE4ParseViewModel _owner;
    private readonly GameFile _entry;
    private Task<bool> _loading;

    public BlueprintEditSession Session { get; private set; }

    /// <summary>Checks a re-read edited package; returns an error or null.</summary>
    public Func<IPackage, BlueprintEditSession, string> Verifier { get; set; }

    /// <summary>Turns a raw constant into the text shown on its pin (e.g. an enumerator name); null shows the raw value.</summary>
    public Func<GraphPin, string, string> FormatPinValue { get; set; }

    public ObservableCollection<CdoPropertyItem> CdoProperties { get; } = [];

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsLoaded => Session != null;
    public bool HasCdo => Session?.Cdo != null;
    public bool IsDirty => Session?.IsDirty == true;
    public bool HasUnsavedChanges => Session?.HasUnsavedChanges == true;
    public string PendingText => Session == null ? string.Empty : string.Format(L("BlueprintEditor_PendingWithWires"), Session.CdoChangeCount, Session.ConstantChangeCount, Session.WireChangeCount);
    public string CdoTitle => Session?.Cdo is { } cdo ? $"{cdo.Cdo.ObjectName} ({cdo.ClassName})" : string.Empty;

    private static string L(string key) => LocalizationManager.Instance.Get(key);

    static BlueprintEditorViewModel()
    {
        WirePlanner.Translate = key => LocalizationManager.Instance.TryGet(key, out var text) ? text : null;
    }

    public BlueprintEditorViewModel(CUE4ParseViewModel owner, GameFile entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public bool CanEdit => _owner != null;

    public ObservableCollection<EditEntry> PinEdits { get; } = [];

    private readonly Dictionary<GraphPin, PinEditEntry> _pinEntries = new();
    private readonly Dictionary<GraphPin, WireEditEntry> _wireEntries = new();
    private readonly Stack<EditStep> _undo = new();
    private readonly Stack<EditStep> _redo = new();

    /// <summary>One undoable change; <see cref="Pin"/> is what to show afterwards.</summary>
    private sealed record EditStep(GraphPin Pin, Action Undo, Action Redo);

    private void PushStep(EditStep step)
    {
        _undo.Push(step);
        _redo.Clear();
        RaiseHistoryProperties();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Loads the package with UAssetAPI once; later calls wait for the same load.</summary>
    public Task<bool> EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task<bool> LoadAsync()
    {
        if (_owner == null)
            return false;

        IsLoading = true;
        Status = L("BlueprintEditor_Loading");
        try
        {
            Session = await Task.Run(() => BlueprintEditSession.Open(_owner, _entry));
            Session.Changed += OnSessionChanged;
            RebuildCdoList();
            Status = Session.Cdo == null ? L("BlueprintEditor_NoCdo") : string.Empty;
            RaiseSessionProperties();
            return true;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to load {Path} for editing", _entry.Path);
            Status = string.Format(L("BlueprintEditor_LoadFailed"), e.Message);
            _loading = null; // allow retrying
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnSessionChanged() => RaiseSessionProperties();

    private void RaiseSessionProperties()
    {
        RaisePropertyChanged(nameof(IsLoaded));
        RaisePropertyChanged(nameof(HasCdo));
        RaisePropertyChanged(nameof(IsDirty));
        RaisePropertyChanged(nameof(PendingText));
        RaisePropertyChanged(nameof(CdoTitle));
    }

    private void RebuildCdoList()
    {
        CdoProperties.Clear();
        if (Session?.Cdo is not { } cdo)
            return;

        foreach (var property in cdo.ListProperties()
                     .OrderByDescending(o => o.IsOverridden)
                     .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            CdoProperties.Add(new CdoPropertyItem(cdo, property));
    }

    #region class defaults

    public IReadOnlyList<string> GetEnumValues(CdoPropertyItem item) => Session?.Cdo?.GetEnumValues(item.Property) ?? [];

    public string GetEditText(CdoPropertyItem item)
    {
        if (Session?.Cdo is not { } cdo || !item.IsOverridden)
            return string.Empty;
        return item.IsSimple ? CdoEditor.FormatValue(item.Property.Current) : cdo.GetJson(item.Property);
    }

    /// <summary>Applies text (simple value) or JSON; returns an error message or null.</summary>
    public string ApplyCdoValue(CdoPropertyItem item, string text)
    {
        if (Session?.Cdo is not { } cdo)
            return L("BlueprintEditor_NotLoaded");
        try
        {
            if (item.IsSimple)
                cdo.SetValue(item.Property, text);
            else
                cdo.SetJson(item.Property, text);
        }
        catch (Exception e)
        {
            return e.Message;
        }

        item.Refresh();
        Session.MarkCdoChanged();
        return null;
    }

    public string AddCdoOverride(CdoPropertyItem item)
    {
        if (Session?.Cdo is not { } cdo)
            return L("BlueprintEditor_NotLoaded");
        try
        {
            cdo.AddOverride(item.Property);
        }
        catch (Exception e)
        {
            return e.Message;
        }

        item.Refresh();
        Session.MarkCdoChanged();
        return null;
    }

    public void RemoveCdoOverride(CdoPropertyItem item)
    {
        if (Session?.Cdo is not { } cdo || !item.IsOverridden)
            return;

        cdo.RemoveOverride(item.Property);
        item.Refresh();
        Session.MarkCdoChanged();
    }

    #endregion

    #region pin constants

    /// <summary>An unconnected input pin whose value comes from literal bytecode constants.</summary>
    public static bool IsEditablePin(GraphPin pin) =>
        pin is { IsInput: true, IsConnected: false, SourceConstants.Count: > 0 } && pin.SourceConstants.All(EditableConstants.IsEditable);

    public static EditableConstantKind GetPinKind(GraphPin pin)
    {
        EditableConstants.TryGetKind(pin.SourceConstants[0], out var kind);
        return kind;
    }

    /// <summary>The pin's current raw value: the pending edit if any, else the original constant.</summary>
    public string GetPinEditText(GraphPin pin, string functionName)
    {
        var source = pin.SourceConstants[0];
        var pending = Session?.Constants.FirstOrDefault(o => o.FunctionName == functionName && o.Offset == (uint) source.StatementIndex);
        return pending?.NewValue ?? EditableConstants.FormatValue(source);
    }

    /// <summary>Records a new value for every constant behind the pin; returns an error message or null.</summary>
    public async Task<string> ApplyPinEditAsync(GraphPin pin, string functionName, string text, object source = null)
    {
        if (LiteralWireOf(pin) is { } literal)
            return await ApplyLiteralTextAsync(pin, literal, text);
        if (!IsEditablePin(pin))
            return L("BlueprintEditor_PinNotEditable");

        var kinds = pin.SourceConstants.Select(o => { EditableConstants.TryGetKind(o, out var k); return k; }).Distinct().ToList();
        foreach (var kind in kinds)
        {
            if (!EditableConstants.IsValid(kind, text))
                return string.Format(L("BlueprintEditor_InvalidValue"), text, kind);
        }

        if (!await EnsureLoadedAsync())
            return Status;

        if (kinds.Contains(EditableConstantKind.Bool))
            text = bool.Parse(text.Trim()) ? "true" : "false";

        // typing the original value again is a revert, not a change
        if (text == EditableConstants.FormatValue(pin.SourceConstants[0]))
            text = null;

        var before = _pinEntries.TryGetValue(pin, out var existing) ? existing.Current : null;
        if (before == text)
            return null;

        ChangePin(pin, functionName, source, text);
        PushStep(new EditStep(pin, () => ChangePin(pin, functionName, source, before), () => ChangePin(pin, functionName, source, text)));
        return null;
    }

    /// <summary>The pin's pending entry, if it was edited.</summary>
    public PinEditEntry FindPinEdit(GraphPin pin) => pin != null && _pinEntries.TryGetValue(pin, out var entry) ? entry : null;

    /// <summary>Sets the pin to the given text, or back to its original constant when the text is null.</summary>
    private void ChangePin(GraphPin pin, string functionName, object source, string text)
    {
        if (text == null)
        {
            if (_pinEntries.Remove(pin, out var entry))
            {
                foreach (KismetExpression constant in pin.SourceConstants)
                    Session.RemoveConstant(functionName, (uint) constant.StatementIndex);
                pin.Value = entry.Original;
                pin.IsEdited = false;
                PinEdits.Remove(entry);
            }
        }
        else
        {
            var display = FormatPinValue?.Invoke(pin, text) ?? text;
            if (!_pinEntries.TryGetValue(pin, out var entry))
            {
                entry = new PinEditEntry { Pin = pin, FunctionName = functionName, Source = source, Original = pin.Value, Current = text };
                entry.CurrentDisplay = display;
                _pinEntries[pin] = entry;
                PinEdits.Add(entry);
            }
            else
            {
                entry.Current = text;
                entry.CurrentDisplay = display;
            }

            foreach (KismetExpression constant in pin.SourceConstants)
            {
                Session.SetConstant(new ConstantEdit
                {
                    FunctionName = functionName,
                    Offset = (uint) constant.StatementIndex,
                    ExpectedToken = constant.Token.ToString(),
                    NewValue = text,
                });
            }

            pin.Value = display;
            pin.IsEdited = true;
        }

        if (pin.ParentNode is { } node)
        {
            node.CalculateSize();
            node.SetPosition(node.X, node.Y); // output pin anchors follow the new width
        }
    }

    private void RaiseHistoryProperties()
    {
        RaisePropertyChanged(nameof(CanUndo));
        RaisePropertyChanged(nameof(CanRedo));
    }

    /// <summary>Undoes the last edit (pin value or wire); returns the pin that changed.</summary>
    public GraphPin Undo()
    {
        if (!_undo.TryPop(out var step))
            return null;

        step.Undo();
        _redo.Push(step);
        RaiseHistoryProperties();
        return step.Pin;
    }

    public GraphPin Redo()
    {
        if (!_redo.TryPop(out var step))
            return null;

        step.Redo();
        _undo.Push(step);
        RaiseHistoryProperties();
        return step.Pin;
    }

    /// <summary>Puts one pin back to its original value or wiring (can be undone).</summary>
    public void RevertPin(GraphPin pin)
    {
        if (_wireEntries.TryGetValue(pin, out var wire) && wire.Edit != null)
        {
            var before = Snapshot(pin);
            RestoreWire(pin, null);
            var after = Snapshot(pin);
            PushStep(new EditStep(pin, () => RestoreWire(pin, before), () => RestoreWire(pin, after)));
            return;
        }
        if (!_pinEntries.TryGetValue(pin, out var entry))
            return;

        var current = entry.Current;
        ChangePin(pin, entry.FunctionName, entry.Source, null);
        PushStep(new EditStep(pin, () => ChangePin(pin, entry.FunctionName, entry.Source, current), () => ChangePin(pin, entry.FunctionName, entry.Source, null)));
    }

    private void ClearPinEdits()
    {
        foreach (var entry in _pinEntries.Values)
            entry.Pin.IsEdited = false;
        foreach (var entry in _wireEntries.Values)
        {
            entry.Pin.IsEdited = false;
            if (entry.Edit != null)
                RestoreLinksOnly(entry);
        }
        _pinEntries.Clear();
        _wireEntries.Clear();
        PinEdits.Clear();
        _undo.Clear();
        _redo.Clear();
        RaiseHistoryProperties();
    }

    #endregion

    #region wires

    /// <summary>What a rewired pin looks like: its pending edit, links and value.</summary>
    private sealed record WireSnapshot(WireEdit Edit, string Description, List<GraphPin> Links, string Value);

    private WireSnapshot Snapshot(GraphPin pin) =>
        _wireEntries.TryGetValue(pin, out var entry) && entry.Edit != null
            ? new WireSnapshot(entry.Edit, entry.Description, [.. pin.LinkedTo], pin.Value)
            : null;

    /// <summary>The pin's pending wire entry, if it was rewired.</summary>
    public WireEditEntry FindWire(GraphPin pin) => pin != null && _wireEntries.TryGetValue(pin, out var entry) && entry.Edit != null ? entry : null;

    /// <summary>The pending literal that replaced the pin's link, if any.</summary>
    public WireEdit LiteralWireOf(GraphPin pin) =>
        pin != null && _wireEntries.TryGetValue(pin, out var entry) && entry.Edit?.Kind == WireEditKind.LiteralData ? entry.Edit : null;

    /// <summary>Whether the pin shows a value that can be edited (a bytecode constant or a literal that replaced a link).</summary>
    public bool CanEditPinValue(GraphPin pin) =>
        pin != null && (LiteralWireOf(pin) is { LiteralKind: not (WirePlanner.LiteralObject or WirePlanner.LiteralInterface) } || IsEditablePin(pin));

    public EditableConstantKind GetValueKind(GraphPin pin) => LiteralWireOf(pin)?.LiteralKind switch
    {
        WirePlanner.LiteralBool => EditableConstantKind.Bool,
        WirePlanner.LiteralInt => EditableConstantKind.Int,
        WirePlanner.LiteralInt64 => EditableConstantKind.Int64,
        WirePlanner.LiteralFloat => EditableConstantKind.Float,
        WirePlanner.LiteralDouble => EditableConstantKind.Double,
        WirePlanner.LiteralByte => EditableConstantKind.Byte,
        WirePlanner.LiteralName => EditableConstantKind.Name,
        WirePlanner.LiteralString => EditableConstantKind.String,
        _ => GetPinKind(pin),
    };

    /// <summary>The raw text of the pin's value: a pending literal, a pending constant or the original constant.</summary>
    public string GetValueText(GraphPin pin, string functionName) =>
        LiteralWireOf(pin)?.LiteralText ?? GetPinEditText(pin, functionName);

    private async Task<string> ApplyLiteralTextAsync(GraphPin pin, WireEdit literal, string text)
    {
        if (!WirePlanner.IsValidLiteral(literal.LiteralKind, text))
            return string.Format(L("BlueprintEditor_InvalidValue"), text, literal.LiteralKind);
        if (!await EnsureLoadedAsync())
            return Status;
        if (literal.LiteralKind == WirePlanner.LiteralBool)
            text = bool.Parse(text.Trim()) ? "true" : "false";
        if (text == literal.LiteralText)
            return null;

        var before = Snapshot(pin);
        var edit = new WireEdit
        {
            FunctionName = literal.FunctionName, Kind = literal.Kind, Offset = literal.Offset, Token = literal.Token,
            LiteralKind = literal.LiteralKind, LiteralText = text,
        };
        RestoreWire(pin, new WireSnapshot(edit, string.Format(L("BlueprintEditor_WireLiteral"), FormatPinValue?.Invoke(pin, text) ?? text), [], FormatPinValue?.Invoke(pin, text) ?? text));
        var after = Snapshot(pin);
        PushStep(new EditStep(pin, () => RestoreWire(pin, before), () => RestoreWire(pin, after)));
        return null;
    }

    /// <summary>
    /// Connects two pins (either order): an exec output to an exec input, or a data output to a data input.
    /// <paramref name="code"/> and <paramref name="nodes"/> are the function's original bytecode and graph. Returns an error or null.
    /// </summary>
    public async Task<string> ConnectAsync(GraphPin a, GraphPin b, string functionName, object source,
        CUE4Parse.UE4.Kismet.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes)
    {
        var (output, input) = a.IsOutput ? (a, b) : (b, a);
        if (!output.IsOutput || !input.IsInput)
            return L("BlueprintEditor_WireDirection");
        if (WirePlanner.IsExec(output) != WirePlanner.IsExec(input))
            return L("BlueprintEditor_WireExecData");

        if (WirePlanner.IsExec(output))
        {
            var from = output.ParentNode;
            var to = input.ParentNode;
            if (output.EntryPoint != null)
            {
                var entryEdit = WirePlanner.PlanConnectEntry(functionName, output, to, code, nodes, out var entryError);
                if (entryEdit == null)
                    return entryError;
                return await RecordWireAsync(output, functionName, source, entryEdit, [input], output.Value, string.Format(L("BlueprintEditor_WireExecTo"), to.Name));
            }
            if (from?.ExecOutPin != output && from?.Output.Count(WirePlanner.IsExec) != 1)
                return L("BlueprintEditor_WireExecSingle");
            var edit = WirePlanner.PlanConnectExec(functionName, from, to, code, nodes, out var error);
            if (edit == null)
                return error;
            return await RecordWireAsync(output, functionName, source, edit, [input], output.Value, string.Format(L("BlueprintEditor_WireExecTo"), to.Name));
        }
        else
        {
            if (FindPinEdit(input) != null)
                return L("BlueprintEditor_WireRevertValueFirst");
            var edit = WirePlanner.PlanConnectData(functionName, input, output, nodes.SelectMany(o => o.Output).ToList(), out var error);
            if (edit == null)
                return error;
            return await RecordWireAsync(input, functionName, source, edit, [output], "",
                string.Format(L("BlueprintEditor_WireDataFrom"), $"{output.ParentNode?.Name}.{output.PinFriendlyName ?? output.PinName}".Trim('.')));
        }
    }

    /// <summary>Breaks the links of a pin: an exec output ends the chain, a data input gets a literal default. Returns an error or null.</summary>
    public async Task<string> BreakAsync(GraphPin pin, string functionName, object source,
        CUE4Parse.UE4.Kismet.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes)
    {
        if (WirePlanner.IsExec(pin))
        {
            if (!pin.IsOutput)
                return L("BlueprintEditor_WireBreakExecInput");
            if (pin.EntryPoint != null)
            {
                var entryEdit = WirePlanner.PlanBreakEntry(functionName, pin, code, nodes, out var entryError);
                if (entryEdit == null)
                    return entryError;
                return await RecordWireAsync(pin, functionName, source, entryEdit, [], pin.Value, L("BlueprintEditor_WireExecEnd"));
            }
            var edit = WirePlanner.PlanBreakExec(functionName, pin.ParentNode, code, nodes, out var error);
            if (edit == null)
                return error;
            return await RecordWireAsync(pin, functionName, source, edit, [], pin.Value, L("BlueprintEditor_WireExecEnd"));
        }
        else
        {
            if (!pin.IsInput)
                return L("BlueprintEditor_WireBreakDataOutput");
            if (FindPinEdit(pin) != null)
                return L("BlueprintEditor_WireRevertValueFirst");
            var edit = WirePlanner.PlanLiteral(functionName, pin, null, out var error);
            if (edit == null)
                return error;
            var display = FormatPinValue?.Invoke(pin, edit.LiteralText) ?? edit.LiteralText;
            return await RecordWireAsync(pin, functionName, source, edit, [], display, string.Format(L("BlueprintEditor_WireLiteral"), display));
        }
    }

    private async Task<string> RecordWireAsync(GraphPin pin, string functionName, object source, WireEdit edit, List<GraphPin> links, string value, string description)
    {
        if (!await EnsureLoadedAsync())
            return Status;

        if (!_wireEntries.ContainsKey(pin))
        {
            var entry = new WireEditEntry
            {
                Pin = pin, FunctionName = functionName, Source = source,
                OriginalLinks = [.. pin.LinkedTo], OriginalValue = pin.Value,
            };
            _wireEntries[pin] = entry;
        }

        var before = Snapshot(pin);
        RestoreWire(pin, new WireSnapshot(edit, description, links, value));
        var after = Snapshot(pin);
        PushStep(new EditStep(pin, () => RestoreWire(pin, before), () => RestoreWire(pin, after)));
        return null;
    }

    /// <summary>Puts the pin in a state: a pending wire edit with its links and value, or (null) back to how it was decompiled.</summary>
    private void RestoreWire(GraphPin pin, WireSnapshot state)
    {
        if (!_wireEntries.TryGetValue(pin, out var entry))
            return;

        var links = state?.Links ?? entry.OriginalLinks;
        foreach (var linked in pin.LinkedTo.ToList())
        {
            linked.Disconnect(pin);
            pin.Disconnect(linked);
        }
        // the edited pin (a data input or an exec output) has a single link; its new partner keeps its other links
        foreach (var linked in links)
        {
            pin.Connect(linked);
            linked.Connect(pin);
        }
        pin.Value = state?.Value ?? entry.OriginalValue;

        if (state == null)
        {
            // the entry stays registered (inactive) so redo can bring the edit back
            if (entry.Edit != null)
                Session.RemoveWire(entry.Edit);
            entry.Edit = null;
            pin.IsEdited = false;
            PinEdits.Remove(entry);
        }
        else
        {
            if (entry.Edit != null && entry.Edit.Key != state.Edit.Key)
                Session.RemoveWire(entry.Edit);
            entry.Edit = state.Edit;
            entry.Description = state.Description;
            Session.SetWire(state.Edit);
            pin.IsEdited = true;
            if (!PinEdits.Contains(entry))
                PinEdits.Add(entry);
        }

        if (pin.ParentNode is { } node)
        {
            node.CalculateSize();
            node.SetPosition(node.X, node.Y);
        }
    }

    /// <summary>Puts the decompiled links back without touching the session (used when everything is discarded).</summary>
    private static void RestoreLinksOnly(WireEditEntry entry)
    {
        var pin = entry.Pin;
        foreach (var linked in pin.LinkedTo.ToList())
        {
            linked.Disconnect(pin);
            pin.Disconnect(linked);
        }
        foreach (var linked in entry.OriginalLinks)
        {
            pin.Connect(linked);
            linked.Connect(pin);
        }
        pin.Value = entry.OriginalValue;
    }



    #endregion

    #region saving

    public async Task<BlueprintSaveResult> SaveAsync(bool exportAsset, bool createPak)
    {
        if (!await EnsureLoadedAsync())
            return new BlueprintSaveResult { Success = false, Message = Status };

        IsBusy = true;
        Status = L("BlueprintEditor_Saving");
        try
        {
            var session = Session;
            var result = await Task.Run(() => session.Save(exportAsset, createPak, package => Verifier?.Invoke(package, session)));
            Status = result.Success
                ? string.Join("  ", new[]
                {
                    result.AssetPath != null ? string.Format(L("BlueprintEditor_SavedAsset"), result.AssetPath) : null,
                    result.PakPath != null ? string.Format(L("BlueprintEditor_SavedPak"), result.PakPath) : null,
                }.Where(o => o != null))
                : string.Format(L("BlueprintEditor_SaveFailed"), result.Message);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops all pending changes. Pins edited in the graph need the graphs to be decompiled again.</summary>
    public async Task DiscardAsync()
    {
        if (Session == null)
            return;

        IsBusy = true;
        try
        {
            var session = Session;
            await Task.Run(session.Discard);
            ClearPinEdits();
            RebuildCdoList();
            Status = L("BlueprintEditor_Discarded");
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Close()
    {
        Session?.Dispose();
    }

    #endregion
}
