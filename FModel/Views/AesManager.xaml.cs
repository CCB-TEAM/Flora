using System.Windows;
using FModel.Services;
using FModel.ViewModels;
using FModel.Framework.Docking;

namespace FModel.Views;

public partial class AesManager : IDockPanel
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public AesManager()
    {
        DataContext = _applicationView;
        InitializeComponent();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        DockWorkspace.Close(this);
    }

    private async void OnRefreshAes(object sender, RoutedEventArgs e)
    {
        await _applicationView.CUE4Parse.RefreshAes();
        await _applicationView.AesManager.InitAes();
        _applicationView.AesManager.HasChange = true; // yes even if nothing actually changed
    }

    public async void OnPanelClosed()
    {
        await _applicationView.UpdateProvider(false);
    }
}
