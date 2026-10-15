using AdonisUI.Controls;
using FModel.Extensions;
using FModel.Framework;
using FModel.Views.Resources.Controls;
using System.Windows;
using System.Windows.Media;
using FModel.Views.Resources.Converters;
using FModel.Framework.Docking;

namespace FModel.ViewModels.Commands;

public class ImageCommand : ViewModelCommand<TabItem>
{
    public ImageCommand(TabItem contextViewModel) : base(contextViewModel)
    {
    }

    public override void Execute(TabItem tabViewModel, object parameter)
    {
        if (parameter == null || !tabViewModel.HasImage) return;

        switch (parameter)
        {
            case "Open":
            {
                DockPanels.ShowImage(tabViewModel.SelectedImage.ExportName + " (Image)", tabViewModel.SelectedImage.Image,
                    tabViewModel.SelectedImage.RenderNearestNeighbor);
                break;
            }
            case "Copy":
                ClipboardExtensions.SetImage(tabViewModel.SelectedImage.ImageBuffer, $"{tabViewModel.SelectedImage.ExportName}.png");
                break;
            case "Save":
                tabViewModel.SaveImage();
                break;
        }
    }
}
