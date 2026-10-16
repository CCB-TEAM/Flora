using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FModel.Framework.Mappings;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Microsoft.Win32;

namespace FModel.Views;

public partial class MappingWizard
{
    private bool _initialized;

    private static string L(string key) => FModel.Localization.LocalizationManager.Instance.Get(key);

    public ObservableCollection<MappingCandidate> Candidates { get; } = [];

    public MappingWizard()
    {
        DataContext = this;
        InitializeComponent();
        HelpText.Text = string.Format(L("MappingWizard_Help"), MappingDiscovery.Dumper7DefaultFolder);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // a dock panel is loaded again every time it's moved
        if (_initialized) return;
        _initialized = true;
        await RescanAsync();
    }

    private async Task RescanAsync()
    {
        StatusText.Text = L("MappingWizard_Scanning");
        var gameDir = UserSettings.Default.CurrentDir?.GameDirectory ?? string.Empty;
        var output = UserSettings.Default.OutputDirectory;
        var found = await Task.Run(() => MappingDiscovery.Scan(gameDir, output));

        Candidates.Clear();
        foreach (var candidate in found)
            Candidates.Add(candidate);

        UpdateCurrent();
        StatusText.Text = found.Count == 0 ? L("MappingWizard_NoneFound") : string.Format(L("MappingWizard_Found"), found.Count);
    }

    private void UpdateCurrent()
    {
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var path = provider?.MappingsContainer is { } container
            ? CUE4ParseViewModel.GetMappingsFilePath(container)
            : null;
        CurrentText.Text = string.IsNullOrEmpty(path) ? L("MappingWizard_ActiveNone") : string.Format(L("MappingWizard_Active"), path);
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not MappingCandidate candidate)
        {
            StatusText.Text = L("MappingWizard_SelectFirst");
            return;
        }

        await ApplyAsync(candidate.Path);
    }

    private async Task ApplyAsync(string path)
    {
        var applicationView = ApplicationService.ApplicationView;
        UserSettings.IsEndpointValid(EEndpointType.Mapping, out var oldEndpoint);
        try
        {
            var endpoint = new EndpointSettings { Overwrite = true, FilePath = path };
            UserSettings.Default.CurrentDir.Endpoints[(int) EEndpointType.Mapping] = endpoint;
            await applicationView.CUE4Parse.InitMappings();
            if (applicationView.CUE4Parse.Provider.MappingsContainer == null)
                throw new InvalidDataException(L("MappingWizard_LoadFailed"));

            applicationView.SettingsView.MappingEndpoint = endpoint;
            StatusText.Text = string.Format(L("MappingWizard_Applied"), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            UserSettings.Default.CurrentDir.Endpoints[(int) EEndpointType.Mapping] = oldEndpoint;
            await applicationView.CUE4Parse.InitMappings();
            StatusText.Text = string.Format(L("MappingWizard_Failed"), ex.Message);
        }

        UpdateCurrent();
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await RescanAsync();

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("MappingWizard_BrowseTitle"),
            Filter = $"{L("MappingWizard_BrowseFilter")} (*.usmap;*.jmap;*.jmap.gz)|*.usmap;*.jmap;*.jmap.gz|{L("MappingWizard_AllFiles")} (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        await ApplyAsync(dialog.FileName);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = (CandidateList.SelectedItem as MappingCandidate)?.Folder
                     ?? MappingDiscovery.SearchRoots(UserSettings.Default.CurrentDir?.GameDirectory ?? string.Empty, UserSettings.Default.OutputDirectory).FirstOrDefault();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusText.Text = L("MappingWizard_NothingToOpen");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }
}
