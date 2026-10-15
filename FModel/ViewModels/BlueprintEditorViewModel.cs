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
    public string PendingText => Session == null ? string.Empty : string.Format(L("BlueprintEditor_Pending"), Session.CdoChangeCount, Session.ConstantChangeCount);
    public string CdoTitle => Session?.Cdo is { } cdo ? $"{cdo.Cdo.ObjectName} ({cdo.ClassName})" : string.Empty;

    private static string L(string key) => LocalizationManager.Instance.Get(key);

    public BlueprintEditorViewModel(CUE4ParseViewModel owner, GameFile entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public bool CanEdit => _owner != null;

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
    public async Task<string> ApplyPinEditAsync(GraphPin pin, string functionName, string text)
    {
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

        foreach (KismetExpression source in pin.SourceConstants)
        {
            Session.SetConstant(new ConstantEdit
            {
                FunctionName = functionName,
                Offset = (uint) source.StatementIndex,
                ExpectedToken = source.Token.ToString(),
                NewValue = text,
            });
        }

        pin.Value = text;
        if (pin.ParentNode is { } node)
        {
            node.CalculateSize();
            node.SetPosition(node.X, node.Y); // output pin anchors follow the new width
        }
        return null;
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
