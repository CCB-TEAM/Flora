using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion.Textures;
using FModel.BlueprintGraph.Widgets;
using FModel.Framework;
using FModel.Settings;
using FModel.Views.BlueprintViewer;
using Newtonsoft.Json;
using Serilog;
using SkiaSharp;

namespace FModel.ViewModels;

public class WidgetTreeItem : ViewModel
{
    public WidgetNode Node { get; }
    public WidgetTreeItem Parent { get; }
    public List<WidgetTreeItem> Children { get; } = [];
    public string DisplayName => Node.Note != null ? $"{Node.DisplayName} ({Node.Note})" : Node.DisplayName;
    public bool IsCollapsedWidget => Node.IsCollapsed;
    public bool HasPreviewEdits => WidgetProps.HasOverrides(Node.Export) || WidgetProps.HasOverrides(Node.Slot);

    /// <summary>Visibility and edit markers follow preview edits.</summary>
    public void RefreshState()
    {
        RaisePropertyChanged(nameof(IsCollapsedWidget));
        RaisePropertyChanged(nameof(DisplayName));
        RaisePropertyChanged(nameof(HasPreviewEdits));
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public WidgetTreeItem(WidgetNode node, WidgetTreeItem parent, int depth)
    {
        Node = node;
        Parent = parent;
        IsExpanded = depth < 2;
        foreach (var child in node.Children)
            Children.Add(new WidgetTreeItem(child, this, depth + 1));
    }

    public IEnumerable<WidgetTreeItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.SelfAndDescendants())
                yield return item;
    }
}

public class WidgetDesignSize
{
    public string Label { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public bool UseDesiredSize { get; init; }
    public override string ToString() => Label;
}

/// <summary>
/// Widget blueprint part of the blueprint viewer: widget hierarchy, layout and brush images.
/// </summary>
public class WidgetDesignerViewModel : ViewModel
{

    public WidgetBlueprintInfo Info { get; private set; }
    public WidgetLayout Layout { get; private set; }
    public ObservableCollection<WidgetTreeItem> Roots { get; } = [];
    public ObservableCollection<WidgetBinding> Bindings { get; } = [];

    /// <summary>Editable properties of the selected widget; edits are preview-only and never saved.</summary>
    public ObservableCollection<WidgetPropertyRow> PropertyRows { get; } = [];

    /// <summary>Game assets the selected widget (and its slot) reference.</summary>
    public ObservableCollection<Framework.Blueprints.AssetReference> References { get; } = [];

    /// <summary>Raised after a preview edit or reset: the canvas has to lay out and redraw.</summary>
    public event Action PropertiesEdited;

    private IFileProvider _provider;
    private WidgetNode _editedNode;
    private readonly Dictionary<WidgetNode, HashSet<IPropertyHolder>> _touched = [];

    public bool HasEditableSelection => _editedNode != null;

    public List<WidgetDesignSize> DesignSizes { get; } =
    [
        new() { Label = FModel.Localization.LocalizationManager.Instance.Get("BlueprintViewer_DesignAuto") },
        new() { Label = FModel.Localization.LocalizationManager.Instance.Get("BlueprintViewer_DesignDesired"), UseDesiredSize = true },
        new() { Label = "1280 x 720", Width = 1280, Height = 720 },
        new() { Label = "1920 x 1080", Width = 1920, Height = 1080 },
        new() { Label = "2560 x 1440", Width = 2560, Height = 1440 },
        new() { Label = "3840 x 2160", Width = 3840, Height = 2160 },
        new() { Label = string.Format(FModel.Localization.LocalizationManager.Instance.Get("BlueprintViewer_DesignPortrait"), 1080, 1920), Width = 1080, Height = 1920 },
    ];

    private WidgetDesignSize _selectedDesignSize;
    public WidgetDesignSize SelectedDesignSize
    {
        get => _selectedDesignSize;
        set => SetProperty(ref _selectedDesignSize, value);
    }

    private string _selectionText = string.Empty;
    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    private string _selectionJson = string.Empty;
    public string SelectionJson
    {
        get => _selectionJson;
        private set => SetProperty(ref _selectionJson, value);
    }

    public bool HasTree => Info?.Root != null;

    public WidgetDesignerViewModel()
    {
        _selectedDesignSize = DesignSizes[0];
    }

    /// <summary>Reads the widget tree (on a worker thread). Returns false when the class has no widget tree.</summary>
    public bool Load(UWidgetBlueprintGeneratedClass widgetClass, IFileProvider provider)
    {
        _provider = provider;
        var types = provider.MappingsForGame?.Types;
        Layout = new WidgetLayout(WidgetDesignerCanvas.MeasureText,
            className => types != null && types.TryGetValue(className, out var type) ? type.SuperType : null);
        Info = WidgetTreeReader.Read(widgetClass);
        if (Info.Root == null)
            return false;

        // fonts are needed by the first layout (UI thread), load them here
        foreach (var node in Info.Root.SelfAndDescendants())
        {
            if (Layout.GetKind(node) is WidgetKind.TextBlock or WidgetKind.RichTextBlock)
                WidgetDesignerCanvas.FontOf(node);
        }
        return true;
    }

    /// <summary>Populates the bindable collections (UI thread).</summary>
    public void Publish()
    {
        Roots.Clear();
        Bindings.Clear();
        if (Info?.Root != null)
            Roots.Add(new WidgetTreeItem(Info.Root, null, 0));
        foreach (var binding in Info?.Bindings ?? [])
            Bindings.Add(binding);
        RaisePropertyChanged(nameof(HasTree));
    }

