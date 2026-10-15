using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

public partial class BackupManager
{
    private readonly BackupManagerViewModel _viewModel;
    private bool _initialized;

    public BackupManager(string gameName)
    {
        DataContext = _viewModel = new BackupManagerViewModel(gameName);
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // a dock panel is loaded again every time it's moved
        if (_initialized) return;
        _initialized = true;
        await _viewModel.Initialize();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.Download();
    }

    private async void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.CreateBackup();
    }
}