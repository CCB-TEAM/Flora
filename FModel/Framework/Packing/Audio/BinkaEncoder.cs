#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSCore;
using CSCore.Codecs;
using CSCore.Codecs.WAV;

namespace FModel.Framework.Packing.Audio;

/// <summary>
/// Encodes audio into Bink Audio (.binka, the ABEU container Unreal uses) with the external binka_encode.exe
/// (https://github.com/595554963github/binkaenc, built by the user and put into the tools folder; it is not shipped with Flora).
/// </summary>
public static class BinkaEncoder
{
    public const string ToolName = "binka_encode.exe";

    /// <summary>Where the tool is looked for: the tools folder next to the program.</summary>
    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");

    public static string ToolPath => Path.Combine(ToolsDirectory, ToolName);

    public static bool IsAvailable => File.Exists(ToolPath);

    /// <summary>Copies the tool the user picked into the tools folder.</summary>
    public static void Install(string toolFile)
    {
        Directory.CreateDirectory(ToolsDirectory);
        File.Copy(toolFile, ToolPath, true);
    }

    /// <summary>
    /// Converts any audio file Flora can decode (wav, mp3, ogg, flac...) to 16-bit PCM WAV and encodes it.
    /// Returns the .binka file (ABEU header, seek table and blocks).
    /// </summary>
    public static async Task<byte[]> EncodeAsync(string audioFile, int quality, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new FileNotFoundException($"{ToolName} was not found in {ToolsDirectory}.", ToolPath);

        var temp = Path.Combine(Path.GetTempPath(), "FloraPack", "binka", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var wav = Path.Combine(temp, "input.wav");
            await Task.Run(() => ConvertToWav16(audioFile, wav), cancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(ToolPath)
            {
                WorkingDirectory = temp,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(wav);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("0");
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add(Math.Clamp(quality, 0, 9).ToString());

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{ToolName} could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{ToolName} failed ({process.ExitCode}): {(await error).Trim()} {(await output).Trim()}".Trim());

            var result = Path.ChangeExtension(wav, ".binka");
            if (!File.Exists(result))
                throw new FileNotFoundException($"{ToolName} did not write {Path.GetFileName(result)}.", result);
            return await File.ReadAllBytesAsync(result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp */ }
        }
    }

    private static void ConvertToWav16(string input, string output)
    {
        using var codec = CodecFactory.Instance.GetCodec(input);
        using var pcm16 = codec.ToSampleSource().ToWaveSource(16);
        using var writer = new WaveWriter(output, pcm16.WaveFormat);
        var buffer = new byte[pcm16.WaveFormat.BytesPerSecond];
        int read;
        while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
            writer.Write(buffer, 0, read);
    }
}
