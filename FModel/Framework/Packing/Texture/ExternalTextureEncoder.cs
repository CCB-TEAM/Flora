#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FModel.Framework.Packing.Texture;

internal sealed record ExternalEncoderOptions(string? AstcencPath, string? TexconvPath, string AstcQuality);

/// <summary>
/// Compresses ASTC / BC3 / BC7 mips by running the bundled astcenc and texconv executables.
/// Tools are searched in the "tools" folder next to the app first (AppContext.BaseDirectory\tools),
/// then on PATH.
/// </summary>
internal static class ExternalTextureEncoder
{
    /// <summary>Folder (relative to the app base directory) that holds astcenc-*.exe and texconv.exe.</summary>
    public const string ToolsFolderName = "tools";

    public static async Task<byte[][]> EncodeImageAsync(
        string imagePath,
        TextureFormatInfo format,
        int width,
        int height,
        IReadOnlyList<TextureMip> mips,
        ExternalEncoderOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        using Image<Rgba32> source = await TextureReplacementSource.LoadSourceImageAsync(imagePath, width, height, cancellationToken).ConfigureAwait(false);
        return await EncodeSourceAsync(source, format, mips, options, log, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[][]> EncodeSourceAsync(
        Image<Rgba32> source,
        TextureFormatInfo format,
        IReadOnlyList<TextureMip> mips,
        ExternalEncoderOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        byte[][] result = new byte[mips.Count][];
        string tempDirectory = Path.Combine(Path.GetTempPath(), "FModel-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            for (int i = 0; i < mips.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TextureMip mip = mips[i];
                string mipPngPath = Path.Combine(tempDirectory, $"mip{i}.png");

                using (Image<Rgba32> mipImage = TextureReplacementSource.CreateMipImage(source, mip))
                {
                    await mipImage.SaveAsPngAsync(mipPngPath, cancellationToken).ConfigureAwait(false);
                }

                log?.Invoke($"External encoding mip {i}: {mip.Width}x{mip.Height}, format={format.Name}");
                result[i] = format.IsAstc
                    ? await EncodeAstcMipAsync(mipPngPath, tempDirectory, i, format, options, log).ConfigureAwait(false)
                    : await EncodeTexconvMipAsync(mipPngPath, tempDirectory, i, format, options, log).ConfigureAwait(false);

                int expected = format.GetMipByteSize(mip.Width, mip.Height);
                if (result[i].Length != expected)
                {
                    throw new InvalidOperationException(
                        $"Encoder produced {result[i].Length} bytes for mip {i}, but {expected} bytes are required.");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup only; encoding output has already been validated.
            }
        }

        return result;
    }

    private static async Task<byte[]> EncodeAstcMipAsync(
        string mipPngPath,
        string tempDirectory,
        int mipIndex,
        TextureFormatInfo format,
        ExternalEncoderOptions options,
        Action<string>? log)
    {
        string encoder = ResolveTool(options.AstcencPath, GetAstcCandidates());
        string astcPath = Path.Combine(tempDirectory, $"mip{mipIndex}.astc");

        await RunAsync(encoder, ["-cl", mipPngPath, astcPath, format.AstcBlockSize, NormalizeAstcQuality(options.AstcQuality)], log).ConfigureAwait(false);

        byte[] file = await File.ReadAllBytesAsync(astcPath).ConfigureAwait(false);
        if (file.Length < 16 || file[0] != 0x13 || file[1] != 0xAB || file[2] != 0xA1 || file[3] != 0x5C)
        {
            throw new InvalidOperationException("astcenc output was not a valid .astc file.");
        }

        return file[16..];
    }

    private static async Task<byte[]> EncodeTexconvMipAsync(
        string mipPngPath,
        string tempDirectory,
        int mipIndex,
        TextureFormatInfo format,
        ExternalEncoderOptions options,
        Action<string>? log)
    {
        string encoder = ResolveTool(options.TexconvPath, "texconv");
        string outputDirectory = Path.Combine(tempDirectory, $"texconv{mipIndex}");
        Directory.CreateDirectory(outputDirectory);
        string ddsPath = Path.Combine(outputDirectory, $"mip{mipIndex}.dds");

        await RunAsync(encoder, ["-nologo", "-y", "-m", "1", "-f", GetTexconvFormat(format), "-o", outputDirectory, mipPngPath], log).ConfigureAwait(false);

        if (!File.Exists(ddsPath))
        {
            throw new InvalidOperationException("texconv did not produce the expected DDS output.");
        }

        return ExtractDdsPayload(await File.ReadAllBytesAsync(ddsPath).ConfigureAwait(false));
    }

    private static string GetTexconvFormat(TextureFormatInfo format)
    {
        return format.Name.ToUpperInvariant() switch
        {
            "PF_DXT5" => "BC3_UNORM",
            "PF_BC7" => "BC7_UNORM",
            _ => throw new InvalidOperationException($"Format {format.Name} is not supported by texconv integration.")
        };
    }

    private static byte[] ExtractDdsPayload(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            throw new InvalidOperationException("texconv output was not a valid DDS file.");
        }

        int dataOffset = 128;
        bool hasDx10Header = dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'1' && dds[87] == (byte)'0';
        if (hasDx10Header)
        {
            dataOffset += 20;
        }

        if (dds.Length <= dataOffset)
        {
            throw new InvalidOperationException("DDS file did not contain texture payload data.");
        }

        return dds[dataOffset..];
    }

    private static string[] GetAstcCandidates()
    {
        if (Avx2.IsSupported)
        {
            return new[] { "astcenc-avx2", "astcenc-sse4.1", "astcenc-sse2", "astcenc" };
        }

        if (Sse41.IsSupported)
        {
            return new[] { "astcenc-sse4.1", "astcenc-sse2", "astcenc-avx2", "astcenc" };
        }

        return new[] { "astcenc-sse2", "astcenc-sse4.1", "astcenc-avx2", "astcenc" };
    }

    private static string ResolveTool(string? configuredPath, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Configured encoder was not found.", fullPath);
            }

            return fullPath;
        }

        string toolsDirectory = Path.Combine(AppContext.BaseDirectory, ToolsFolderName);
        foreach (string candidate in candidates)
        {
            string local = Path.Combine(toolsDirectory, candidate + ".exe");
            if (File.Exists(local))
            {
                return local;
            }
        }

        foreach (string candidate in candidates)
        {
            if (IsToolOnPath(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find encoder tool. Expected one of {string.Join(", ", candidates)} in '{toolsDirectory}' or on PATH.");
    }

    private static bool IsToolOnPath(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeAstcQuality(string quality)
    {
        string normalized = quality.StartsWith("-", StringComparison.Ordinal) ? quality : "-" + quality;
        string[] allowed = ["-fastest", "-fast", "-medium", "-thorough", "-exhaustive"];
        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ASTC quality must be one of: fastest, fast, medium, thorough, exhaustive.");
        }

        return normalized.ToLowerInvariant();
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? log)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        log?.Invoke($"Running: {Path.GetFileName(executable)} {string.Join(' ', arguments)}");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start encoder '{executable}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Encoder '{executable}' failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{stderr}");
        }
    }
}
