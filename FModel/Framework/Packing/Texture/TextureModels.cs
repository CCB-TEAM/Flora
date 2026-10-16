#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace FModel.Framework.Packing.Texture;

public sealed record TextureFormatInfo(string Name, int BlockWidth, int BlockHeight, int BlockBytes)
{
    public bool IsAstc => Name.StartsWith("PF_ASTC_", StringComparison.Ordinal);

    public bool IsDxt1 => string.Equals(Name, "PF_DXT1", StringComparison.OrdinalIgnoreCase);

    public bool IsDxt5 => string.Equals(Name, "PF_DXT5", StringComparison.OrdinalIgnoreCase);

    public bool IsBc7 => string.Equals(Name, "PF_BC7", StringComparison.OrdinalIgnoreCase);

    public bool IsBgra8 => string.Equals(Name, "PF_B8G8R8A8", StringComparison.OrdinalIgnoreCase);

    public bool IsRgba8 => string.Equals(Name, "PF_R8G8B8A8", StringComparison.OrdinalIgnoreCase);

    public bool IsArgb8 => string.Equals(Name, "PF_A8R8G8B8", StringComparison.OrdinalIgnoreCase);

    public bool IsUncompressed8BitColor => IsBgra8 || IsRgba8 || IsArgb8;

    public bool RequiresNativeBlockEncoder => IsAstc || IsDxt5 || IsBc7;

    public bool RequiresTexconv => IsDxt5 || IsBc7;

    public string AstcBlockSize
    {
        get
        {
            if (!IsAstc)
                throw new InvalidOperationException($"{Name} is not an ASTC format.");

            return Name["PF_ASTC_".Length..];
        }
    }

    public int GetMipByteSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Invalid mip dimensions {width}x{height}.");

        var blocksWide = Math.Max(1L, ((long)width + BlockWidth - 1) / BlockWidth);
        var blocksHigh = Math.Max(1L, ((long)height + BlockHeight - 1) / BlockHeight);
        var byteSize = checked(blocksWide * blocksHigh * BlockBytes);
        if (byteSize > int.MaxValue)
            throw new InvalidOperationException($"Mip {width}x{height} in {Name} is too large to encode.");

        return (int)byteSize;
    }
}

public sealed record TextureMip(int Index, int Width, int Height, int ByteLength, int StorageOffset = 0);

public enum TextureMipStorage
{
    Ubulk,
    UexpInline,
    UexpFooter
}

public sealed record TextureMipPlacement(
    int Index,
    int Width,
    int Height,
    int ByteLength,
    TextureMipStorage Storage,
    int Offset);

public sealed record TextureAssetInfo(
    string AssetPath,
    string UexpPath,
    string UbulkPath,
    byte[] ExportData,
    byte[] UexpFooter,
    byte[] UbulkData,
    TextureFormatInfo Format,
    int Width,
    int Height,
    IReadOnlyList<TextureMip> Mips,
    int ExternalMipCount,
    IReadOnlyList<TextureMip> InlineMips,
    IReadOnlyList<int> InlineMarkerOffsets,
    int InlineSentinelOffset,
    IReadOnlyList<TextureMipPlacement> MipPlacements);

public sealed record TextureInspectionResult(
    string AssetPath,
    string Format,
    int Width,
    int Height,
    int MipCount,
    int ExternalMipCount,
    int InlineMipCount,
    bool HasUbulk,
    IReadOnlyList<TextureMipPlacement> MipPlacements);

public sealed record TextureReplacementResult(
    string AssetPath,
    string UexpPath,
    string? UbulkPath,
    TextureInspectionResult Inspection);

public sealed record TextureCodecOptions(
    string AstcQuality = "medium",
    Action<string>? Log = null);
