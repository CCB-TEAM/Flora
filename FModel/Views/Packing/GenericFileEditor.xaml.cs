using System.IO;
using System.Windows;
using FModel.Framework.Packing;
using FModel.Localization;
using FModel.ViewModels;
using Microsoft.Win32;

namespace FModel.Views.Packing;

/// <summary>An editor of a file of the pak. The workspace host puts one next to the list of files.</summary>
public interface IPackEditor
{
    void Load(PackItem item, PackWorkspaceViewModel viewModel);
}

/// <summary>Any file: replace its content with a file from disk, or save it to disk.</summary>
public partial class GenericFileEditor : IPackEditor
{
    private PackWorkspaceViewModel _viewModel = null!;
    private PackItem _item = null!;

    public GenericFileEditor() => InitializeComponent();

    public void Load(PackItem item, PackWorkspaceViewModel viewModel)
    {
        _item = item;
        _viewModel = viewModel;
        DataContext = item;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog();
        if (dialog.ShowDialog() != true) return;
        _viewModel.ReplaceContent(_item, File.ReadAllBytes(dialog.FileName), dialog.FileName);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = _item.FileName };
        if (dialog.ShowDialog() == true)
            File.Copy(_item.DiskPath, dialog.FileName, true);
    }
}
