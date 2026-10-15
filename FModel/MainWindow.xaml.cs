using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using FModel.Framework.Docking;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using ICSharpCode.AvalonEdit.Editing;

namespace FModel;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public static MainWindow YesWeCats;
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private DiscordHandler _discordHandler => DiscordService.DiscordHandler;

    // left panels, in the order of ApplicationViewModel.SelectedLeftTabIndex
    private static readonly string[] LeftPanelIds = ["Archives", "Folders", "Packages"];
    private bool _syncingLeftPanel;

    public MainWindow()
    {
        CommandBindings.Add(new CommandBinding(new RoutedCommand("ReloadMappings", typeof(MainWindow), new InputGestureCollection { new KeyGesture(Key.F12) }), OnMappingsReload));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => OnOpenAvalonFinder()));
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (_, _) =>
        {
            if (UserSettings.Default.FeaturePreviewNewAssetExplorer && !_applicationView.IsAssetsExplorerVisible)
            {
                // back browsing the json view will reopen the assets explorer
                _applicationView.IsAssetsExplorerVisible = true;
                return;
            }

            if (_applicationView.SelectedLeftTabIndex == 2)
            {
                _applicationView.SelectedLeftTabIndex = 1;
            }
            else if (_applicationView.SelectedLeftTabIndex == 1 && AssetsFolderName.SelectedItem is TreeItem { Parent: TreeItem parent })
            {
                AssetsFolderName.Focus();
                parent.IsSelected = true;
            }
        }));

        DataContext = _applicationView;
        InitializeComponent();

        AssetsExplorer.ItemContainerGenerator.StatusChanged += ItemContainerGenerator_StatusChanged;
        AssetsListName.ItemContainerGenerator.StatusChanged += ItemContainerGenerator_StatusChanged;
        AssetsExplorer.SelectionChanged += (_, e) => SyncSelection(AssetsListName, e);
        AssetsListName.SelectionChanged += (_, e) => SyncSelection(AssetsExplorer, e);

        FLogger.Logger = LogRtbName;
        YesWeCats = this;

        // floating dock windows don't inherit the main window's DataContext
        foreach (var panel in new FrameworkElement[] { ArchivesPanel, FoldersPanel, PackagesPanel, WorkspacePanel, OutputPanel })
            panel.DataContext = _applicationView;

        DockWorkspace.Attach(DockManager);
        DockPanels.RegisterRestorables();
        _applicationView.PropertyChanged += OnApplicationViewPropertyChanged;
    }

    // Hack to sync selection between packages tab and explorer
    private void SyncSelection(ListBox target, SelectionChangedEventArgs e)
    {
        foreach (var added in e.AddedItems.OfType<GameFileViewModel>())
        {
            if (!target.SelectedItems.Contains(added))
                target.SelectedItems.Add(added);
        }

        foreach (var removed in e.RemovedItems.OfType<GameFileViewModel>())
        {
            if (target.SelectedItems.Contains(removed))
                target.SelectedItems.Remove(removed);
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        DockWorkspace.SaveLayout();
        _discordHandler.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DockWorkspace.RestoreLayout();
        const bool newOrUpdated = false;

        switch (UserSettings.Default.AesReload)
        {
            case EAesReload.Always:
                await _applicationView.CUE4Parse.RefreshAes();
                break;
            case EAesReload.OncePerDay when UserSettings.Default.CurrentDir.LastAesReload != DateTime.Today:
                UserSettings.Default.CurrentDir.LastAesReload = DateTime.Today;
                await _applicationView.CUE4Parse.RefreshAes();
                break;
        }

        await Task.WhenAll(
            ApplicationViewModel.InitOodle(),
            ApplicationViewModel.InitZlib()
        );

        await _applicationView.CUE4Parse.Initialize();
        await _applicationView.AesManager.InitAes();
        await _applicationView.UpdateProvider(true);
#if !DEBUG
        await _applicationView.CUE4Parse.InitInformation();
#endif

        await Task.WhenAll(
            _applicationView.CUE4Parse.VerifyConsoleVariables(),
            _applicationView.CUE4Parse.VerifyOnDemandArchives(),
            _applicationView.CUE4Parse.InitMappings(),
            ApplicationViewModel.InitDetex(),
            ApplicationViewModel.InitVgmStream(),
            ApplicationViewModel.InitImGuiSettings(newOrUpdated),
            Task.Run(() =>
            {
                if (UserSettings.Default.DiscordRpc == EDiscordRpc.Always)
                    _discordHandler.Initialize(_applicationView.GameDisplayName);
            })
        ).ConfigureAwait(false);

#if DEBUG
        // await _threadWorkerView.Begin(cancellationToken =>
        //     _applicationView.CUE4Parse.Extract(cancellationToken,
        //         _applicationView.CUE4Parse.Provider["Marvel/Content/Marvel/Wwise/Assets/Events/Music/music_new/event/Entry.uasset"]));
#endif
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is TextArea && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        if (_threadWorkerView.CanBeCanceled && e.Key == Key.Escape)
        {
            _applicationView.Status.SetStatus(EStatusKind.Stopping);
            _threadWorkerView.Cancel();
        }
        else if (_applicationView.Status.IsReady && e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            OnSearchViewClick(null, null);
        else if (_applicationView.Status.IsReady && e.Key == Key.R && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            OnRefViewClick(null, null);
        else if (e.Key == Key.F3)
            OnOpenAvalonFinder();
        else if (e.Key == Key.Left && !_applicationView.IsAssetsExplorerVisible && _applicationView.CUE4Parse.TabControl.SelectedTab is { HasImage: true })
            _applicationView.CUE4Parse.TabControl.SelectedTab.GoPreviousImage();
        else if (e.Key == Key.Right && !_applicationView.IsAssetsExplorerVisible && _applicationView.CUE4Parse.TabControl.SelectedTab is { HasImage: true })
            _applicationView.CUE4Parse.TabControl.SelectedTab.GoNextImage();
        else if (_applicationView.Status.IsReady && _applicationView.IsAssetsExplorerVisible && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            CategoriesSelector.SelectedIndex = e.SystemKey switch
            {
                Key.D0 or Key.NumPad0 => 0,
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => CategoriesSelector.SelectedIndex
            };
        }
        else if (_applicationView.Status.IsReady && UserSettings.Default.FeaturePreviewNewAssetExplorer && UserSettings.Default.SwitchAssetExplorer.IsTriggered(e.Key))
            _applicationView.IsAssetsExplorerVisible = !_applicationView.IsAssetsExplorerVisible;
        else if (UserSettings.Default.AssetAddTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.AddTab();
        else if (UserSettings.Default.AssetRemoveTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.RemoveTab();
        else if (UserSettings.Default.AssetLeftTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoLeftTab();
        else if (UserSettings.Default.AssetRightTab.IsTriggered(e.Key))
            _applicationView.CUE4Parse.TabControl.GoRightTab();
        else if (UserSettings.Default.DirLeftTab.IsTriggered(e.Key) && _applicationView.SelectedLeftTabIndex > 0)
            _applicationView.SelectedLeftTabIndex--;
        else if (UserSettings.Default.DirRightTab.IsTriggered(e.Key) && _applicationView.SelectedLeftTabIndex < LeftPanelIds.Length - 1)
            _applicationView.SelectedLeftTabIndex++;
    }

    private void OnSearchViewClick(object sender, RoutedEventArgs e) => DockPanels.ShowSearch(ESearchViewTab.SearchView);

    private void OnRefViewClick(object sender, RoutedEventArgs e) => DockPanels.ShowSearch(ESearchViewTab.RefView);

    // the left tab index is still what the rest of the app drives, mirror it on the dock panels both ways
    private void OnApplicationViewPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApplicationViewModel.SelectedLeftTabIndex) || _syncingLeftPanel)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowLeftPanel(_applicationView.SelectedLeftTabIndex));
            return;
        }

        ShowLeftPanel(_applicationView.SelectedLeftTabIndex);
    }

    private void ShowLeftPanel(int index)
    {
        if (index < 0 || index >= LeftPanelIds.Length)
            return;

        _syncingLeftPanel = true;
        try
        {
            DockWorkspace.Activate(LeftPanelIds[index]);
        }
        finally
        {
            _syncingLeftPanel = false;
        }

        Control target = index switch
        {
            0 => DirectoryFilesListBox,
            1 => AssetsFolderName,
            _ => AssetsListName
        };
        Dispatcher.BeginInvoke(() => target.Focus(), DispatcherPriority.Input);
    }

    private void OnActiveDockContentChanged(object sender, EventArgs e)
    {
        if (_syncingLeftPanel)
            return;

        var active = DockManager.ActiveContent;
        var index = ReferenceEquals(active, ArchivesPanel) ? 0 :
            ReferenceEquals(active, FoldersPanel) ? 1 :
            ReferenceEquals(active, PackagesPanel) ? 2 : -1;
        if (index < 0)
            return;

        _syncingLeftPanel = true;
        try
        {
            _applicationView.SelectedLeftTabIndex = index;
        }
        finally
        {
            _syncingLeftPanel = false;
        }
    }

    private void OnShowPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string contentId })
            DockWorkspace.Activate(contentId);
    }

    private LayoutContent ActiveLayoutContent => DockManager.Layout?.ActiveContent;

    private void OnWindowMenuOpened(object sender, RoutedEventArgs e)
    {
        var active = ActiveLayoutContent;
        PinActivePanelMenuItem.IsEnabled = DetachActivePanelMenuItem.IsEnabled = active is { CanFloat: true } || active is { IsFloating: true };
        PinActivePanelMenuItem.IsChecked = active != null && DockWorkspace.IsPinned(active);
        DetachActivePanelMenuItem.IsChecked = active != null && DockWorkspace.IsDetached(active);
    }

    private void OnPinActivePanelClick(object sender, RoutedEventArgs e)
    {
        if (ActiveLayoutContent is { } active)
            DockWorkspace.SetPinned(active, PinActivePanelMenuItem.IsChecked);
    }

    private void OnDetachActivePanelClick(object sender, RoutedEventArgs e)
    {
        if (ActiveLayoutContent is { } active)
            DockWorkspace.SetDetached(active, DetachActivePanelMenuItem.IsChecked);
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e) => DockWorkspace.ResetLayout();

    private async void OnMappingsReload(object sender, ExecutedRoutedEventArgs e)
    {
        await _applicationView.CUE4Parse.InitMappings(true);
    }

    private void OnOpenAvalonFinder()
    {
        if (_applicationView.IsAssetsExplorerVisible)
        {
            AssetsExplorerSearch.TextBox.Focus();
            AssetsExplorerSearch.TextBox.SelectAll();
        }
        else if (_applicationView.CUE4Parse.TabControl.SelectedTab is { } tab)
        {
            tab.HasSearchOpen = true;
            AvalonEditor.YesWeSearch.Focus();
            AvalonEditor.YesWeSearch.SelectAll();
        }
    }

    private void OnAssetsTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: TreeItem treeItem } || treeItem.Folders.Count > 0) return;

        _applicationView.SelectedLeftTabIndex++;
    }

    private void OnPreviewTexturesToggled(object sender, RoutedEventArgs e) => ItemContainerGenerator_StatusChanged(AssetsExplorer.ItemContainerGenerator, EventArgs.Empty);
    private void ItemContainerGenerator_StatusChanged(object sender, EventArgs e)
    {
        if (sender is not ItemContainerGenerator { Status: GeneratorStatus.ContainersGenerated } generator)
            return;

        var foundVisibleItem = false;
        var itemCount = generator.Items.Count;

        for (var i = 0; i < itemCount; i++)
        {
            var container = generator.ContainerFromIndex(i);
            if (container == null)
            {
                if (foundVisibleItem) break; // we're past the visible range already
                continue; // keep scrolling to find visible items
            }

            if (container is FrameworkElement { IsVisible: true } && generator.Items[i] is GameFileViewModel file)
            {
                foundVisibleItem = true;
                file.OnIsVisible();
            }
        }
    }

    private void OnAssetsTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView { SelectedItem: TreeItem }) return;

        _applicationView.IsAssetsExplorerVisible = true;
        _applicationView.SelectedLeftTabIndex = 1;
    }

    private async void OnAssetsListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        var selectedItems = listBox.SelectedItems.OfType<GameFileViewModel>().Select(gvm => gvm.Asset).ToArray();
        if (selectedItems.Length == 0) return;

        await _threadWorkerView.Begin(cancellationToken => { _applicationView.CUE4Parse.ExtractSelected(cancellationToken, selectedItems); });
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        if (AssetsFolderName.SelectedItem is TreeItem folder)
        {
            folder.SearchText = string.Empty;
            folder.SelectedCategory = EAssetCategory.All;
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox) return;
        UserSettings.Default.LoadingMode = ELoadingMode.Multiple;
        _applicationView.LoadingModes.LoadCommand.Execute(listBox.SelectedItems);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_applicationView.Status.IsReady || sender is not ListBox listBox)
            return;
        if (e.Key != Key.Enter)
            return;
        if (listBox.SelectedItem == null)
            return;

        switch (listBox.SelectedItem)
        {
            case GameFileViewModel file:
                _applicationView.IsAssetsExplorerVisible = false;
                ApplicationService.ApplicationView.SelectedLeftTabIndex = 2;
                await _threadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.ExtractSelected(cancellationToken, [file.Asset]));
                break;
            case TreeItem folder:
                ApplicationService.ApplicationView.SelectedLeftTabIndex = 1;

                var parent = folder.Parent;
                while (parent != null)
                {
                    parent.IsExpanded = true;
                    parent = parent.Parent;
                }

                var childFolder = folder;
                while (childFolder.Folders.Count == 1 && childFolder.AssetsList.Assets.Count == 0)
                {
                    childFolder.IsExpanded = true;
                    childFolder = childFolder.Folders[0];
                }

                childFolder.IsExpanded = true;
                childFolder.IsSelected = true;
                break;
        }
    }

    private void FeaturePreviewOnUnchecked(object sender, RoutedEventArgs e)
    {
        _applicationView.IsAssetsExplorerVisible = false;
    }

    private async void OnFoldersPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TreeView treeView || treeView.SelectedItem is not TreeItem folder)
            return;

        if ((folder.IsExpanded || folder.Folders.Count == 0) && folder.AssetsList.Assets.Count > 0)
        {
            _applicationView.SelectedLeftTabIndex++;
            return;
        }

        var childFolder = folder;
        while (childFolder.Folders.Count == 1 && childFolder.AssetsList.Assets.Count == 0)
        {
            childFolder.IsExpanded = true;
            childFolder = childFolder.Folders[0];
        }

        childFolder.IsExpanded = true;
        childFolder.IsSelected = true;
    }
}
