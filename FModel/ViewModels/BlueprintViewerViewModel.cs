using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;
using FModel.BlueprintGraph;
using FModel.BlueprintGraph.Assets;
using FModel.BlueprintGraph.Decompiler;
using FModel.BlueprintGraph.Nodes;
using FModel.BlueprintGraph.Widgets;
using FModel.Framework;
using FModel.Framework.Mappings;
using FModel.Localization;
using Newtonsoft.Json;

namespace FModel.ViewModels;

public enum EBlueprintFunctionKind
{
    Event,
    Function,
}

public class BlueprintFunctionItem
{
    public string DisplayName { get; init; }
    public EBlueprintFunctionKind Kind { get; init; }
    public UFunction Function { get; init; }

    public string KindLabel => Kind == EBlueprintFunctionKind.Event ? "Event" : "Function";
}

public class BlueprintGraphResult
{
    public BPGraph Graph { get; init; }
    public DecompilationResult Result { get; init; }
    public string BytecodeJson { get; init; }

    /// <summary>The function whose bytecode the graph was decompiled from (the ubergraph for events).</summary>
    public string FunctionName { get; init; }
}

public class BlueprintViewerViewModel : ViewModel
{
    private const string UbergraphKey = "<ubergraph>";

    private readonly IFileProvider _provider;
    private readonly string _jmapPath;
    private readonly Dictionary<string, BlueprintGraphResult> _graphs = new();

    private BlueprintGraphContext _context;
    private Asset _asset;

    public GameFile Entry { get; }
    public string Title => $"Blueprint Viewer - {Entry.Path}";

    public ObservableCollection<BlueprintFunctionItem> Functions { get; } = [];

    private BlueprintFunctionItem _selectedFunction;
    public BlueprintFunctionItem SelectedFunction
    {
        get => _selectedFunction;
        set => SetProperty(ref _selectedFunction, value);
    }

