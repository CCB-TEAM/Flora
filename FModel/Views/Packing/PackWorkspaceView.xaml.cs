using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FModel.Framework.Packing;
using FModel.Localization;
using FModel.ViewModels;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;

namespace FModel.Views.Packing;

public partial class PackWorkspaceView
{
    private static string L(string key) => LocalizationManager.Instance.Get(key);
    private const string ProjectFilter = "Flora pack project (*.florapack)|*.florapack";

    private readonly PackWorkspaceViewModel _viewModel = new();

    /// <summary>The workspace shown in this panel (the agent tools work on it).</summary>
    public PackWorkspaceViewModel ViewModel => _viewModel;
    private Point _dragStart;
    private bool _loadingSettings;
    private string _lastTargetFolder = "";

    public PackWorkspaceView()
    {
        DataContext = _viewModel;
        InitializeComponent();
        VersionBox.ItemsSource = Enum.GetValues<UAssetAPI.PakVersion>();
        _viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            _viewModel.EnsureGameSource();
            LoadSettings();
        };
    }

    private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackWorkspaceViewModel.Project))
            LoadSettings();
    }

    /// <summary>Shows the project settings again after they were changed elsewhere (the agent).</summary>
    public void ReloadSettings() => LoadSettings();

    private void LoadSettings()
    {
        _loadingSettings = true;
        var settings = _viewModel.Project.Settings;
        MountPointBox.Text = settings.MountPoint;
        VersionBox.SelectedItem = (UAssetAPI.PakVersion) settings.PakVersion;
        CompressBox.IsChecked = settings.Compress;
        _loadingSettings = false;
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !IsLoaded) return;
        var settings = _viewModel.Project.Settings;
        settings.MountPoint = string.IsNullOrWhiteSpace(MountPointBox.Text) ? "../../../" : MountPointBox.Text.Trim();
        if (VersionBox.SelectedItem is UAssetAPI.PakVersion version)
            settings.PakVersion = (int) version;
        settings.Compress = CompressBox.IsChecked == true;
    }

    /// <summary>Adds files an editor produced, replacing the unchanged copies.</summary>
    public void AddEditedFiles(IEnumerable<(string PakPath, byte[] Data)> files, string origin) => _viewModel.AddEditedFiles(files, origin);

    #region project

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Project.Items.Count > 0 &&
            MessageBox.Show(L("Pack_ConfirmNew"), L("Pack_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _viewModel.NewProject();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ProjectFilter };
        if (dialog.ShowDialog() != true) return;
        if (_viewModel.OpenProject(dialog.FileName) is { } error)
            MessageBox.Show(error, L("Pack_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Project.FilePath == null) OnSaveAsClick(sender, e);
        else Save(_viewModel.Project.FilePath);
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = ProjectFilter, FileName = _viewModel.Project.Name, DefaultExt = ".florapack" };
        if (dialog.ShowDialog() == true) Save(dialog.FileName);
    }

    private void Save(string file)
    {
        if (_viewModel.SaveProject(file) is { } error)
            MessageBox.Show(error, L("Pack_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void OnBuildClick(object sender, RoutedEventArgs e)
    {
        OnSettingsChanged(sender, e);
        var settings = _viewModel.Project.Settings;
        var dialog = new SaveFileDialog
        {
            Filter = "Unreal pak (*.pak)|*.pak",
            DefaultExt = ".pak",
            FileName = string.IsNullOrEmpty(settings.OutputPath) ? _viewModel.Project.Name + "_P.pak" : Path.GetFileName(settings.OutputPath),
            InitialDirectory = string.IsNullOrEmpty(settings.OutputPath) ? null : Path.GetDirectoryName(settings.OutputPath),
        };
        if (dialog.ShowDialog() != true) return;
        if (await _viewModel.BuildAsync(dialog.FileName) is { } error)
            MessageBox.Show(error, L("Pack_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    #endregion

    #region sources

    private void OnAddFolderSourceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog();
        if (dialog.ShowDialog() == true)
            _viewModel.AddFolderSource(dialog.SelectedPath);
    }

    private async void OnAddPakSourceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unreal pak (*.pak)|*.pak" };
        if (dialog.ShowDialog() == true)
            await _viewModel.AddPakSourceAsync(dialog.FileName);
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedFolder = e.NewValue as PackFolderNode;

    private async void OnAddSelectedClick(object sender, RoutedEventArgs e) => await AddBrowserSelectionAsync();

    private async void OnBrowserDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(BrowserList, source) != null)
            await AddBrowserSelectionAsync();
    }

    private async System.Threading.Tasks.Task AddBrowserSelectionAsync()
    {
        if (_viewModel.SelectedSource is not { } source) return;
        var paths = BrowserList.SelectedItems.OfType<PackSourceFile>().Select(o => o.Path).ToList();
        await _viewModel.AddFromSourceAsync(source, paths);
    }

    #endregion

    #region drag and drop

    private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);
    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private bool PastDragThreshold(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var delta = e.GetPosition(null) - _dragStart;
        return Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!PastDragThreshold(e) || _viewModel.SelectedSource is not { } source) return;
        if (e.OriginalSource is not DependencyObject origin || ItemsControl.ContainerFromElement(BrowserList, origin) == null) return;

        var paths = BrowserList.SelectedItems.OfType<PackSourceFile>().Select(o => o.Path).ToList();
        if (paths.Count == 0) return;
        DragDrop.DoDragDrop(BrowserList, new DataObject(typeof(PackDragData), new PackDragData(source, paths)), DragDropEffects.Copy);
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!PastDragThreshold(e) || _viewModel.SelectedSource is not { } source || _viewModel.SelectedFolder is not { } folder) return;
        if (!IsOnTreeItem(e.OriginalSource as DependencyObject)) return;

        var paths = _viewModel.FilesUnder(folder).ToList();
        if (paths.Count == 0) return;
        DragDrop.DoDragDrop(FolderTree, new DataObject(typeof(PackDragData), new PackDragData(source, paths)), DragDropEffects.Copy);
    }

    private static bool IsOnTreeItem(DependencyObject element)
    {
        for (; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is TreeViewItem)
                return true;
        return false;
    }

    private void OnPackDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PackDragData)) || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPackDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PackDragData)) is PackDragData drag)
        {
            var source = drag.Source ?? _viewModel.GameSource();
            if (source != null)
                await _viewModel.AddFromSourceAsync(source, drag.Paths);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var folder = PromptDialog.Show(Window.GetWindow(this), L("Pack_Title"), L("Pack_TargetFolder"), _lastTargetFolder);
            if (folder == null) return;
            _lastTargetFolder = folder;
            await _viewModel.AddDiskPathsAsync(files, folder);
        }
        e.Handled = true;
    }

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var folder = PromptDialog.Show(Window.GetWindow(this), L("Pack_Title"), L("Pack_TargetFolder"), _lastTargetFolder);
        if (folder == null) return;
        _lastTargetFolder = folder;
        await _viewModel.AddDiskPathsAsync(dialog.FileNames, folder);
    }

    #endregion

    #region pak contents

    private void OnPackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackGrid.SelectedItems.Count != 1 || PackGrid.SelectedItem is not PackItem item)
        {
            EditorHost.Content = null;
            EditorHost.Visibility = Visibility.Collapsed;
            return;
        }

        var editor = CreateEditor(item);
        editor.Load(item, _viewModel);
        EditorHost.Content = editor;
        EditorHost.Visibility = Visibility.Visible;
    }

    private static IPackEditor CreateEditor(PackItem item) =>
        item.Extension == "locres" ? new LocresEditor() : TextureEditor.IsTexture(item) ? new TextureEditor() : AudioEditor.IsAudio(item) ? new AudioEditor() : new GenericFileEditor();

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnPackKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void RemoveSelected() => _viewModel.Remove(PackGrid.SelectedItems.OfType<PackItem>());

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not PackItem item || e.EditingElement is not TextBox box) return;
        e.Cancel = true; // the path is applied by the project (it moves the workspace copy)
        if (_viewModel.Rename(item, box.Text) is { } error)
            MessageBox.Show(error, L("Pack_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        PackGrid.Items.Refresh();
    }

    #endregion
}

/// <summary>A one-line text prompt.</summary>
internal static class PromptDialog
{
    public static string? Show(Window? owner, string title, string label, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 360, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L("Pack_Cancel"), IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(14), Children = { new TextBlock { Text = label }, box, buttons } };
        var window = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Owner = owner, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        window.SetResourceReference(Window.BackgroundProperty, AdonisUI.Brushes.Layer0BackgroundBrush);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim().Replace('\\', '/').Trim('/') : null;
    }

    private static string L(string key) => LocalizationManager.Instance.Get(key);
}
