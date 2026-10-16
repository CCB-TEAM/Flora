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
using FModel.BlueprintGraph.Engine;
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

    public string KindLabel => LocalizationManager.Instance.Get(Kind == EBlueprintFunctionKind.Event ? "BlueprintViewer_KindEvent" : "BlueprintViewer_KindFunction");
}

public class BlueprintGraphResult
{
    public BPGraph Graph { get; init; }
    public DecompilationResult Result { get; init; }
    public string BytecodeJson { get; init; }

    /// <summary>The function whose bytecode the graph was decompiled from (the ubergraph for events).</summary>
    public string FunctionName { get; init; }

    /// <summary>The function's bytecode statements as decompiled (wire edits are checked against them).</summary>
    public CUE4Parse.UE4.Kismet.KismetExpression[] Code { get; init; } = [];

    /// <summary>Whether CUE4Parse read the function's bytecode to the end (otherwise the graph is partial and must not be edited).</summary>
    public bool IsBytecodeComplete { get; init; } = true;
}

/// <summary>A place a node can lead to: a member declaration (Member set) or a game type/asset.</summary>
public class NodeLink
{
    public string OwnerPath { get; init; }
    public string Member { get; init; }
    public string Label { get; init; }
}

/// <summary>A node found by the blueprint-wide search.</summary>
public class BlueprintSearchHit
{
    public BlueprintFunctionItem Item { get; init; }
    public BPNode Node { get; init; }
    public string Match { get; init; }

    public string Title => Node.Name;
    public string Where => $"{Item.DisplayName}  -  {Match}";
}

public class BlueprintViewerViewModel : ViewModel
{
    private const string UbergraphKey = "<ubergraph>";

    private readonly IFileProvider _provider;
    private readonly string _jmapPath;
    private readonly Dictionary<string, BlueprintGraphResult> _graphs = new();
    private readonly Dictionary<string, EnumData> _enums = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.SemaphoreSlim _decompileLock = new(1, 1);

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
        Editor = new BlueprintEditorViewModel(owner, entry) { Verifier = VerifyEditedPackage, FormatPinValue = FormatPinValue };
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

        var wired = session.Wires.Select(o => o.FunctionName).ToHashSet();
        foreach (var functionName in session.Constants.Select(o => o.FunctionName).Concat(wired).Distinct())
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
            // wire edits legitimately add or remove nodes (e.g. a broken link drops the pure node that fed it)
            if (original != null && !wired.Contains(functionName) && Shape(original.Graph) != Shape(decompiler.Graph))
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

