#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FModel.Framework.Packing.Texture;

public static class Bc1Encoder
{
    public static byte[] Encode(Image<Rgba32> image)
    {
        var blocksWide = Math.Max(1, (image.Width + 3) / 4);
        var blocksHigh = Math.Max(1, (image.Height + 3) / 4);
        var output = new byte[blocksWide * blocksHigh * 8];
        Span<Rgba32> block = stackalloc Rgba32[16];
        var offset = 0;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                ReadBlock(image, bx * 4, by * 4, block);
                EncodeBlock(block, output.AsSpan(offset, 8));
                offset += 8;
            }
        }

        return output;
    }

    private static void ReadBlock(Image<Rgba32> image, int startX, int startY, Span<Rgba32> pixels)
    {
        var idx = 0;
        for (var y = 0; y < 4; y++)
        {
            var py = Math.Min(startY + y, image.Height - 1);
            for (var x = 0; x < 4; x++)
            {
                var px = Math.Min(startX + x, image.Width - 1);
                pixels[idx++] = image[px, py];
            }
        }
    }

    private static void EncodeBlock(ReadOnlySpan<Rgba32> pixels, Span<byte> output)
    {
        var useTransparency = false;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A < 128)
            {
                useTransparency = true;
                break;
            }
        }

        var (minColor, maxColor) = FindEndpoints(pixels);
        var c0 = ToRgb565(maxColor);
        var c1 = ToRgb565(minColor);

        if (useTransparency)
        {
            if (c0 > c1)
                (c0, c1) = (c1, c0);
        }
        else if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
        }

        Span<Rgba32> palette = stackalloc Rgba32[4];
        BuildPalette(c0, c1, useTransparency, palette);

        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var index = (uint)SelectIndex(pixels[i], palette, useTransparency);
            indices |= index << (i * 2);
        }

        BitConverter.TryWriteBytes(output[..2], c0);
        BitConverter.TryWriteBytes(output.Slice(2, 2), c1);
        BitConverter.TryWriteBytes(output.Slice(4, 4), indices);
    }

    private static (Rgba32 Min, Rgba32 Max) FindEndpoints(ReadOnlySpan<Rgba32> pixels)
    {
        var min = pixels[0];
        var max = pixels[0];
        var minLuma = Luma(min);
        var maxLuma = minLuma;

        for (var i = 1; i < pixels.Length; i++)
        {
            var luma = Luma(pixels[i]);
            if (luma < minLuma)
            {
                minLuma = luma;
                min = pixels[i];
            }

            if (luma > maxLuma)
            {
                maxLuma = luma;
                max = pixels[i];
            }
        }

        return (min, max);
    }

    private static int SelectIndex(Rgba32 pixel, ReadOnlySpan<Rgba32> palette, bool useTransparency)
    {
        if (useTransparency && pixel.A < 128)
            return 3;

        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        var maxPalette = useTransparency ? 3 : 4;
        for (var i = 0; i < maxPalette; i++)
        {
            var distance = ColorDistance(pixel, palette[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static void BuildPalette(ushort c0, ushort c1, bool useTransparency, Span<Rgba32> palette)
    {
        palette[0] = FromRgb565(c0);
        palette[1] = FromRgb565(c1);

        if (useTransparency)
        {
            palette[2] = Interpolate(palette[0], palette[1], 1, 1);
            palette[3] = new Rgba32(0, 0, 0, 0);
        }
        else
        {
            palette[2] = Interpolate(palette[0], palette[1], 2, 1);
            palette[3] = Interpolate(palette[0], palette[1], 1, 2);
        }
    }

    private static Rgba32 Interpolate(Rgba32 a, Rgba32 b, int aWeight, int bWeight)
    {
        var total = aWeight + bWeight;
        return new Rgba32(
            (byte)((a.R * aWeight + b.R * bWeight) / total),
            (byte)((a.G * aWeight + b.G * bWeight) / total),
            (byte)((a.B * aWeight + b.B * bWeight) / total),
            (byte)((a.A * aWeight + b.A * bWeight) / total));
    }

    private static ushort ToRgb565(Rgba32 color)
    {
        var r = color.R >> 3;
        var g = color.G >> 2;
        var b = color.B >> 3;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Rgba32 FromRgb565(ushort value)
    {
        var r = (byte)(((value >> 11) & 0x1F) * 255 / 31);
        var g = (byte)(((value >> 5) & 0x3F) * 255 / 63);
        var b = (byte)((value & 0x1F) * 255 / 31);
        return new Rgba32(r, g, b, 255);
    }

    private static int ColorDistance(Rgba32 a, Rgba32 b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        var da = a.A - b.A;
        return dr * dr + dg * dg + db * db + da * da;
    }

    private static int Luma(Rgba32 color)
    {
        return color.R * 299 + color.G * 587 + color.B * 114;
    }
}
