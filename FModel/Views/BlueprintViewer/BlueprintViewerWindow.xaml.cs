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

        // open the ubergraph (first event) or the first function right away
        FunctionList.SelectedItem = _viewModel.Functions.FirstOrDefault();

        if (_viewModel.IsWidgetBlueprint)
        {
            InitDesigner();
            ModeTabs.SelectedIndex = 1; // like the editor, widget blueprints open on the designer
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
        if (FunctionList.SelectedItem is not BlueprintFunctionItem item)
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
        if (pin == null || !_viewModel.Editor.CanEdit || _viewModel.Current?.FunctionName == null || !BlueprintEditorViewModel.IsEditablePin(pin))
        {
            _editedPin = null;
            PinEditPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _editedPin = pin;
        PinEditLabel.Text = string.Format(L("BlueprintEditor_PinValue"), pin.PinFriendlyName, BlueprintEditorViewModel.GetPinKind(pin),
            pin.SourceConstants.Count);
        PinEditBox.Text = _viewModel.Editor.GetPinEditText(pin, _viewModel.Current.FunctionName);
        PinEditPanel.Visibility = Visibility.Visible;
    }

    private async void ApplyPinEdit()
    {
        if (_editedPin == null || _viewModel.Current?.FunctionName is not { } functionName)
            return;

        var pin = _editedPin;
        var error = await _viewModel.Editor.ApplyPinEditAsync(pin, functionName, PinEditBox.Text);
        PinEditError.Text = error ?? string.Empty;
        PinEditError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null)
            return;

        GraphCanvas.InvalidateVisual();
        _viewModel.DescribeSelection(GraphCanvas.SelectedNodes, GraphCanvas.SelectedPin);
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

        var hadPinEdits = _viewModel.Editor.Session?.ConstantChangeCount > 0;
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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
