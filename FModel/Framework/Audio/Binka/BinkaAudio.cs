using System;
using System.IO;
using System.Text;

namespace FModel.Framework.Audio.Binka;

/// <summary>
/// Converts Bink Audio (Unreal "ABEU"/UEBA and legacy "1FCB"/BCF1 containers) to 16-bit PCM WAV in process.
/// </summary>
public static class BinkaAudio
{
    public static bool IsBinka(byte[] data) =>
        data is { Length: >= 4 } &&
        ((data[0] == 'A' && data[1] == 'B' && data[2] == 'E' && data[3] == 'U') ||
         (data[0] == '1' && data[1] == 'F' && data[2] == 'C' && data[3] == 'B'));

    public static bool TryDecodeToWav(byte[] data, out byte[] wav, out string error)
    {
        wav = null;
        error = null;
        if (!IsBinka(data))
        {
            error = "not a Bink Audio file (ABEU/1FCB)";
            return false;
        }

        try
        {
            int channels, sampleRate, numSamples;
            uint dataOffset;
            float[] samples;
            if (data[0] == 'A')
            {
                if (UebaDecoder.ParseHeader(data, out _, out channels, out sampleRate, out numSamples, out dataOffset, (uint) data.Length) < 0)
                {
                    error = "unsupported UEBA header";
                    return false;
                }
                var decoder = new UebaDecoder(sampleRate, channels);
                if (!decoder.IsValid)
                {
                    error = $"unsupported stream ({channels} channels, {sampleRate} Hz)";
                    return false;
                }
                samples = decoder.DecodeAll(data, dataOffset, numSamples);
            }
            else
            {
                if (Bcf1Decoder.ParseHeader(data, out _, out channels, out sampleRate, out numSamples, out dataOffset) < 0)
                {
                    error = "unsupported BCF1 header";
                    return false;
                }
                var decoder = new Bcf1Decoder(sampleRate, channels);
                if (!decoder.IsValid)
                {
                    error = $"unsupported stream ({channels} channels, {sampleRate} Hz)";
                    return false;
                }
                samples = decoder.DecodeAll(data, dataOffset, numSamples);
            }

            if (samples == null || samples.Length == 0)
            {
                error = "no audio packets could be decoded";
                return false;
            }

            wav = WriteWav(samples, channels, sampleRate);
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    private static byte[] WriteWav(float[] samples, int channels, int sampleRate)
    {
        var dataSize = samples.Length * 2;
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short) 1); // PCM
        writer.Write((short) channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short) (channels * 2));
        writer.Write((short) 16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
            writer.Write((short) Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f));
        writer.Flush();
        return stream.ToArray();
    }
}
