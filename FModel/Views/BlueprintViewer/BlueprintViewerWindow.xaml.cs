using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FModel.BlueprintGraph.Nodes;
using FModel.BlueprintGraph.Widgets;
using FModel.Extensions;
using FModel.Localization;
using FModel.Services;
using FModel.ViewModels;
using FModel.Framework.Docking;

namespace FModel.Views.BlueprintViewer;

public partial class BlueprintViewerWindow : IDockPanel
{
    private readonly BlueprintViewerViewModel _viewModel;
    private List<BPNode> _searchResult = [];
    private int _searchIndex;
    private bool _designerReady;
    private bool _designerFitted;
    private bool _syncingWidgetSelection;
    private bool _loaded;

    public BlueprintViewerWindow(BlueprintViewerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        NodeJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");
        FunctionJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");
        WidgetJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");
        CdoJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");
        GraphCanvas.SelectionChanged += OnGraphSelectionChanged;
        GraphCanvas.NodeContextRequested += OnNodeContextRequested;
        // the graph is read-only: edits come from the agent tools, the viewer shows and reviews them
        _viewModel.Editor.Edited += OnEditsChanged;
        _viewModel.GraphsReset += OnGraphsReset;
        DesignerCanvas.ImageResolver = _viewModel.Designer.ResolveImage;
        DesignerCanvas.SelectionChanged += OnDesignerSelectionChanged;
        DesignerCanvas.SizeChanged += OnDesignerSizeChanged;
        _viewModel.Designer.PropertiesEdited += OnDesignerPropertiesEdited;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // a dock panel is loaded again every time it's moved or its tab comes back
        if (_loaded)
            return;
        _loaded = true;

        if (!await _viewModel.LoadAsync())
            return;

        var navigate = _viewModel.PendingFunction != null || _viewModel.PendingFind != null;
        if (!navigate)
        {
            // open the ubergraph (first event) or the first function right away
            FunctionList.SelectedItem = _viewModel.Functions.FirstOrDefault();
        }

        if (_viewModel.IsWidgetBlueprint)
        {
            InitDesigner();
            if (!navigate)
                ModeTabs.SelectedIndex = 1; // like the editor, widget blueprints open on the designer
        }

        _ready = true;
        if (navigate)
            ApplyNavigation(_viewModel.PendingFunction, _viewModel.PendingFind);
    }

    private bool _ready;

    /// <summary>Shows a function (and/or runs a Find search) once the blueprint is loaded.</summary>
    public void NavigateTo(string functionName, string findTerm)
    {
        if (!_ready)
        {
            _viewModel.PendingFunction = functionName;
            _viewModel.PendingFind = findTerm;
            return;
        }
        ApplyNavigation(functionName, findTerm);
    }

    private void ApplyNavigation(string functionName, string findTerm)
    {
        _viewModel.PendingFunction = _viewModel.PendingFind = null;
        ModeTabs.SelectedIndex = 0;
        if (_viewModel.FindFunctionItem(functionName) is { } item)
        {
            if (ReferenceEquals(FunctionList.SelectedItem, item))
                OnFunctionSelected(FunctionList, null);
            else
                FunctionList.SelectedItem = item;
            FunctionList.ScrollIntoView(item);
        }
        else if (FunctionList.SelectedItem == null)
        {
            FunctionList.SelectedItem = _viewModel.Functions.FirstOrDefault();
        }

        if (!string.IsNullOrEmpty(findTerm))
        {
            DetailTabs.SelectedItem = FindTab;
            FindBox.Text = findTerm;
            RunFind();
        }
    }

    private async void InitDesigner()
    {
        var designer = _viewModel.Designer;
        var (width, height) = designer.ResolveDesignSize();
        DesignerCanvas.SetTree(designer.Info.Root, designer.Layout, width, height);
        _designerReady = true;

        await designer.PreloadImagesAsync();
        DesignerCanvas.InvalidateVisual();
    }

