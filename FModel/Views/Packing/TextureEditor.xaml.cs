using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using FModel.Framework.Packing;
using FModel.Framework.Packing.Texture;
using FModel.Localization;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Microsoft.Win32;
using Serilog;
using SkiaSharp;

namespace FModel.Views.Packing;

/// <summary>Shows a texture of the pak and replaces its pixels with an image, encoded into the format of the asset.</summary>
public partial class TextureEditor : IPackEditor
{
    private static string L(string key) => LocalizationManager.Instance.Get(key);
    private const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp|All files|*.*";

    private PackWorkspaceViewModel _viewModel = null!;
    private PackItem _item = null!;
    private byte[]? _png;

    public TextureEditor() => InitializeComponent();

    /// <summary>Whether the file is a texture asset (a .uasset of Texture2D with its .uexp next to it).</summary>
    public static bool IsTexture(PackItem item) => PackReplace.IsTexture(item);

    public void Load(PackItem item, PackWorkspaceViewModel viewModel)
    {
        _item = item;
        _viewModel = viewModel;
        TitleText.Text = item.PakPath;
        InfoText.Text = "";
        ResultText.Text = "";
        Preview.Source = null;
        _png = null;
        _ = RefreshAsync(item);
    }

    private async Task RefreshAsync(PackItem item)
    {
        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        try
        {
            var (info, png) = await Task.Run(async () =>
            {
                var inspection = await TextureReplaceService.InspectAsync(item.DiskPath, cue4.ResolvePackEngineVersion(), cue4.ResolvePackUsmapPath());
                byte[]? image = null;
                try
                {
                    var package = cue4.ReadLegacyPackageFromDisk(Path.ChangeExtension(item.PakPath, null), item.DiskPath);
                    if (package.GetExports().OfType<UTexture2D>().FirstOrDefault() is { } texture)
                    {
                        using var bitmap = texture.Decode(UserSettings.Default.CurrentDir.TexturePlatform)?.ToSkBitmap();
                        image = bitmap?.Encode(SKEncodedImageFormat.Png, 100)?.ToArray();
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Could not preview {Path}", item.PakPath);
                }
                return (inspection, image);
            });

            if (!ReferenceEquals(item, _item))
                return;
            InfoText.Text = string.Format(L("Pack_TexInfo"), info.Format, info.Width, info.Height, info.MipCount);
            _png = png;
            if (png != null)
                ShowPng(png);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to read texture {Path}", item.PakPath);
            if (ReferenceEquals(item, _item))
                InfoText.Text = string.Format(L("Pack_StatusFailed"), e.Message);
        }
    }

    private void ShowPng(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        Preview.Source = image;
    }

    private async void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter };
        if (dialog.ShowDialog() == true)
            await ReplaceAsync(dialog.FileName);
    }

    private async void OnImageDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            await ReplaceAsync(files[0]);
            e.Handled = true;
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_png == null) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(_item.PakPath) + ".png", Filter = "PNG|*.png" };
        if (dialog.ShowDialog() == true)
            File.WriteAllBytes(dialog.FileName, _png);
    }

    private async Task ReplaceAsync(string imagePath)
    {
        var item = _item;
        ReplaceButton.IsEnabled = false;
        ResultText.Text = L("Pack_TexEncoding");
        try
        {
            await PackReplace.ReplaceTextureAsync(_viewModel, item, imagePath);
            ResultText.Text = string.Format(L("Pack_TexDone"), Path.GetFileName(imagePath));
            await RefreshAsync(item);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to replace texture {Path}", item.PakPath);
            ResultText.Text = string.Format(L("Pack_StatusFailed"), e.Message);
        }
        finally
        {
            ReplaceButton.IsEnabled = true;
        }
    }
}