    public (float Width, float Height) ResolveDesignSize()
    {
        var root = Info?.Root;
        var size = SelectedDesignSize ?? DesignSizes[0];
        if (!size.UseDesiredSize && size.Width > 0)
            return (size.Width, size.Height);

        // screens are usually a canvas panel filling the viewport, components are previewed at their desired size
        var useDesired = size.UseDesiredSize || root != null && Layout.GetKind(root) != WidgetKind.CanvasPanel;
        if (useDesired && root != null)
        {
            Layout.Layout(root, UserSettings.Default.WidgetDesignerWidth, UserSettings.Default.WidgetDesignerHeight);
            if (root.DesiredWidth >= 1 && root.DesiredHeight >= 1)
                return (root.DesiredWidth, root.DesiredHeight);
        }
        return (UserSettings.Default.WidgetDesignerWidth, UserSettings.Default.WidgetDesignerHeight);
    }

    public WidgetTreeItem FindItem(WidgetNode node) =>
        Roots.SelectMany(o => o.SelfAndDescendants()).FirstOrDefault(o => o.Node == node);

    public void DescribeSelection(WidgetNode node, bool rebuildProperties = true)
    {
        if (rebuildProperties)
            BuildPropertyRows(node);

        References.Clear();
        if (node == null)
        {
            SelectionText = SelectionJson = string.Empty;
            return;
        }

        try
        {
            var references = Framework.Blueprints.BlueprintNavigator.FindReferences(node.Export);
            if (node.UserWidgetClass?.GetPathName() is { } widgetClass && references.All(o => o.ObjectPath != widgetClass))
                references.Insert(0, new Framework.Blueprints.AssetReference { Property = "Widget Blueprint", ObjectPath = widgetClass });
            foreach (var reference in references)
                References.Add(reference);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not list the references of {Widget}", node.Name);
        }

        var (x, y, w, h) = node.DesignRect();
        var sb = new StringBuilder()
            .AppendLine(node.Name)
            .AppendLine($"Class: {node.ClassName} ({Layout.GetKind(node)})");
        if (node.Slot != null)
            sb.AppendLine($"Slot: {node.SlotClassName}");
        sb.AppendLine($"Rect: {x:0.#}, {y:0.#}  {w:0.#} x {h:0.#}")
            .AppendLine($"Desired: {node.DesiredWidth:0.#} x {node.DesiredHeight:0.#}")
            .AppendLine($"Visibility: {node.Visibility}");
        if (node.Note != null)
            sb.AppendLine(node.Note);
        foreach (var binding in Info.Bindings.Where(o => o.ObjectName == node.Name))
            sb.AppendLine($"Binding: {binding.PropertyName} <- {binding.FunctionName}");
        SelectionText = sb.ToString().TrimEnd();

        try
        {
            var json = new StringBuilder();
            json.AppendLine(JsonConvert.SerializeObject(node.Export, Formatting.Indented));
            if (node.Slot != null)
                json.AppendLine().AppendLine(JsonConvert.SerializeObject(node.Slot, Formatting.Indented));
            SelectionJson = json.ToString();
        }
        catch (Exception e)
        {
            SelectionJson = e.Message;
        }
    }

    #region preview edits

    private void BuildPropertyRows(WidgetNode node)
    {
        _editedNode = node;
        PropertyRows.Clear();
        if (node != null)
        {
            var rows = WidgetPropertyRows.Build(node, _provider, holder => TrackEdit(node, holder), () => PropertiesEdited?.Invoke());
            foreach (var row in rows)
                PropertyRows.Add(row);
        }
        RaisePropertyChanged(nameof(HasEditableSelection));
    }

    private void TrackEdit(WidgetNode node, IPropertyHolder holder)
    {
        if (!_touched.TryGetValue(node, out var holders))
            _touched[node] = holders = new HashSet<IPropertyHolder>(ReferenceEqualityComparer.Instance);
        holders.Add(holder);
    }

    public void ResetWidget(WidgetNode node)
    {
        if (node == null || !_touched.Remove(node, out var holders))
            return;
        foreach (var holder in holders)
            WidgetProps.ClearOverrides(holder);
        AfterReset();
    }

    public void ResetAll()
    {
        foreach (var holder in _touched.Values.SelectMany(o => o))
            WidgetProps.ClearOverrides(holder);
        _touched.Clear();
        AfterReset();
    }

    private void AfterReset()
    {
        foreach (var row in PropertyRows)
            row.Refresh();
        PropertiesEdited?.Invoke();
    }

    public void RefreshTreeItems()
    {
        foreach (var item in Roots.SelectMany(o => o.SelfAndDescendants()))
            item.RefreshState();
    }

    #endregion

    #region images

    public SKBitmap ResolveImage(UObject resource) => WidgetAssetCache.TryGetImage(resource);

    /// <summary>Decodes every brush resource of the tree in the background (shared cache); the canvas redraws when done.</summary>
    public Task PreloadImagesAsync()
    {
        var resources = CollectBrushResources().Where(o => !WidgetAssetCache.IsImageLoaded(o)).ToList();
        if (resources.Count == 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            Parallel.ForEach(resources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
                resource => WidgetAssetCache.LoadImage(resource));
        });
    }

    private IEnumerable<UObject> CollectBrushResources()
    {
        if (Info?.Root == null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Info.Root.SelfAndDescendants())
        {
            var e = node.Export;
            var style = WidgetProps.Struct(e, "WidgetStyle");
            foreach (var brush in new[]
                     {
                         WidgetProps.Struct(e, "Brush"), WidgetProps.Struct(e, "Background"),
                         WidgetProps.Struct(style, "Normal"), WidgetProps.Struct(style, "BackgroundImage"), WidgetProps.Struct(style, "FillImage"),
                     })
            {
                if (WidgetProps.Object(brush, "ResourceObject") is { } resource && seen.Add(resource.GetPathName()))
                    yield return resource;
            }
        }
    }

    #endregion
}