    private void OnDesignerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_designerFitted || !_designerReady || e.NewSize.Width <= 0)
            return;

        _designerFitted = true;
        DesignerCanvas.ZoomToFit();
    }

    private void OnDesignSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_designerReady)
            return;

        var (width, height) = _viewModel.Designer.ResolveDesignSize();
        DesignerCanvas.SetDesignSize(width, height);
    }

    private void OnOutlinesClick(object sender, RoutedEventArgs e)
    {
        DesignerCanvas.ShowOutlines = OutlinesCheckBox.IsChecked == true;
        DesignerCanvas.InvalidateVisual();
    }

    private void OnDesignerFitClick(object sender, RoutedEventArgs e) => DesignerCanvas.ZoomToFit();

    private void OnEditPropertiesClick(object sender, RoutedEventArgs e) => DesignerDetailsTabs.SelectedItem = DesignerEditTab;

    private void OnDesignerPropertiesEdited()
    {
        // preview-only overrides changed: lay out and redraw, keep the property rows (and their focus)
        DesignerCanvas.Refresh();
        _viewModel.Designer.RefreshTreeItems();
        _viewModel.Designer.DescribeSelection(DesignerCanvas.SelectedWidget, false);
    }

    private void OnResetWidgetClick(object sender, RoutedEventArgs e) => _viewModel.Designer.ResetWidget(DesignerCanvas.SelectedWidget);

    private void OnResetAllClick(object sender, RoutedEventArgs e) => _viewModel.Designer.ResetAll();

    private void OnResetPropertyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WidgetPropertyRow row)
            row.Reset();
    }

    private void OnPropertyValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox)
            return;

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        textBox.SelectAll();
        e.Handled = true;
    }

    private void OnWidgetTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingWidgetSelection || e.NewValue is not WidgetTreeItem item)
            return;

        DesignerCanvas.Select(item.Node);
        ShowWidgetDetails(item.Node);
    }

    private void OnDesignerSelectionChanged(WidgetNode node)
    {
        ShowWidgetDetails(node);
        if (node == null || _viewModel.Designer.FindItem(node) is not { } item)
            return;

        _syncingWidgetSelection = true;
        try
        {
            for (var parent = item.Parent; parent != null; parent = parent.Parent)
                parent.IsExpanded = true;
            item.IsSelected = true;
        }
        finally
        {
            _syncingWidgetSelection = false;
        }
    }

    private void ShowWidgetDetails(WidgetNode node)
    {
        _viewModel.Designer.DescribeSelection(node);
        WidgetJsonEditor.Text = _viewModel.Designer.SelectionJson ?? string.Empty;
    }

    private void OnBindingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is not WidgetBinding binding)
            return;

        var function = _viewModel.Functions.FirstOrDefault(o => o.Function.Name == binding.FunctionName);
        if (function == null)
            return;

        ModeTabs.SelectedIndex = 0;
        FunctionList.SelectedItem = function;
        FunctionList.ScrollIntoView(function);
    }

    private async void OnFunctionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating || FunctionList.SelectedItem is not BlueprintFunctionItem item)
            return;

        var previous = _viewModel.Current;
        var result = await _viewModel.OpenAsync(item);
        if (result == null)
            return;

        if (!ReferenceEquals(previous, result))
        {
            GraphCanvas.SetGraph(result.Graph);
            FunctionJsonEditor.Text = result.BytecodeJson ?? string.Empty;
            UpdateSearch();
        }

        var entry = _viewModel.FindEntryNode(item);
        if (entry != null)
            GraphCanvas.FocusNode(entry, true);
        else
            GraphCanvas.ZoomToFit();
    }

    private void OnGraphSelectionChanged()
    {
        _viewModel.DescribeSelection(GraphCanvas.SelectedNodes, GraphCanvas.SelectedPin);
        NodeJsonEditor.Text = _viewModel.SelectionJson ?? string.Empty;
        ShowPinEditor(GraphCanvas.SelectedPin);
    }

    #region editing

    private static string L(string key) => LocalizationManager.Instance.Get(key);

    private GraphPin _editedPin;

    /// <summary>Shows the pending change of the selected pin (the graph itself can't be edited), with a revert button.</summary>
    private void ShowPinEditor(GraphPin pin)
    {
        EditEntry change = pin == null ? null : (EditEntry) _viewModel.Editor.FindPinEdit(pin) ?? _viewModel.Editor.FindWire(pin);
        _editedPin = change == null ? null : pin;
        PinEditPanel.Visibility = change == null ? Visibility.Collapsed : Visibility.Visible;
        PinEditLabel.Text = change?.Summary ?? string.Empty;
    }

    private bool _refreshQueued;

    /// <summary>The pending edits changed (usually through the agent): redraw once the change is complete.</summary>
    private void OnEditsChanged()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            RefreshAfterPinChange();
            ShowCdoEditor(SelectedCdoItem);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>The decompiled graphs were dropped (pending edits discarded): show the current function again.</summary>
    private async void OnGraphsReset()
    {
        ShowCdoEditor(SelectedCdoItem);
        ApplyCdoFilter();
        if (FunctionList.SelectedItem is not BlueprintFunctionItem item)
            return;

        var result = await _viewModel.OpenAsync(item);
        if (result == null)
            return;
        GraphCanvas.SetGraph(result.Graph);
        FunctionJsonEditor.Text = result.BytecodeJson ?? string.Empty;
        UpdateSearch();
        GraphCanvas.ZoomToFit();
    }

    private void RefreshAfterPinChange()
    {
        GraphCanvas.RebuildConnections();
        _viewModel.DescribeSelection(GraphCanvas.SelectedNodes, GraphCanvas.SelectedPin);
        ShowPinEditor(GraphCanvas.SelectedPin);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor.Undo() != null)
            RefreshAfterPinChange();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Editor.Redo() != null)
            RefreshAfterPinChange();
    }

    private void OnPinRevertClick(object sender, RoutedEventArgs e)
    {
        if (_editedPin == null)
            return;

        _viewModel.Editor.RevertPin(_editedPin);
        RefreshAfterPinChange();
    }

    private bool _navigating;

    private async void OnPinEditListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PinEditList.SelectedItem is EditEntry { Source: BlueprintFunctionItem item } entry)
            await NavigateToNodeAsync(item, entry.Node ?? (entry is ObjectEditEntry objectEntry ? FindNodeAt(objectEntry.Edit.Offset) : null));
    }

    /// <summary>The node of the current graph whose statement covers the bytecode offset.</summary>
    private BPNode FindNodeAt(uint offset) =>
        _viewModel.Current?.Graph.Nodes.Where(o => o.NodeInstr != null && o.NodeInstr.StatementIndex <= offset && offset < o.NodeInstr.EndIndex)
            .OrderBy(o => o.NodeInstr.EndIndex - o.NodeInstr.StatementIndex).FirstOrDefault();

    /// <summary>Opens the graph of the item and centres the node in it.</summary>
    private async System.Threading.Tasks.Task NavigateToNodeAsync(BlueprintFunctionItem item, BPNode node)
    {
        _navigating = true;
        try
        {
            FunctionList.SelectedItem = item;
            var previous = _viewModel.Current;
            var result = await _viewModel.OpenAsync(item);
            if (result == null)
                return;

            if (!ReferenceEquals(previous, result))
            {
                GraphCanvas.SetGraph(result.Graph);
                FunctionJsonEditor.Text = result.BytecodeJson ?? string.Empty;
                UpdateSearch();
            }

            if (node != null)
                GraphCanvas.FocusNode(node, true);
        }
        finally
        {
            _navigating = false;
        }
    }

    private async void OnModeTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ModeTabs) || ModeTabs.SelectedItem != DefaultsTab)
            return;

        if (await _viewModel.Editor.EnsureLoadedAsync())
            ApplyCdoFilter();
    }

    private void OnCdoFilterChanged(object sender, RoutedEventArgs e) => ApplyCdoFilter();

    private void ApplyCdoFilter()
    {
        var filter = CdoFilter.Text;
        var overriddenOnly = OverriddenOnlyCheckBox.IsChecked == true;
        var view = CollectionViewSource.GetDefaultView(_viewModel.Editor.CdoProperties);
        view.Filter = string.IsNullOrWhiteSpace(filter) && !overriddenOnly
            ? null
            : o => o is CdoPropertyItem item &&
                   (!overriddenOnly || item.IsOverridden) &&
                   (string.IsNullOrWhiteSpace(filter) ||
                    item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.TypeText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.Preview.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private CdoPropertyItem SelectedCdoItem => CdoList.SelectedItem as CdoPropertyItem;

    private void OnCdoSelected(object sender, SelectionChangedEventArgs e) => ShowCdoEditor(SelectedCdoItem);

    /// <summary>Shows the selected class default (read-only).</summary>
    private void ShowCdoEditor(CdoPropertyItem item)
    {
        if (item == null)
        {
            CdoSelectionTitle.Text = string.Empty;
            CdoJsonEditor.Visibility = Visibility.Collapsed;
            return;
        }

        CdoSelectionTitle.Text = $"{item.DisplayName} : {item.TypeText}  ({item.Owner})" +
                                 (item.IsOverridden ? string.Empty : Environment.NewLine + L("BlueprintEditor_Inherited"));
        CdoJsonEditor.Visibility = item.IsOverridden ? Visibility.Visible : Visibility.Collapsed;
        CdoJsonEditor.Text = _viewModel.Editor.GetEditText(item);
    }

    private async void OnExportAssetClick(object sender, RoutedEventArgs e) => await SaveAsync(true, false);
    private async void OnSavePakClick(object sender, RoutedEventArgs e) => await SaveAsync(false, true);

    private async System.Threading.Tasks.Task SaveAsync(bool exportAsset, bool createPak)
    {
        IsEnabled = false;
        try
        {
            var result = await _viewModel.Editor.SaveAsync(exportAsset, createPak);
            if (!result.Success)
                MessageBox.Show(Window.GetWindow(this)!, _viewModel.Editor.Status, _viewModel.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, L("BlueprintEditor_DiscardConfirm"), _viewModel.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await _viewModel.DiscardAllAsync();
    }

    public bool OnPanelClosing()
    {
        return !_viewModel.Editor.HasUnsavedChanges ||
               MessageBox.Show(Window.GetWindow(this)!, L("BlueprintEditor_CloseConfirm"), _viewModel.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public void OnPanelClosed()
    {
        _viewModel.Editor.Edited -= OnEditsChanged;
        _viewModel.GraphsReset -= OnGraphsReset;
        _viewModel.Close();
    }

    #endregion

    private void OnFunctionFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FunctionFilter.Text;
        var view = CollectionViewSource.GetDefaultView(_viewModel.Functions);
        view.Filter = string.IsNullOrWhiteSpace(filter)
            ? null
            : o => o is BlueprintFunctionItem item &&
                   (item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.Function.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => UpdateSearch();

    private void UpdateSearch()
    {
        var term = SearchBox.Text;
        _searchIndex = 0;
        _searchResult = string.IsNullOrWhiteSpace(term)
            ? []
            : GraphCanvas.Nodes.Where(o =>
                Contains(o.Name) ||
                o.Input.Any(p => !p.IsHidden && (Contains(p.PinFriendlyName) || Contains(p.Value))) ||
                o.Output.Any(p => !p.IsHidden && Contains(p.PinFriendlyName))).ToList();

        GraphCanvas.SearchResult = _searchResult;
        SearchCount.Text = _searchResult.Count > 0 ? $"1/{_searchResult.Count}" : string.IsNullOrWhiteSpace(term) ? "" : "0";
        if (_searchResult.Count > 0)
            GraphCanvas.FocusNode(_searchResult[0]);
        GraphCanvas.InvalidateVisual();

        bool Contains(string source) => source != null && source.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void StepSearch(int direction)
    {
        if (_searchResult.Count == 0)
            return;

        _searchIndex = (_searchIndex + direction + _searchResult.Count) % _searchResult.Count;
        SearchCount.Text = $"{_searchIndex + 1}/{_searchResult.Count}";
        GraphCanvas.FocusNode(_searchResult[_searchIndex]);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        StepSearch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e) => StepSearch(1);
    private void OnSearchPreviousClick(object sender, RoutedEventArgs e) => StepSearch(-1);
    private void OnFitClick(object sender, RoutedEventArgs e) => GraphCanvas.ZoomToFit();

    #region cross-blueprint navigation

    private void OnNodeContextRequested(BPNode node, GraphPin pin, Point position)
    {
        var menu = new ContextMenu { PlacementTarget = GraphCanvas };

        if (pin != null && (_viewModel.Editor.FindWire(pin) != null || _viewModel.Editor.FindPinEdit(pin) != null))
        {
            var revert = new MenuItem { Header = L("BlueprintViewer_RevertWire") };
            revert.Click += (_, _) =>
            {
                _viewModel.Editor.RevertPin(pin);
                RefreshAfterPinChange();
            };
            menu.Items.Add(revert);
            menu.Items.Add(new Separator());
        }
        var links = _viewModel.GetNodeLinks(node);

        foreach (var link in links.Where(o => o.Member != null))
        {
            var item = new MenuItem { Header = string.Format(L("BlueprintViewer_GoToDefinition"), link.Member), FontWeight = FontWeights.SemiBold };
            item.Click += async (_, _) => await GoToAsync(link);
            menu.Items.Add(item);
        }
        if (!links.Any(o => o.Member != null))
            menu.Items.Add(new MenuItem { Header = L("BlueprintViewer_NoDefinition"), IsEnabled = false });

        var types = links.Where(o => o.Member == null).ToList();
        if (types.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var link in types)
            {
                var item = new MenuItem { Header = string.Format(L("BlueprintViewer_OpenAsset"), link.Label), ToolTip = link.OwnerPath };
                item.Click += async (_, _) => await GoToAsync(link);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var usages = new MenuItem { Header = L("BlueprintViewer_FindUsages") };
        usages.Click += (_, _) => OpenFind();
        menu.Items.Add(usages);

        menu.IsOpen = true;
    }

    /// <summary>Opens what a link leads to: a declaration in this or another blueprint, or a referenced asset.</summary>
    private async System.Threading.Tasks.Task GoToAsync(NodeLink link)
    {
        var cue4Parse = ApplicationService.ApplicationView.CUE4Parse;
        Mouse.OverrideCursor = Cursors.Wait;
        (FModel.Framework.Blueprints.DefinitionTarget Target, string Reason) resolved;
        try
        {
            resolved = await _viewModel.ResolveAsync(link);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (resolved.Target is not { } target)
        {
            MessageBox.Show(Window.GetWindow(this)!, string.Format(L("BlueprintViewer_DefinitionNotFound"), resolved.Reason), _viewModel.Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isThisBlueprint = string.Equals(target.File.Path, _viewModel.Entry.Path, StringComparison.OrdinalIgnoreCase);
        if (link.Member == null)
        {
            // a type or asset
            if (!isThisBlueprint)
                cue4Parse.OpenReferencedAsset(target.File);
            return;
        }

        if (isThisBlueprint)
            ApplyNavigation(target.FunctionName, target.VariableName);
        else if (!cue4Parse.IsBlueprintPackage(target.File))
            cue4Parse.OpenReferencedAsset(target.File); // e.g. a member of a user-defined struct
        else
            cue4Parse.OpenBlueprintViewer(target.File, target.FunctionName, target.VariableName);
    }

    private void OnWidgetReferenceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        OpenSelectedWidgetReference();
        e.Handled = true;
    }

    private void OnWidgetReferenceDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedWidgetReference();

    private void OpenSelectedWidgetReference()
    {
        if (WidgetReferenceList.SelectedItem is not FModel.Framework.Blueprints.AssetReference reference)
            return;

        if (!FModel.Framework.Blueprints.BlueprintNavigator.TryFindGameFile(_viewModel.Provider, reference.ObjectPath, out var file))
        {
            MessageBox.Show(Window.GetWindow(this)!, string.Format(L("BlueprintViewer_ReferenceNotFound"), reference.ObjectPath), _viewModel.Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.Equals(file.Path, _viewModel.Entry.Path, StringComparison.OrdinalIgnoreCase))
            ApplicationService.ApplicationView.CUE4Parse.OpenReferencedAsset(file);
    }

    #endregion


    #region find in blueprint

    private System.Threading.CancellationTokenSource _findCancellation;

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        RunFind();
        e.Handled = true;
    }

    private void OnFindClick(object sender, RoutedEventArgs e) => RunFind();

    private async void RunFind()
    {
        _findCancellation?.Cancel();
        var cancellation = _findCancellation = new System.Threading.CancellationTokenSource();
        var term = FindBox.Text.Trim();
        FindResults.ItemsSource = null;
        if (term.Length == 0)
        {
            FindStatus.Text = string.Empty;
            return;
        }

        try
        {
            var progress = new Progress<string>(text => FindStatus.Text = text);
            var hits = await _viewModel.SearchAllAsync(term, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            FindResults.ItemsSource = hits;
            FindStatus.Text = hits.Count == 0 ? L("BlueprintViewer_FindNone") : string.Format(L("BlueprintViewer_FindResults"), hits.Count);
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
    }

    private async void OnFindResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindResults.SelectedItem is not BlueprintSearchHit hit)
            return;
        await NavigateToNodeAsync(hit.Item, hit.Node);
        GraphCanvas.SelectedNodes.Clear();
        GraphCanvas.SelectedNodes.Add(hit.Node);
        OnGraphSelectionChanged();
        GraphCanvas.InvalidateVisual();
    }

    /// <summary>Opens the Find tab, prefilled with the selected node's name when there is one.</summary>
    private void OpenFind()
    {
        DetailTabs.SelectedItem = FindTab;
        if (GraphCanvas.SelectedNodes.FirstOrDefault() is { } node && !FindBox.IsKeyboardFocused)
        {
            FindBox.Text = node.Name;
            RunFind();
        }
        FindBox.Focus();
        FindBox.SelectAll();
    }

    #endregion

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inTextBox = Keyboard.FocusedElement is TextBox or ComboBox || Keyboard.FocusedElement is FrameworkElement { TemplatedParent: ComboBox };
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenFind();
            e.Handled = true;
        }
        else if (!inTextBox && Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Z || e.Key == Key.Y))
        {
            if (e.Key == Key.Z) OnUndoClick(this, e); else OnRedoClick(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
        {
            SearchBox.Text = string.Empty;
            GraphCanvas.Focus();
            e.Handled = true;
        }
    }
}
