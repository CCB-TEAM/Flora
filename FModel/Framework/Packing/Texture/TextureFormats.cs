#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace FModel.Framework.Packing.Texture;

public static class TextureFormats
{
    private static readonly TextureFormatInfo[] KnownFormats =
    [
        new("PF_DXT1", 4, 4, 8),
        new("PF_DXT5", 4, 4, 16),
        new("PF_BC7", 4, 4, 16),
        new("PF_B8G8R8A8", 1, 1, 4),
        new("PF_R8G8B8A8", 1, 1, 4),
        new("PF_A8R8G8B8", 1, 1, 4),
        new("PF_ASTC_4x4", 4, 4, 16),
        new("PF_ASTC_5x4", 5, 4, 16),
        new("PF_ASTC_5x5", 5, 5, 16),
        new("PF_ASTC_6x5", 6, 5, 16),
        new("PF_ASTC_6x6", 6, 6, 16),
        new("PF_ASTC_8x5", 8, 5, 16),
        new("PF_ASTC_8x6", 8, 6, 16),
        new("PF_ASTC_8x8", 8, 8, 16),
        new("PF_ASTC_10x5", 10, 5, 16),
        new("PF_ASTC_10x6", 10, 6, 16),
        new("PF_ASTC_10x8", 10, 8, 16),
        new("PF_ASTC_10x10", 10, 10, 16),
        new("PF_ASTC_12x10", 12, 10, 16),
        new("PF_ASTC_12x12", 12, 12, 16),
    ];

    public static IReadOnlyList<TextureFormatInfo> SupportedFormats => KnownFormats;

    public static TextureFormatInfo DetectFormat(byte[] exportData, IEnumerable<string> nameMap, string? formatHint = null)
    {
        var hintMatch = MatchKnownFormat(formatHint);
        if (hintMatch is not null)
            return hintMatch;

        var ascii = Encoding.ASCII.GetString(exportData);
        foreach (var format in KnownFormats)
        {
            if (ascii.Contains(format.Name, StringComparison.Ordinal))
                return format;
        }

        foreach (var name in nameMap)
        {
            var match = MatchKnownFormat(name);
            if (match is not null)
                return match;
        }

        throw new InvalidOperationException("Could not detect a supported pixel format from the asset.");
    }

    private static TextureFormatInfo? MatchKnownFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return KnownFormats.FirstOrDefault(format =>
            string.Equals(format.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(format.Name, StringComparison.OrdinalIgnoreCase));
    }
}
