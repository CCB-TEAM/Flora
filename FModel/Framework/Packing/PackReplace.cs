using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FModel.Framework.Packing.Audio;
using FModel.Framework.Packing.Texture;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Framework.Packing;

/// <summary>
/// Replaces the content of package files of the pak (textures, sounds) while keeping the assets themselves.
/// Used by the workspace editors and the agent tools.
/// </summary>
public static class PackReplace
{
    /// <summary>Whether the file is a texture asset (a .uasset of Texture2D with its .uexp next to it).</summary>
    public static bool IsTexture(PackItem item)
    {
        if (item.Extension != "uasset" || !File.Exists(Path.ChangeExtension(item.DiskPath, ".uexp")))
            return false;
        try
        {
            using var stream = File.OpenRead(item.DiskPath);
            var buffer = new byte[(int) Math.Min(stream.Length, 262144)];
            stream.ReadExactly(buffer);
            return System.Text.Encoding.ASCII.GetString(buffer).Contains("Texture2D", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the file is a sound wave asset.</summary>
    public static bool IsSound(PackItem item)
    {
        if (item.Extension != "uasset")
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

    public static Task<TextureInspectionResult> InspectTextureAsync(PackItem item)
    {
        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        return TextureReplaceService.InspectAsync(item.DiskPath, cue4.ResolvePackEngineVersion(), cue4.ResolvePackUsmapPath());
    }

    /// <summary>Encodes the image into the texture's format and size (mips included) and puts the result into the pak.</summary>
    public static async Task<TextureInspectionResult> ReplaceTextureAsync(PackWorkspaceViewModel viewModel, PackItem item, string imagePath)
    {
        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        var temp = NewTemp("tex");
        try
        {
            var (written, inspection) = await Task.Run(async () =>
            {
                var input = Path.Combine(temp, "in");
                var output = Path.Combine(temp, "out");
                Directory.CreateDirectory(input);
                Directory.CreateDirectory(output);
                var name = CopyPackage(item, input);
                var result = await TextureReplaceService.ReplaceAsync(Path.Combine(input, name), imagePath, Path.Combine(output, name),
                    cue4.ResolvePackEngineVersion(), cue4.ResolvePackUsmapPath());
                var files = new[] { result.AssetPath, result.UexpPath, result.UbulkPath }.Where(o => o != null && File.Exists(o)).Select(o => o!)
                    .Select(o => (Ext: Path.GetExtension(o), Data: File.ReadAllBytes(o))).ToList();
                return (files, result.Inspection);
            });

            foreach (var (ext, data) in written)
                viewModel.ReplaceContent(item.PakPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? item : Sibling(viewModel, item, ext), data, imagePath);
            return inspection;
        }
        finally
        {
            DeleteTemp(temp);
        }
    }

    /// <summary>Encodes an audio file into Bink Audio and puts it into the sound, keeping the asset (its path, class and settings).</summary>
    public static async Task<SoundWaveReplacement> EncodeSoundAsync(PackWorkspaceViewModel viewModel, PackItem item, string audioFile, int quality)
    {
        if (!BinkaEncoder.IsAvailable)
            throw new InvalidOperationException($"{BinkaEncoder.ToolName} is missing from {BinkaEncoder.ToolsDirectory}.");

        var cue4 = ApplicationService.ApplicationView.CUE4Parse;
        var temp = NewTemp("snd");
        try
        {
            var binka = await BinkaEncoder.EncodeAsync(audioFile, quality);
            var replacement = await Task.Run(() =>
            {
                Directory.CreateDirectory(temp);
                var name = CopyPackage(item, temp);
                return SoundWaveReplacer.Replace(Path.Combine(temp, name), binka, cue4.ResolvePackEngineVersion(), cue4.ResolvePackUsmapPath());
            });

            foreach (var (ext, data) in new[] { (".uasset", replacement.Uasset), (".uexp", replacement.Uexp), (".ubulk", replacement.Ubulk) })
                viewModel.ReplaceContent(Sibling(viewModel, item, ext), data, audioFile);
            return replacement;
        }
        finally
        {
            DeleteTemp(temp);
        }
    }

    /// <summary>Copies another sound asset's .uasset/.uexp/.ubulk over the sound (the path in the pak stays). Returns the parts copied.</summary>
    public static int ReplaceSoundAsset(PackWorkspaceViewModel viewModel, PackItem item, string soundAsset)
    {
        var count = 0;
        foreach (var part in PackPaths.PackageParts)
        {
            var source = Path.ChangeExtension(soundAsset, part);
            var target = Path.ChangeExtension(item.PakPath, part).Replace('\\', '/');
            if (File.Exists(source))
            {
                viewModel.ReplaceContent(viewModel.Project.Find(target) ?? new PackItem { PakPath = target }, File.ReadAllBytes(source), source);
                count++;
            }
            else if (part != ".uasset" && viewModel.Project.Find(target) is { } stale)
            {
                // a part the new sound does not have (a .ubulk) must not stay from the old one
                viewModel.Project.Remove(stale);
            }
        }
        viewModel.RefreshSummary();
        return count;
    }

    /// <summary>The item of another part of the package, or a stand-in when the part is new (a .ubulk).</summary>
    private static PackItem Sibling(PackWorkspaceViewModel viewModel, PackItem item, string extension)
    {
        var path = Path.ChangeExtension(item.PakPath, extension).Replace('\\', '/');
        return viewModel.Project.Find(path) ?? new PackItem { PakPath = path };
    }

    /// <summary>Copies the package parts of the item into a folder; returns the file name of the item there.</summary>
    private static string CopyPackage(PackItem item, string folder)
    {
        var name = Path.GetFileName(item.DiskPath);
        foreach (var part in PackPaths.PackageParts)
        {
            var sibling = Path.ChangeExtension(item.DiskPath, part);
            if (File.Exists(sibling)) File.Copy(sibling, Path.Combine(folder, Path.ChangeExtension(name, part)), true);
        }
        return name;
    }

    private static string NewTemp(string kind) => Path.Combine(Path.GetTempPath(), "FloraPack", kind, Guid.NewGuid().ToString("N"));

    private static void DeleteTemp(string temp)
    {
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        catch { /* temp */ }
    }
}
