using System.Windows;
using System.Windows.Media;
using FModel.Localization;
using FModel.Services;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.BlueprintViewer;
using FModel.Views.Resources.Controls;
using FModel.Views.Resources.Converters;

namespace FModel.Framework.Docking;

/// <summary>
/// Every tool that used to open its own window, opened as a panel of the main window instead.
/// </summary>
public static class DockPanels
{
    public const string SearchId = "Search";
    public const string AudioPlayerId = "AudioPlayer";
    public const string ImageMergerId = "ImageMerger";
    public const string AesManagerId = "AesManager";
    public const string BackupManagerId = "BackupManager";
    public const string SettingsId = "Settings";
    public const string AboutId = "About";

    private static string L(string key) => LocalizationManager.Instance.Get(key);

    /// <summary>
    /// Tools that are reopened when the last session's layout is restored. The search panel isn't one of them:
    /// loading a provider closes it anyway.
    /// </summary>
    public static void RegisterRestorables()
    {
        DockWorkspace.RegisterRestorable(AudioPlayerId, () => new AudioPlayer());
        DockWorkspace.RegisterRestorable(ImageMergerId, () => new ImageMerger());
    }

    public static SearchView ShowSearch(ESearchViewTab tab)
    {
        var view = DockWorkspace.Show(SearchId, L("Search_Title"), () => new SearchView(), EDockTarget.Document);
        view.FocusTab(tab);
        return view;
    }

    public static void CloseSearch() => DockWorkspace.Close(SearchId);

    public static AudioPlayer ShowAudioPlayer() =>
        DockWorkspace.Show(AudioPlayerId, L("AudioPlayer_Title"), () => new AudioPlayer(), EDockTarget.Right, 700, 450);

    public static ImageMerger ShowImageMerger() =>
        DockWorkspace.Show(ImageMergerId, L("Menu_Views_ImageMerger"), () => new ImageMerger(), EDockTarget.Document);

    public static AesManager ShowAesManager() =>
        DockWorkspace.Show(AesManagerId, L("AesManager_Title"), () => new AesManager(), EDockTarget.Right, 620, 500);

    public static BackupManager ShowBackupManager() =>
        DockWorkspace.Show(BackupManagerId, L("Menu_Directory_Backup"),
            () => new BackupManager(ApplicationService.ApplicationView.CUE4Parse.Provider.ProjectName), EDockTarget.Right, 520, 400);

    public static SettingsView ShowSettings() =>
        DockWorkspace.Show(SettingsId, L("Settings_Title"), () => new SettingsView(), EDockTarget.Document);

    public static About ShowAbout() =>
        DockWorkspace.Show(AboutId, L("About_Title"), () => new About(), EDockTarget.Document);

    public static void ShowBlueprintViewer(BlueprintViewerViewModel viewModel) =>
        DockWorkspace.Show($"Blueprint:{viewModel.Title}", viewModel.Title, () => new BlueprintViewerWindow(viewModel), EDockTarget.Document);

    public static void ShowProperties(TabItem tab) =>
        DockWorkspace.Show($"Properties:{tab.Header}", $"{tab.Header} ({L("Dock_Properties")})",
            () => new PropertiesPopout(tab), EDockTarget.Right, 600, 400);

    public static void ShowImage(string title, ImageSource image, bool nearestNeighbor = false)
    {
        var popout = DockWorkspace.Show($"Image:{title}", title, () => new ImagePopout(), EDockTarget.Document);
        popout.ImageCtrl.Source = image;
        RenderOptions.SetBitmapScalingMode(popout.ImageCtrl, BoolToRenderModeConverter.Instance.Convert(nearestNeighbor));
    }
}
