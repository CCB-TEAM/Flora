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
        GraphCanvas.PinValueClicked += OnPinValueClicked;
        GraphCanvas.NodeContextRequested += OnNodeContextRequested;
        GraphCanvas.WireDropped += OnWireDropped;
        GraphCanvas.PinBreakRequested += OnPinBreakRequested;
        GraphCanvas.AllowWireEditing = _viewModel.Editor.CanEdit;
        GraphCanvas.ToolTip = _viewModel.Editor.CanEdit ? L("BlueprintViewer_WireHint") : null;
        GraphCanvas.PreviewMouseWheel += (_, _) =>
        {
            CloseInlinePinEditor(true);
            OnInlinePinEnumClosed(null, EventArgs.Empty);
        };
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

    private void ShowPinEditor(GraphPin pin)
    {
        PinEditError.Visibility = Visibility.Collapsed;
        if (pin == null || !_viewModel.Editor.CanEdit || _viewModel.Current?.FunctionName == null || !_viewModel.Editor.CanEditPinValue(pin))
        {
            _editedPin = null;
            PinEditPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _editedPin = pin;
        PinEditLabel.Text = string.Format(L("BlueprintEditor_PinValue"), pin.PinFriendlyName, _viewModel.Editor.GetValueKind(pin),
            Math.Max(1, pin.SourceConstants.Count));
        var raw = _viewModel.Editor.GetValueText(pin, _viewModel.Current.FunctionName);
        var enumNames = _viewModel.GetPinEnumNames(pin);
        _fillingPinEditBox = true;
        try
        {
            PinEditBox.ItemsSource = enumNames.Count > 0 ? enumNames
                : _viewModel.Editor.GetValueKind(pin) == FModel.BlueprintGraph.Decompiler.EditableConstantKind.Bool ? new[] { "true", "false" }
                : null;
            PinEditBox.Text = enumNames.Count > 0 ? _viewModel.FormatPinValue(pin, raw) : raw;
        }
        finally
        {
            _fillingPinEditBox = false;
        }
        PinEditBox.ToolTip = enumNames.Count > 0 ? L("BlueprintEditor_EnumHint") : null;
        PinEditPanel.Visibility = Visibility.Visible;
    }

    private bool _fillingPinEditBox;

    // picking from the drop-down applies right away; typing waits for Enter or Apply
    private void OnPinEditPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingPinEditBox || PinEditBox.SelectedItem is not string picked || !PinEditBox.IsDropDownOpen)
            return;
        PinEditBox.Text = picked;
        ApplyPinEdit();
    }

    private async void ApplyPinEdit()
    {
        if (_editedPin == null)
            return;

        var error = await CommitPinAsync(_editedPin, PinEditBox.Text);
        PinEditError.Text = error ?? string.Empty;
        PinEditError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Applies a new literal value to the pin; returns an error message or null.</summary>
    private async System.Threading.Tasks.Task<string> CommitPinAsync(GraphPin pin, string text)
    {
        if (_viewModel.Current?.FunctionName is not { } functionName)
            return L("BlueprintEditor_PinNotEditable");
        if (!_viewModel.Current.IsBytecodeComplete)
            return L("BlueprintEditor_BytecodeIncomplete");

        text = _viewModel.ParsePinValue(pin, text ?? string.Empty);
        var error = await _viewModel.Editor.ApplyPinEditAsync(pin, functionName, text, FunctionList.SelectedItem);
        if (error == null)
            RefreshAfterPinChange();
        return error;
    }

    #region editing on the node

    private GraphPin _inlinePin;

    private async void OnPinValueClicked(GraphPin pin, Rect rect)
    {
        if (!_viewModel.Editor.CanEdit || _viewModel.Current?.FunctionName is not { } functionName || !_viewModel.Editor.CanEditPinValue(pin))
            return;

        CloseInlinePinEditor(true);

        var current = _viewModel.Editor.GetValueText(pin, functionName);
        if (_viewModel.Editor.GetValueKind(pin) == FModel.BlueprintGraph.Decompiler.EditableConstantKind.Bool)
        {
            // a click flips a boolean
            await CommitPinAsync(pin, current.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true");
            return;
        }

        var enumNames = _viewModel.GetPinEnumNames(pin);
        if (enumNames.Count > 0)
        {
            _inlineEnumPin = null; // no commit while filling
            InlinePinEnumBox.ItemsSource = enumNames;
            InlinePinEnumBox.SelectedItem = _viewModel.FormatPinValue(pin, current);
            InlinePinEnumBox.FontSize = Math.Clamp(12 * GraphCanvas.ViewScale, 9, 28);
            InlinePinEnumBox.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
            InlinePinEnumBox.MinWidth = Math.Max(rect.Width + 20, 80);
            InlinePinEnumBox.Visibility = Visibility.Visible;
            _inlineEnumPin = pin;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                InlinePinEnumBox.Focus();
                InlinePinEnumBox.IsDropDownOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        _inlinePin = pin;
        InlinePinEditor.ClearValue(Control.BorderBrushProperty);
        InlinePinEditor.ToolTip = null;
        InlinePinEditor.Text = current;
        InlinePinEditor.FontSize = Math.Clamp(12 * GraphCanvas.ViewScale, 9, 28);
        InlinePinEditor.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        InlinePinEditor.MinWidth = Math.Max(rect.Width, 60);
        InlinePinEditor.Height = Math.Max(rect.Height, 20);
        InlinePinEditor.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            InlinePinEditor.Focus();
            InlinePinEditor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseInlinePinEditor(bool commit)
    {
        if (_inlinePin == null)
            return;

        var pin = _inlinePin;
        var text = InlinePinEditor.Text;
        _inlinePin = null;
        InlinePinEditor.Visibility = Visibility.Collapsed;
        if (commit)
            _ = CommitPinAsync(pin, text); // an invalid value is dropped; the side panel shows how to fix it
    }

    private async void OnInlinePinKeyDown(object sender, KeyEventArgs e)
    {
        if (_inlinePin == null)
            return;

        if (e.Key == Key.Escape)
        {
            CloseInlinePinEditor(false);
            GraphCanvas.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var pin = _inlinePin;
            var error = await CommitPinAsync(pin, InlinePinEditor.Text);
            if (error != null)
            {
                // stay open so the value can be fixed
                InlinePinEditor.BorderBrush = System.Windows.Media.Brushes.IndianRed;
                InlinePinEditor.ToolTip = error;
                return;
            }

            _inlinePin = null;
            InlinePinEditor.Visibility = Visibility.Collapsed;
            GraphCanvas.Focus();
        }
    }

    private void OnInlinePinLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseInlinePinEditor(true);

    private GraphPin _inlineEnumPin;

    private async void OnInlinePinEnumSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_inlineEnumPin is not { } pin || InlinePinEnumBox.SelectedItem is not string picked)
            return;

        _inlineEnumPin = null;
        InlinePinEnumBox.Visibility = Visibility.Collapsed;
        await CommitPinAsync(pin, picked);
        GraphCanvas.Focus();
    }

    private void OnInlinePinEnumClosed(object sender, EventArgs e)
    {
        // closed without picking another value
        _inlineEnumPin = null;
        InlinePinEnumBox.Visibility = Visibility.Collapsed;
    }

    #endregion

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
            await NavigateToNodeAsync(item, entry.Node);
    }

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

    private void OnPinApplyClick(object sender, RoutedEventArgs e) => ApplyPinEdit();

    private void OnPinEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ApplyPinEdit();
        e.Handled = true;
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

    private void ShowCdoEditor(CdoPropertyItem item)
    {
        CdoError.Visibility = Visibility.Collapsed;
        if (item == null)
        {
            CdoSelectionTitle.Text = string.Empty;
            CdoValueBox.Visibility = Visibility.Collapsed;
            CdoJsonEditor.Visibility = Visibility.Collapsed;
            CdoApplyButton.IsEnabled = CdoAddButton.IsEnabled = CdoRemoveButton.IsEnabled = false;
            return;
        }

        CdoSelectionTitle.Text = $"{item.DisplayName} : {item.TypeText}  ({item.Owner})" +
                                 (item.IsOverridden ? string.Empty : Environment.NewLine + L("BlueprintEditor_Inherited"));
        CdoApplyButton.IsEnabled = item.IsOverridden;
        CdoAddButton.IsEnabled = !item.IsOverridden;
        CdoRemoveButton.IsEnabled = item.IsOverridden;

        var text = _viewModel.Editor.GetEditText(item);
        if (item.IsSimple)
        {
            CdoJsonEditor.Visibility = Visibility.Collapsed;
            CdoValueBox.Visibility = item.IsOverridden ? Visibility.Visible : Visibility.Collapsed;
            var values = _viewModel.Editor.GetEnumValues(item).ToList();
            if (item.Property.Type == UAssetAPI.Unversioned.EPropertyType.BoolProperty)
                values = ["true", "false"];
            CdoValueBox.ItemsSource = values;
            CdoValueBox.Text = text;
        }
        else
        {
            CdoValueBox.Visibility = Visibility.Collapsed;
            CdoJsonEditor.Visibility = item.IsOverridden ? Visibility.Visible : Visibility.Collapsed;
            CdoJsonEditor.Text = text;
        }
    }

    private void ShowCdoError(string error)
    {
        CdoError.Text = error ?? string.Empty;
        CdoError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCdoApplyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCdoItem is not { } item)
            return;

        var error = _viewModel.Editor.ApplyCdoValue(item, item.IsSimple ? CdoValueBox.Text : CdoJsonEditor.Text);
        if (error == null)
            ShowCdoEditor(item);
        ShowCdoError(error);
    }

    private void OnCdoValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        OnCdoApplyClick(sender, e);
        e.Handled = true;
    }

    private void OnCdoAddClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCdoItem is not { } item)
            return;

        var error = _viewModel.Editor.AddCdoOverride(item);
        if (error == null)
            ShowCdoEditor(item);
        ShowCdoError(error);
    }

    private void OnCdoRemoveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCdoItem is not { } item)
            return;

        _viewModel.Editor.RemoveCdoOverride(item);
        ShowCdoEditor(item);
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

        var hadPinEdits = _viewModel.Editor.Session?.ConstantChangeCount > 0 || _viewModel.Editor.Session?.WireChangeCount > 0;
        await _viewModel.Editor.DiscardAsync();
        ShowCdoEditor(SelectedCdoItem);
        ApplyCdoFilter();

        if (hadPinEdits && FunctionList.SelectedItem is BlueprintFunctionItem item)
        {
            // pins show edited values: decompile again from the original bytecode
            _viewModel.ClearGraphCache();
            var result = await _viewModel.OpenAsync(item);
            if (result != null)
            {
                GraphCanvas.SetGraph(result.Graph);
                FunctionJsonEditor.Text = result.BytecodeJson ?? string.Empty;
                UpdateSearch();
                GraphCanvas.ZoomToFit();
            }
        }
    }

    public bool OnPanelClosing()
    {
        return !_viewModel.Editor.HasUnsavedChanges ||
               MessageBox.Show(Window.GetWindow(this)!, L("BlueprintEditor_CloseConfirm"), _viewModel.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public void OnPanelClosed()
    {
        _viewModel.Editor.Close();
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

        if (pin != null && _viewModel.Editor.CanEdit)
        {
            var breakItem = new MenuItem { Header = L("BlueprintViewer_BreakLinks") };
            breakItem.Click += (_, _) => OnPinBreakRequested(pin);
            menu.Items.Add(breakItem);
            if (_viewModel.Editor.FindWire(pin) != null)
            {
                var revert = new MenuItem { Header = L("BlueprintViewer_RevertWire") };
                revert.Click += (_, _) =>
                {
                    _viewModel.Editor.RevertPin(pin);
                    RefreshAfterPinChange();
                };
                menu.Items.Add(revert);
            }
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


    #region wires

    private async void OnWireDropped(GraphPin from, GraphPin to)
    {
        if (_viewModel.Current is not { FunctionName: { } functionName } current)
            return;
        if (!current.IsBytecodeComplete)
        {
            _viewModel.Editor.Status = L("BlueprintEditor_BytecodeIncomplete");
            return;
        }

        var error = await _viewModel.Editor.ConnectAsync(from, to, functionName, FunctionList.SelectedItem, current.Code, current.Graph.Nodes);
        _viewModel.Editor.Status = error ?? string.Empty;
        if (error == null)
            RefreshAfterPinChange();
    }

    private async void OnPinBreakRequested(GraphPin pin)
    {
        if (_viewModel.Current is not { FunctionName: { } functionName } current)
            return;
        if (!current.IsBytecodeComplete)
        {
            _viewModel.Editor.Status = L("BlueprintEditor_BytecodeIncomplete");
            return;
        }

        // breaking an exec input means breaking the exec output that leads to it (when there is exactly one)
        if (WirePlannerIsExecInput(pin) && pin.LinkedTo.Count == 1)
            pin = pin.LinkedTo[0];
        // breaking a data output means breaking the input it feeds (when there is exactly one)
        else if (!FModel.Framework.BlueprintEditing.WirePlanner.IsExec(pin) && pin.IsOutput && pin.LinkedTo.Count == 1)
            pin = pin.LinkedTo[0];

        var error = await _viewModel.Editor.BreakAsync(pin, functionName, FunctionList.SelectedItem, current.Code, current.Graph.Nodes);
        _viewModel.Editor.Status = error ?? string.Empty;
        if (error == null)
            RefreshAfterPinChange();
    }

    private static bool WirePlannerIsExecInput(GraphPin pin) => FModel.Framework.BlueprintEditing.WirePlanner.IsExec(pin) && pin.IsInput;

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
        else if (!inTextBox && e.Key == Key.Delete && GraphCanvas.IsKeyboardFocusWithin && GraphCanvas.SelectedPin is { } selectedPin && _viewModel.Editor.CanEdit)
        {
            // like deleting a wire in the editor: break the selected pin's link
            OnPinBreakRequested(selectedPin);
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