    private BlueprintGraphResult _current;
    public BlueprintGraphResult Current
    {
        get => _current;
        private set
        {
            SetProperty(ref _current, value);
            RaisePropertyChanged(nameof(ProblemsText));
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _selectionText = string.Empty;
    public string SelectionText
    {
        get => _selectionText;
        set => SetProperty(ref _selectionText, value);
    }

    private string _selectionJson = string.Empty;
    public string SelectionJson
    {
        get => _selectionJson;
        set => SetProperty(ref _selectionJson, value);
    }

    public bool HasSignatureJmap { get; }

    public WidgetDesignerViewModel Designer { get; } = new();

    public BlueprintEditorViewModel Editor { get; }

    private bool _isWidgetBlueprint;
    public bool IsWidgetBlueprint
    {
        get => _isWidgetBlueprint;
        private set => SetProperty(ref _isWidgetBlueprint, value);
    }

    public string ProblemsText
    {
        get
        {
            if (_current?.Result is not { } result || result.IsSuccessful)
                return string.Empty;

            var inferred = result.Problems.Count(o => o.Message.StartsWith("No signature found", StringComparison.Ordinal));
            var sb = new StringBuilder();
            if (result.HasCriticalErrors)
                sb.AppendLine(L("BlueprintViewer_ProblemFailed")).AppendLine();
            if (inferred > 0)
                sb.AppendLine(string.Format(L("BlueprintViewer_ProblemInferred"), inferred)).AppendLine();
            foreach (var problem in result.Problems.Where(o => !o.Message.StartsWith("No signature found", StringComparison.Ordinal)))
            {
                var index = -1;
                try { index = problem.Context?.GetInstr().StatementIndex ?? -1; } catch { /* context may point past the block */ }
                sb.AppendLine(index >= 0 ? $"[{index}] {problem.Message}" : problem.Message);
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static string L(string key) => LocalizationManager.Instance.Get(key);

    public BlueprintViewerViewModel(IFileProvider provider, GameFile entry, string jmapPath, CUE4ParseViewModel owner = null)
    {
        _provider = provider;
        _jmapPath = jmapPath;
        Entry = entry;
        HasSignatureJmap = BlueprintGraphContextFactory.HasSignatureJmap(jmapPath);
        Editor = new BlueprintEditorViewModel(owner, entry) { Verifier = VerifyEditedPackage };
    }

    /// <summary>Forgets the decompiled graphs, e.g. after discarding pin edits.</summary>
    public void ClearGraphCache()
    {
        _graphs.Clear();
        _current = null;
        RaisePropertyChanged(nameof(Current));
    }

    /// <summary>
    /// Decompiles every function with edited constants from the saved package and compares the graph shapes
    /// with the originals: literal edits must never change the node structure.
    /// </summary>
    private string VerifyEditedPackage(IPackage package, Framework.BlueprintEditing.BlueprintEditSession session)
    {
        var asset = new Asset(package, Entry.NameWithoutExtension, _context.Options.ReorderEvents);
        if (!asset.IsBP)
            return "The saved package is not a blueprint any more.";
        asset.LoadAllProperties(_context);

        foreach (var functionName in session.Constants.Select(o => o.FunctionName).Distinct())
        {
            var isUbergraph = functionName.StartsWith("ExecuteUbergraph", StringComparison.Ordinal);
            var function = isUbergraph
                ? asset.SortedEvents.FirstOrDefault()
                : asset.Functions.FirstOrDefault(o => o.Name == functionName);
            if (function == null)
                return $"{functionName} is missing from the saved package.";

            var decompiler = new FunctionDecompiler(new GlobalDecompilerContext(asset, _context, function));
            var result = decompiler.Decompile(null);
            if (result.HasCriticalErrors)
                return $"{functionName} no longer decompiles: {result.Problems.First(o => o.IsCritical).Message}";

            var original = _graphs.Values.FirstOrDefault(o => o.FunctionName == functionName);
            if (original != null && Shape(original.Graph) != Shape(decompiler.Graph))
                return $"The graph of {functionName} changed shape after the edit ({original.Graph.Nodes.Count} -> {decompiler.Graph.Nodes.Count} nodes).";
        }
        return null;

        static string Shape(BPGraph graph) => string.Join(",", graph.Nodes.Select(o => o.NodeType).OrderBy(o => o, StringComparer.Ordinal));
    }

    /// <summary>
    /// Loads the package and lists its events and functions. Returns false when the asset is not a blueprint.
    /// </summary>
    public async Task<bool> LoadAsync()
    {
        IsBusy = true;
        StatusText = L(HasSignatureJmap ? "BlueprintViewer_LoadingWithSignatures" : "BlueprintViewer_Loading");
        try
        {
            var hasWidgetTree = false;
            var items = await Task.Run(() =>
            {
                _context = BlueprintGraphContextFactory.Create(_provider, _jmapPath);
                var package = _provider.LoadPackage(Entry);
                _asset = new Asset(package, Entry.NameWithoutExtension, _context.Options.ReorderEvents);
                if (!_asset.IsBP)
                    return null;

                if (WidgetTreeReader.FindWidgetClass(package) is { } widgetClass)
                {
                    try
                    {
                        hasWidgetTree = Designer.Load(widgetClass, _provider);
                    }
                    catch (Exception e)
                    {
                        Serilog.Log.Warning(e, "Failed to read the widget tree of {Path}", Entry.Path);
                    }
                }

                _asset.LoadAllProperties(_context);

                var list = new List<BlueprintFunctionItem>();
                list.AddRange(_asset.Events.Select(o => new BlueprintFunctionItem { DisplayName = o.Key, Kind = EBlueprintFunctionKind.Event, Function = o.Value }));
                list.AddRange(_asset.Functions.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(o => new BlueprintFunctionItem { DisplayName = o.Name, Kind = EBlueprintFunctionKind.Function, Function = o }));
                return list;
            });

            if (items == null)
            {
                StatusText = L("BlueprintViewer_NotBlueprint");
                return false;
            }

            Functions.Clear();
            foreach (var item in items)
                Functions.Add(item);

            if (hasWidgetTree)
                Designer.Publish();
            IsWidgetBlueprint = hasWidgetTree;

            StatusText = string.Format(L("BlueprintViewer_Summary"), _asset.ObjectName, _asset.Events.Count, _asset.Functions.Count);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Decompiles (or returns the cached graph of) the selected item. All events share the ubergraph.
    /// </summary>
    public async Task<BlueprintGraphResult> OpenAsync(BlueprintFunctionItem item)
    {
        if (item == null || _asset == null)
            return null;

        var key = item.Kind == EBlueprintFunctionKind.Event ? UbergraphKey : item.Function.Name;
        if (!_graphs.TryGetValue(key, out var result))
        {
            IsBusy = true;
            StatusText = string.Format(L("BlueprintViewer_Decompiling"), item.DisplayName);
            try
            {
                result = await Task.Run(() =>
                {
                    var decompiler = new FunctionDecompiler(new GlobalDecompilerContext(_asset, _context, item.Function));
                    var decompilation = decompiler.Decompile(null);
                    decompiler.Graph.LayoutNodesMsagl(null, _context.Options.UseMsagl);

                    string json;
                    try { json = JsonConvert.SerializeObject(decompiler.GlobalContext.CurrentFunction, Formatting.Indented); }
                    catch (Exception e) { json = e.Message; }

                    return new BlueprintGraphResult
                    {
                        Graph = decompiler.Graph, Result = decompilation, BytecodeJson = json,
                        FunctionName = decompiler.GlobalContext.CurrentFunction.Name,
                    };
                });
                _graphs[key] = result;
            }
            catch (Exception e)
            {
                StatusText = string.Format(L("BlueprintViewer_DecompileFailed"), item.DisplayName, e.Message);
                return null;
            }
            finally
            {
                IsBusy = false;
            }
        }

        Current = result;
        var inferredCount = result.Graph.Nodes.Count(o => o.IsSignatureInferred);
        StatusText = string.Format(L("BlueprintViewer_GraphStatus"), item.DisplayName, result.Graph.Nodes.Count) +
                     (inferredCount > 0 ? string.Format(L("BlueprintViewer_GraphInferred"), inferredCount) : "") +
                     (result.Result.HasCriticalErrors ? L("BlueprintViewer_GraphFailed") : "");
        return result;
    }

    public BPNode FindEntryNode(BlueprintFunctionItem item)
    {
        if (_current == null || item == null)
            return null;

        var name = item.Function.Name;
        return _current.Graph.FindFuncStartNode(name) ??
               _current.Graph.Nodes.FirstOrDefault(o => o.NodeType.Contains("Event", StringComparison.Ordinal) &&
                                                        (o.Name == name || o.Name == item.DisplayName));
    }

    public void DescribeSelection(IReadOnlyList<BPNode> nodes, GraphPin pin)
    {
        var node = nodes.FirstOrDefault();
        if (node == null)
        {
            SelectionText = nodes.Count == 0 ? string.Empty : $"{nodes.Count} nodes selected";
            SelectionJson = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(node.Name).AppendLine($"Type: {node.NodeType}{(node.Pure ? " (pure)" : "")}");
        if (node.StatementIndex >= 0)
            sb.AppendLine($"Statement index: {node.StatementIndex}");
        if (node.IsSignatureInferred)
            sb.AppendLine("Signature inferred from call site");
        if (nodes.Count > 1)
            sb.AppendLine($"(+{nodes.Count - 1} more selected)");

        if (pin != null)
        {
            sb.AppendLine().AppendLine($"Pin: {pin.PinFriendlyName} ({(pin.IsInput ? "input" : "output")})")
                .AppendLine($"Category: {pin.PinType.PinCategory}  Container: {pin.PinType.ContainerType}{(pin.PinType.IsReference ? "  by ref" : "")}");
            if (!string.IsNullOrEmpty(pin.PinType.PinSubCategoryObject) && pin.PinType.PinSubCategoryObject != "None")
                sb.AppendLine($"Type object: {pin.PinType.PinSubCategoryObject}");
            if (!string.IsNullOrEmpty(pin.Value))
                sb.AppendLine($"Value: {pin.Value}");
            if (pin.Property != null)
                sb.AppendLine($"Property: {pin.Property.Name} ({pin.Property.Owner})");
        }

        SelectionText = sb.ToString().TrimEnd();
        try { SelectionJson = node.NodeInstr != null ? node.NodeJson : string.Empty; }
        catch (Exception e) { SelectionJson = e.Message; }
    }
}
