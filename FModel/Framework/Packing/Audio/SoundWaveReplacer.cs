#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace FModel.Framework.Packing.Audio;

public sealed record SoundWaveReplacement(byte[] Uasset, byte[] Uexp, byte[] Ubulk, int Channels, int SampleRate, int Samples, double Seconds);

/// <summary>
/// Puts new Bink Audio into a cooked streaming SoundWave: the 28 byte ABEU header becomes the inline chunk and the rest
/// (a "SEEK" table plus the blocks) the one chunk of the .ubulk, the way Unreal cooks short sounds.
/// </summary>
public static class SoundWaveReplacer
{
    /// <summary>The largest chunk Unreal streams.</summary>
    public const int MaxChunkBytes = 262144;

    private const int HeaderSize = 28;
    private const int ExtrasPrefix = 36; // flags, cue point count, guid(16), NumChunks, then the AudioFormat name (8): what follows the properties

    public static SoundWaveReplacement Replace(string uassetPath, byte[] binka, EngineVersion engineVersion, string? usmapPath)
    {
        // --- the encoder's file: header, seek table, blocks
        if (binka.Length < HeaderSize + 4 || binka[0] != 'A' || binka[1] != 'B' || binka[2] != 'E' || binka[3] != 'U')
            throw new InvalidDataException("The encoded file is not an ABEU Bink Audio file.");
        int channels = binka[5];
        var rate = BitConverter.ToInt32(binka, 8);
        var samples = BitConverter.ToInt32(binka, 12);
        var seekEntries = BitConverter.ToUInt16(binka, 24);
        var blocksStart = HeaderSize + seekEntries * 2;
        if (blocksStart >= binka.Length)
            throw new InvalidDataException("The encoded file has no audio blocks.");

        // --- streamed layout: header chunk (no seek table) and one data chunk
        var header = binka[..HeaderSize];
        BitConverter.GetBytes((ushort) 0).CopyTo(header, 24);
        var chunk = new byte[16 + seekEntries * 2 + (binka.Length - blocksStart)];
        chunk[0] = (byte) 'S'; chunk[1] = (byte) 'E'; chunk[2] = (byte) 'E'; chunk[3] = (byte) 'K';
        BitConverter.GetBytes((uint) Math.Round(rate * 10.24)).CopyTo(chunk, 4);  // frames Unreal puts in a streaming chunk (10.24 s)
        BitConverter.GetBytes(seekEntries).CopyTo(chunk, 4 + 4 + 3);
        Buffer.BlockCopy(binka, HeaderSize, chunk, 16, seekEntries * 2);
        Buffer.BlockCopy(binka, blocksStart, chunk, 16 + seekEntries * 2, binka.Length - blocksStart);
        if (chunk.Length > MaxChunkBytes)
            throw new InvalidOperationException($"The encoded sound is {chunk.Length / 1024} KB; sounds that need more than one streaming chunk ({MaxChunkBytes / 1024} KB) are not supported yet. Use a shorter sound or a lower quality.");
        // the size Unreal writes into the header: everything but the 15 bytes of the SEEK header
        BitConverter.GetBytes((uint) (HeaderSize + chunk.Length - 15)).CopyTo(header, 20);

        // --- the asset
        var mappings = string.IsNullOrWhiteSpace(usmapPath) ? null : new Usmap(usmapPath);
        var asset = new UAsset(Path.GetFullPath(uassetPath), engineVersion, mappings);
        var export = asset.Exports.OfType<NormalExport>().FirstOrDefault(o => o.Extras is { Length: > ExtrasPrefix } && o.Data.Any(p => p.Name.ToString() == "SampleRate"))
                     ?? throw new InvalidOperationException("No SoundWave export was found.");
        var resources = asset.DataResources ?? throw new InvalidOperationException("The sound has no data resources (UE 5.3+ streaming layout expected).");

        var extras = export.Extras!;
        var flagsOfWave = BitConverter.ToUInt32(extras, 0);
        if ((flagsOfWave & 1) == 0)
            throw new InvalidOperationException("The sound is not cooked.");
        if (BitConverter.ToInt32(extras, 4) != 0)
            throw new InvalidOperationException("Sounds with cue points are not supported yet.");
        var numChunks = BitConverter.ToInt32(extras, 24);
        if (numChunks < 2)
            throw new InvalidOperationException("Only streamed sounds with a header chunk and data chunks can be replaced.");

        // read the original chunk records to find their data resources and check the whole tail is understood
        var position = ExtrasPrefix;
        var records = new List<(uint Flags, int Index)>();
        for (var i = 0; i < numChunks; i++)
        {
            var chunkFlags = BitConverter.ToUInt32(extras, position);
            var index = BitConverter.ToInt32(extras, position + 4);
            position += 8;
            if ((chunkFlags & 4) != 0)
                position += checked((int) resources[index].SerialSize);
            position += 8 + ((chunkFlags & 2) != 0 ? 4 : 0);
            records.Add((chunkFlags, index));
        }
        if (position != extras.Length)
            throw new InvalidOperationException("The SoundWave data has a layout this tool does not know.");
        if ((records[0].Flags & 4) == 0 || (records[1].Flags & 4) != 0)
            throw new InvalidOperationException("The SoundWave chunks are laid out differently from the streaming layout this tool writes.");

        // --- the new tail
        using (var tail = new MemoryStream())
        using (var writer = new BinaryWriter(tail))
        {
            writer.Write(extras, 0, 8);
            writer.Write(Guid.NewGuid().ToByteArray());                       // CompressedDataGuid: a new sound
            writer.Write(2);                                                  // NumChunks
            writer.Write(extras, 28, 8);                                      // AudioFormat (BINKA)
            writer.Write(records[0].Flags); writer.Write(records[0].Index);   // inline header chunk
            writer.Write(header);
            writer.Write(HeaderSize); writer.Write(HeaderSize);
            if ((records[0].Flags & 2) != 0) writer.Write(0u);
            writer.Write(records[1].Flags | 2u); writer.Write(records[1].Index); // data chunk with a seek offset
            writer.Write(chunk.Length); writer.Write(chunk.Length); writer.Write(0u);
            export.Extras = tail.ToArray();
        }

        // data resources of the two chunks; those of further chunks of a longer sound go away
        var headerResource = resources[records[0].Index];
        headerResource.SerialSize = headerResource.RawSize = HeaderSize;
        var dataResource = resources[records[1].Index];
        dataResource.SerialSize = dataResource.RawSize = chunk.Length;
        resources[records[0].Index] = headerResource;
        resources[records[1].Index] = dataResource;
        foreach (var index in records.Skip(2).Select(o => o.Index).OrderByDescending(o => o))
            if (index >= 0 && index < resources.Count && index > records[1].Index)
                resources.RemoveAt(index);

        // properties
        var seconds = (double) samples / rate;
        void SetInt(string name, int value)
        {
            if (export.Data.FirstOrDefault(p => p.Name.ToString() == name) is IntPropertyData p) p.Value = value;
        }
        void SetFloat(string name, float value)
        {
            if (export.Data.FirstOrDefault(p => p.Name.ToString() == name) is FloatPropertyData p) p.Value = value;
        }
        SetInt("NumChannels", channels);
        SetInt("SampleRate", rate);
        SetFloat("Duration", (float) seconds);
        SetFloat("TotalSamples", samples);

        var temp = Path.Combine(Path.GetTempPath(), "FloraPack", "sound", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var written = Path.Combine(temp, Path.GetFileName(uassetPath));
            asset.Write(written);
            return new SoundWaveReplacement(File.ReadAllBytes(written), File.ReadAllBytes(Path.ChangeExtension(written, ".uexp")), chunk, channels, rate, samples, seconds);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp */ }
        }
    }
}
