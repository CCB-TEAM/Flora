using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse_Conversion.Sounds;
using FModel.Framework.Audio.Binka;
using FModel.Framework.Docking;
using FModel.Framework.Packing;
using FModel.Framework.Packing.Audio;
using FModel.Localization;
using FModel.Services;
using FModel.ViewModels;
using Microsoft.Win32;
using Serilog;

namespace FModel.Views.Packing;

/// <summary>Plays a sound of the pak and replaces the whole sound asset with another one.</summary>
public partial class AudioEditor : IPackEditor
{
    private static string L(string key) => LocalizationManager.Instance.Get(key);

    private PackWorkspaceViewModel _viewModel = null!;
    private PackItem _item = null!;
    private byte[]? _audio;
    private string _extension = "";

    public AudioEditor() => InitializeComponent();

    /// <summary>Whether the file is a sound asset (a .uasset of SoundWave with its .uexp next to it).</summary>
    public static bool IsAudio(PackItem item)
    {
        if (item.Extension != "uasset" || !File.Exists(Path.ChangeExtension(item.DiskPath, ".uexp")))
            return false;
        try
        {
            using var stream = File.OpenRead(item.DiskPath);
            var buffer = new byte[(int) Math.Min(stream.Length, 262144)];
            stream.ReadExactly(buffer);
            return System.Text.Encoding.ASCII.GetString(buffer).Contains("SoundWave", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public void Load(PackItem item, PackWorkspaceViewModel viewModel)
    {
        _item = item;
        _viewModel = viewModel;
        _audio = null;
        TitleText.Text = item.PakPath;
        InfoText.Text = "";
        PlayButton.IsEnabled = ExportButton.IsEnabled = false;
        _ = RefreshAsync(item);
    }

    private async Task RefreshAsync(PackItem item)
    {
        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        try
        {
            var result = await Task.Run(() =>
            {
                var package = cue4.ReadLegacyPackageFromDisk(Path.ChangeExtension(item.PakPath, null), item.DiskPath);
                var wave = package.GetExports().OfType<USoundWave>().FirstOrDefault() ?? throw new InvalidOperationException("No SoundWave in the package.");
                wave.Decode(true, out var format, out var data);
                var detail = $"{wave.GetOrDefault<int>("NumChannels")} ch, {wave.GetOrDefault<int>("SampleRate")} Hz, {wave.GetOrDefault<float>("Duration"):0.##} s";
                return (Format: format ?? "?", Data: data, Detail: detail);
            });

            if (!ReferenceEquals(item, _item))
                return;
            _audio = result.Data;
            _extension = result.Format.ToLowerInvariant();
            InfoText.Text = string.Format(L("Pack_AudioInfo"), result.Format, result.Detail, result.Data?.Length ?? 0);
            PlayButton.IsEnabled = ExportButton.IsEnabled = result.Data != null;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to read sound {Path}", item.PakPath);
            if (ReferenceEquals(item, _item))
                InfoText.Text = string.Format(L("Pack_StatusFailed"), e.Message);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_audio == null) return;
        var path = Path.Combine(Path.GetTempPath(), "FloraPack", "preview", Path.ChangeExtension(Path.GetFileName(_item.PakPath), _extension));
        DockPanels.ShowAudioPlayer().Load(_audio, path.Replace('\\', '/'));
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_audio == null) return;
        var data = _audio;
        var extension = _extension;
        if (BinkaAudio.IsBinka(data) && BinkaAudio.TryDecodeToWav(data, out var wav, out _))
        {
            data = wav;
            extension = "wav";
        }
        var dialog = new SaveFileDialog { FileName = Path.ChangeExtension(Path.GetFileName(_item.PakPath), extension) };
        if (dialog.ShowDialog() == true)
            File.WriteAllBytes(dialog.FileName, data);
    }

    /// <summary>Encodes an audio file into Bink Audio and puts it into this sound, keeping the asset (its path, class and settings).</summary>
    private async void OnEncodeClick(object sender, RoutedEventArgs e)
    {
        if (!BinkaEncoder.IsAvailable)
        {
            MessageBox.Show(string.Format(L("Pack_AudioNeedTool"), BinkaEncoder.ToolName, BinkaEncoder.ToolsDirectory), L("Pack_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            var tool = new OpenFileDialog { Filter = $"{BinkaEncoder.ToolName}|{BinkaEncoder.ToolName}|exe|*.exe", Title = BinkaEncoder.ToolName };
            if (tool.ShowDialog() != true) return;
            BinkaEncoder.Install(tool.FileName);
        }

        var dialog = new OpenFileDialog { Filter = "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.wma|All files|*.*" };
        if (dialog.ShowDialog() != true) return;

        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        var item = _item;
        var quality = QualityBox.SelectedIndex;
        ResultText.Text = L("Pack_AudioEncoding");
        var temp = Path.Combine(Path.GetTempPath(), "FloraPack", "snd", Guid.NewGuid().ToString("N"));
        try
        {
            var binka = await BinkaEncoder.EncodeAsync(dialog.FileName, quality);
            var replacement = await Task.Run(() =>
            {
                Directory.CreateDirectory(temp);
                var name = Path.GetFileName(item.DiskPath);
                foreach (var part in PackPaths.PackageParts)
                {
                    var sibling = Path.ChangeExtension(item.DiskPath, part);
                    if (File.Exists(sibling)) File.Copy(sibling, Path.Combine(temp, Path.ChangeExtension(name, part)), true);
                }
                return SoundWaveReplacer.Replace(Path.Combine(temp, name), binka, cue4.ResolvePackEngineVersion(), cue4.ResolvePackUsmapPath());
            });

            var origin = dialog.FileName;
            foreach (var (ext, data) in new[] { (".uasset", replacement.Uasset), (".uexp", replacement.Uexp), (".ubulk", replacement.Ubulk) })
            {
                var target = Path.ChangeExtension(item.PakPath, ext).Replace('\\', '/');
                _viewModel.ReplaceContent(_viewModel.Project.Find(target) ?? new PackItem { PakPath = target }, data, origin);
            }
            ResultText.Text = string.Format(L("Pack_AudioEncoded"), Path.GetFileName(origin), replacement.Seconds, replacement.Channels, replacement.SampleRate, replacement.Ubulk.Length / 1024);
            Load(item, _viewModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to encode sound {Path}", item.PakPath);
            ResultText.Text = string.Format(L("Pack_StatusFailed"), ex.Message);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp */ }
        }
    }

    /// <summary>Replaces the sound with another sound asset: its .uasset, .uexp and .ubulk are copied over this one (the path in the pak stays).</summary>
    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Sound asset (*.uasset)|*.uasset" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var count = 0;
            foreach (var part in PackPaths.PackageParts)
            {
                var source = Path.ChangeExtension(dialog.FileName, part);
                var target = Path.ChangeExtension(_item.PakPath, part).Replace('\\', '/');
                if (File.Exists(source))
                {
                    var existing = _viewModel.Project.Find(target) ?? new PackItem { PakPath = target };
                    _viewModel.ReplaceContent(existing, File.ReadAllBytes(source), source);
                    count++;
                }
                else if (part != ".uasset" && _viewModel.Project.Find(target) is { } stale)
                {
                    // a part the new sound does not have (a .ubulk) must not stay from the old one
                    _viewModel.Project.Remove(stale);
                }
            }
            _viewModel.RefreshSummary();
            ResultText.Text = string.Format(L("Pack_AudioReplaced"), Path.GetFileName(dialog.FileName), count);
            Load(_item, _viewModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to replace sound {Path}", _item.PakPath);
            ResultText.Text = string.Format(L("Pack_StatusFailed"), ex.Message);
        }
    }
}