        var key = GraphKey(item);
        if (!_graphs.TryGetValue(key, out var result))
        {
            IsBusy = true;
            StatusText = string.Format(L("BlueprintViewer_Decompiling"), item.DisplayName);
            try
            {
                result = await GetGraphAsync(item);
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
                     (result.Result.HasCriticalErrors ? L("BlueprintViewer_GraphFailed") : "") +
                     (result.IsBytecodeComplete ? "" : L("BlueprintViewer_GraphIncomplete"));
        return result;
    }

    private static string GraphKey(BlueprintFunctionItem item) => item.Kind == EBlueprintFunctionKind.Event ? UbergraphKey : item.Function.Name;

    /// <summary>The cached graph of the item, decompiled first if needed (one decompilation at a time).</summary>
    private async Task<BlueprintGraphResult> GetGraphAsync(BlueprintFunctionItem item)
    {
        var key = GraphKey(item);
        await _decompileLock.WaitAsync();
        try
        {
            if (_graphs.TryGetValue(key, out var cached))
                return cached;

            var result = await Task.Run(() => Decompile(item));
            _graphs[key] = result;
            return result;
        }
        finally
        {
            _decompileLock.Release();
        }
    }

    private BlueprintGraphResult Decompile(BlueprintFunctionItem item)
    {
        var decompiler = new FunctionDecompiler(new GlobalDecompilerContext(_asset, _context, item.Function));
        var decompilation = decompiler.Decompile(null);

        // enum literals are stored as bytes: show the enumerator name (before layout, which depends on node sizes)
        foreach (var node in decompiler.Graph.Nodes)
        {
            var changed = false;
            foreach (var pin in node.Input)
            {
                if (!BlueprintEditorViewModel.IsEditablePin(pin) || GetPinEnum(pin) == null)
                    continue;
                pin.Value = FormatPinValue(pin, EditableConstants.FormatValue(pin.SourceConstants[0]));
                changed = true;
            }
            if (changed)
                node.CalculateSize();
        }
        decompiler.Graph.LayoutNodesMsagl(null, _context.Options.UseMsagl);

        string json;
        try { json = JsonConvert.SerializeObject(decompiler.GlobalContext.CurrentFunction, Formatting.Indented); }
        catch (Exception e) { json = e.Message; }

        var function = decompiler.GlobalContext.CurrentFunction;
        return new BlueprintGraphResult
        {
            Graph = decompiler.Graph, Result = decompilation, BytecodeJson = json,
            FunctionName = function.Name,
            IsBytecodeComplete = function.IsScriptBytecodeComplete,
            Code = function.ScriptBytecode ?? [],
        };
    }

    #region enum pins

    /// <summary>The enum of a literal byte pin (from the parameter's type and the loaded mappings), or null.</summary>
    public EnumData GetPinEnum(GraphPin pin)
    {
        if (_context?.Signatures == null || pin?.SourceConstants.Count is not > 0 ||
            !EditableConstants.TryGetKind(pin.SourceConstants[0], out var kind) || kind != EditableConstantKind.Byte)
            return null;

        var name = pin.Property?.PinType.PinSubCategoryObject;
        if (string.IsNullOrEmpty(name) || name == "None")
            name = pin.PinType.PinSubCategoryObject;
        if (string.IsNullOrEmpty(name) || name == "None")
            return null;

        lock (_enums)
        {
            if (!_enums.TryGetValue(name, out var data))
            {
                data = _context.Signatures.TryFindEnum(name);
                if (data != null)
                {
                    if (name.Contains('/') && !name.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
                        ApplyUserEnumDisplayNames(data, name);
                    // the implicit _MAX entry is not a value to pick
                    foreach (var max in data.Elements.Keys.Where(o => ShortEnumName(o).EndsWith("_MAX", StringComparison.Ordinal)).ToList())
                        data.Elements.Remove(max);
                }
                _enums[name] = data;
            }
            return data is { Elements.Count: > 0 } ? data : null;
        }
    }

    /// <summary>
    /// Blueprint enums name their entries NewEnumerator0, 1, ...; the names shown in the editor are in the enum asset's DisplayNameMap.
    /// </summary>
    private void ApplyUserEnumDisplayNames(EnumData data, string path)
    {
        try
        {
            if (!_provider.TryLoadPackageObject(path, out var export) ||
                export.GetOrDefault<CUE4Parse.UE4.Assets.Objects.UScriptMap>("DisplayNameMap") is not { } map)
                return;

            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in map.Properties)
            {
                if (key?.GenericValue is FName internalName && value?.GenericValue is CUE4Parse.UE4.Objects.Core.i18N.FText text &&
                    !string.IsNullOrWhiteSpace(text.Text))
                    displayNames[internalName.Text] = text.Text;
            }
            if (displayNames.Count == 0)
                return;

            var renamed = new Dictionary<string, long>();
            foreach (var (element, value) in data.Elements)
            {
                var shortName = ShortEnumName(element);
                var display = displayNames.TryGetValue(shortName, out var found) ? found : shortName;
                if (!renamed.TryAdd(display, value))
                    renamed.TryAdd($"{display} ({shortName})", value);
            }
            data.Elements = renamed;
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not read the display names of {Enum}", path);
        }
    }

    public static string ShortEnumName(string element)
    {
        var index = element.LastIndexOf("::", StringComparison.Ordinal);
        return index >= 0 ? element[(index + 2)..] : element;
    }

    /// <summary>The enumerator names of an enum pin in value order.</summary>
    public IReadOnlyList<string> GetPinEnumNames(GraphPin pin) =>
        GetPinEnum(pin) is { } data ? data.Elements.OrderBy(o => o.Value).Select(o => ShortEnumName(o.Key)).ToList() : [];

    /// <summary>How a raw constant is shown on the pin: the enumerator name for enum pins.</summary>
    public string FormatPinValue(GraphPin pin, string raw)
    {
        if (GetPinEnum(pin) is not { } data || !long.TryParse(raw, out var value))
            return raw;
        var element = data.Elements.FirstOrDefault(o => o.Value == value).Key;
        return element == null ? raw : ShortEnumName(element);
    }

    /// <summary>Turns typed text into a raw constant: enumerator names become their value.</summary>
    public string ParsePinValue(GraphPin pin, string text)
    {
        if (GetPinEnum(pin) is not { } data)
            return text;
        var trimmed = text.Trim();
        foreach (var (element, value) in data.Elements)
        {
            if (element.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || ShortEnumName(element).Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return text;
    }

    #endregion

    #region search

    /// <summary>
    /// Finds nodes in every event and function of the blueprint (decompiling what wasn't opened yet) by node name, pin name, pin type or pin value.
    /// </summary>
    public async Task<List<BlueprintSearchHit>> SearchAllAsync(string term, IProgress<string> progress, System.Threading.CancellationToken cancellation)
    {
        var hits = new List<BlueprintSearchHit>();
        if (string.IsNullOrWhiteSpace(term) || _asset == null)
            return hits;

        var seen = new HashSet<string>();
        var items = Functions.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var item = items[i];
            if (!seen.Add(GraphKey(item)))
                continue;

            progress?.Report($"{i + 1}/{items.Count}  {item.DisplayName}");
            BlueprintGraphResult graph;
            try
            {
                graph = await GetGraphAsync(item);
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Search: failed to decompile {Function}", item.DisplayName);
                continue;
            }

            foreach (var node in graph.Graph.Nodes)
            {
                if (MatchNode(node, term) is { } match)
                    hits.Add(new BlueprintSearchHit { Item = item, Node = node, Match = match });
            }
        }
        return hits;
    }

    private static string MatchNode(BPNode node, string term)
    {
        bool Contains(string source) => source != null && source.Contains(term, StringComparison.OrdinalIgnoreCase);

        if (Contains(node.Name)) return node.NodeType;
        foreach (var pin in node.Input.Concat(node.Output))
        {
            if (pin.IsHidden) continue;
            if (Contains(pin.PinFriendlyName)) return $"pin {pin.PinFriendlyName}";
            if (pin.IsInput && Contains(pin.Value)) return $"{pin.PinFriendlyName} = {pin.Value}";
            if (Contains(pin.PinType.PinSubCategoryObject)) return $"{pin.PinFriendlyName} : {pin.PinType.PinSubCategoryObject}";
        }
        return null;
    }

    #endregion


    #region cross-blueprint navigation

    public IFileProvider Provider => _provider;

    /// <summary>Function to open (and Find term to run) once the window has loaded, or for an already open window.</summary>
    public string PendingFunction { get; set; }
    public string PendingFind { get; set; }

    /// <summary>
    /// Where a node can lead: the declaration of what it calls or reads (in this or another blueprint), and the game types its pins use.
    /// Native (/Script) declarations and types are left out.
    /// </summary>
    public List<NodeLink> GetNodeLinks(BPNode node)
    {
        var links = new List<NodeLink>();
        if (node == null)
            return links;

        var owner = node.DefinitionOwner;
        var member = node.DefinitionMember;
        if (node.NodeType == "K2Node_Event" && _asset?.GeneratedClass?.SuperStruct?.ResolvedObject?.GetPathName() is { } parent)
        {
            // an implemented event is declared by a parent class
            owner = parent;
            member = node.Name;
        }
        if (Framework.Blueprints.BlueprintNavigator.IsGameObjectPath(owner) && !string.IsNullOrEmpty(member) && !owner.Contains(':'))
            links.Add(new NodeLink { OwnerPath = owner, Member = member, Label = member });

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in node.Input.Concat(node.Output).Where(o => !o.IsHidden))
        {
            foreach (var candidate in new[] { pin.PinType.PinSubCategoryObject, pin.IsInput ? pin.Value : null })
            {
                if (!Framework.Blueprints.BlueprintNavigator.IsGameObjectPath(candidate))
                    continue;
                var package = Framework.Blueprints.BlueprintNavigator.PackagePath(candidate);
                if (types.Add(package))
                    links.Add(new NodeLink { OwnerPath = candidate, Label = package[(package.LastIndexOf('/') + 1)..] });
            }
        }
        if (Framework.Blueprints.BlueprintNavigator.IsGameObjectPath(owner) && !owner.Contains(':') &&
            types.Add(Framework.Blueprints.BlueprintNavigator.PackagePath(owner)))
        {
            var package = Framework.Blueprints.BlueprintNavigator.PackagePath(owner);
            links.Add(new NodeLink { OwnerPath = owner, Label = package[(package.LastIndexOf('/') + 1)..] });
        }
        return links;
    }

    public Task<(Framework.Blueprints.DefinitionTarget Target, string Reason)> ResolveAsync(NodeLink link) =>
        Task.Run(() =>
        {
            var target = Framework.Blueprints.BlueprintNavigator.Resolve(_provider, link.OwnerPath, link.Member, out var reason);
            return (target, reason);
        });

    /// <summary>The list entry of a function or event by its name.</summary>
    public BlueprintFunctionItem FindFunctionItem(string name) =>
        string.IsNullOrEmpty(name) ? null : Functions.FirstOrDefault(o => o.Function.Name == name) ?? Functions.FirstOrDefault(o => o.DisplayName == name);

    #endregion

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
        if (node.NodeInstr is { } instr)
            sb.AppendLine($"Statement index: {instr.StatementIndex}  (bytecode {instr.StatementIndex}..{instr.EndIndex}, {instr.Size} bytes)");
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
            foreach (var constant in pin.SourceConstants)
                sb.AppendLine($"Constant: {constant.Token} at {constant.StatementIndex} ({constant.Size} bytes)");
            if (pin.Property != null)
                sb.AppendLine($"Property: {pin.Property.Name} ({pin.Property.Owner})");
        }

        SelectionText = sb.ToString().TrimEnd();
        try { SelectionJson = node.NodeInstr != null ? node.NodeJson : string.Empty; }
        catch (Exception e) { SelectionJson = e.Message; }
    }
}
