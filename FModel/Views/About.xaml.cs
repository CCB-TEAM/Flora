using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

public partial class About
{
    private readonly AboutViewModel _viewModel;
    private bool _initialized;

    public About()
    {
        DataContext = _viewModel = new AboutViewModel();
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // a dock panel is loaded again every time it's moved
        if (_initialized) return;
        _initialized = true;
        await _viewModel.Initialize();
    }
}
