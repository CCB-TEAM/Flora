using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using FModel.Services;
using FModel.ViewModels;
using UAssetAPI;

namespace FModel.Views;

public partial class ImportManager
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private TabItem _sourceTab;

    public ImportManager(List<Import> imports, TabItem sourceTab = null)
    {
        var importManagerViewModel = new ImportManagerViewModel();
        DataContext = _applicationView;
        _applicationView.ImportManager = importManagerViewModel;
        _sourceTab = sourceTab;
        importManagerViewModel.SetSourceTab(sourceTab);

        InitializeComponent();

        _ = importManagerViewModel.LoadImportsAsync(imports);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_applicationView.ImportManager != null)
        {
            _ = _applicationView.ImportManager.RefreshFromSourceAsync();
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        await _applicationView.UpdateProvider(false);
    }
}
