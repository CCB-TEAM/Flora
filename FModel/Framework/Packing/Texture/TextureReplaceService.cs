#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.Packing.Texture;

/// <summary>
/// In-process texture replacement entry points (no external UAssetCLI process).
/// </summary>
public static class TextureReplaceService
{
    public static Task<TextureInspectionResult> InspectAsync(
        string uassetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var info = TextureAssetParser.Load(uassetPath, engineVersion, usmapPath);
        return Task.FromResult(ToInspection(info));
    }

    public static async Task<TextureReplacementResult> ReplaceAsync(
        string uassetPath,
        string sourceImagePath,
        string outputUassetPath,
        EngineVersion engineVersion,
        string? usmapPath,
        TextureCodecOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TextureCodecOptions();
        var info = TextureAssetParser.Load(uassetPath, engineVersion, usmapPath);
        var mipPayloads = await TextureReplacementSource.LoadAndEncodeImageAsync(
            sourceImagePath,
            info.Format,
            info.Width,
            info.Height,
            info.Mips,
            options,
            ct).ConfigureAwait(false);

        TextureReplacer.WriteReplacement(info, mipPayloads, outputUassetPath);
        var ubulkPath = Path.ChangeExtension(outputUassetPath, ".ubulk");
        return new TextureReplacementResult(
            Path.GetFullPath(outputUassetPath),
            Path.ChangeExtension(Path.GetFullPath(outputUassetPath), ".uexp"),
            File.Exists(ubulkPath) ? ubulkPath : null,
            ToInspection(info));
    }

    private static TextureInspectionResult ToInspection(TextureAssetInfo info)
    {
        return new TextureInspectionResult(
            info.AssetPath,
            info.Format.Name,
            info.Width,
            info.Height,
            info.Mips.Count,
            info.ExternalMipCount,
            info.InlineMips.Count,
            File.Exists(info.UbulkPath),
            info.MipPlacements);
    }
}
