#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FModel.Framework.Packing.Locres;

public static class LocresResourceCodec
{
    private static readonly byte[] MagicGuid =
    [
        0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC,
        0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B
    ];

    public enum LocresVersion : byte
    {
        Legacy = 0,
        Compact = 1,
        Optimized = 2,
        OptimizedCityHash64Utf16 = 3
    }

    public static LocresPreviewDto Read(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var version = ReadVersion(reader);
        if (version > LocresVersion.OptimizedCityHash64Utf16)
            throw new InvalidOperationException($"Unsupported locres version: {(byte)version}");

        var entries = version == LocresVersion.Legacy
            ? ReadLegacy(reader)
            : ReadModern(reader, version);

        var namespaces = entries
            .Select(entry => entry.Namespace)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new LocresPreviewDto(
            VersionLabel(version),
            namespaces,
            entries.Count,
            entries.Select((entry, index) => entry with { Index = index }).ToArray());
    }

    public static byte[] Write(LocresPreviewDto locres)
    {
        var version = ParseVersion(locres.Version);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        if (version == LocresVersion.Legacy)
            WriteLegacy(writer, locres.Entries);
        else
            WriteModern(writer, version, locres.Entries);

        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] ApplyTranslations(byte[] originalData, IReadOnlyList<LocresEntryDto> translations)
    {
        var original = Read(originalData);
        var byIndex = translations.ToDictionary(entry => entry.Index);
        var updated = original.Entries
            .Select(entry => byIndex.TryGetValue(entry.Index, out var replacement)
                ? entry with { Text = replacement.Text ?? string.Empty }
                : entry)
            .ToArray();

        return Write(original with { Entries = updated });
    }

    private static LocresVersion ReadVersion(BinaryReader reader)
    {
        var start = reader.BaseStream.Position;
        var guid = reader.ReadBytes(MagicGuid.Length);
        if (guid.SequenceEqual(MagicGuid))
            return (LocresVersion)reader.ReadByte();

        reader.BaseStream.Position = start;
        return LocresVersion.Legacy;
    }

    private static IReadOnlyList<LocresEntryDto> ReadLegacy(BinaryReader reader)
    {
        var entries = new List<LocresEntryDto>();
        var namespaceCount = reader.ReadInt32();
        for (var i = 0; i < namespaceCount; i++)
        {
            var ns = ReadFString(reader);
            var keyCount = reader.ReadInt32();
            for (var k = 0; k < keyCount; k++)
            {
                var key = ReadFString(reader);
                var sourceHash = reader.ReadUInt32();
                var text = ReadFString(reader);
                entries.Add(new LocresEntryDto(entries.Count, ns, key, text, SourceHash: sourceHash));
            }
        }

        return entries;
    }

    private static IReadOnlyList<LocresEntryDto> ReadModern(BinaryReader reader, LocresVersion version)
    {
        var localizedStringOffset = reader.ReadInt64();
        var currentOffset = reader.BaseStream.Position;
        if (version >= LocresVersion.Optimized)
            _ = reader.ReadInt32();

        var strings = Array.Empty<string>();
        if (localizedStringOffset >= 0)
        {
            reader.BaseStream.Position = localizedStringOffset;
            var stringCount = reader.ReadInt32();
            strings = new string[stringCount];
            for (var i = 0; i < stringCount; i++)
            {
                strings[i] = ReadFString(reader);
                if (version >= LocresVersion.Optimized)
                    _ = reader.ReadInt32();
            }
        }

        reader.BaseStream.Position = currentOffset;
        if (version >= LocresVersion.Optimized)
            _ = reader.ReadInt32();

        var entries = new List<LocresEntryDto>();
        var namespaceCount = reader.ReadInt32();
        for (var i = 0; i < namespaceCount; i++)
        {
            var namespaceHash = version >= LocresVersion.Optimized ? reader.ReadUInt32() : 0;
            var ns = ReadFString(reader);
            var keyCount = reader.ReadUInt32();
            for (var k = 0; k < keyCount; k++)
            {
                var keyHash = version >= LocresVersion.Optimized ? reader.ReadUInt32() : 0;
                var key = ReadFString(reader);
                var sourceHash = reader.ReadUInt32();
                var stringIndex = reader.ReadInt32();
                var text = stringIndex >= 0 && stringIndex < strings.Length ? strings[stringIndex] : string.Empty;
                entries.Add(new LocresEntryDto(entries.Count, ns, key, text, namespaceHash, keyHash, sourceHash));
            }
        }

        return entries;
    }

    private static void WriteLegacy(BinaryWriter writer, IReadOnlyList<LocresEntryDto> entries)
    {
        var groups = entries.GroupBy(entry => entry.Namespace, StringComparer.Ordinal).ToArray();
        writer.Write(groups.Length);
        foreach (var group in groups)
        {
            WriteFString(writer, group.Key, forceUnicode: true);
            writer.Write(group.Count());
            foreach (var entry in group)
            {
                WriteFString(writer, entry.Key, forceUnicode: true);
                writer.Write(entry.SourceHash);
                WriteFString(writer, entry.Text, forceUnicode: true);
            }
        }
    }

    private static void WriteModern(BinaryWriter writer, LocresVersion version, IReadOnlyList<LocresEntryDto> entries)
    {
        writer.Write(MagicGuid);
        writer.Write((byte)version);
        var localizedStringOffsetPosition = writer.BaseStream.Position;
        writer.Write(0L);

        if (version >= LocresVersion.Optimized)
            writer.Write(entries.Count);

        var groups = entries.GroupBy(entry => entry.Namespace, StringComparer.Ordinal).ToArray();
        writer.Write(groups.Length);

        var stringIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var stringRefs = new List<LocresStringEntry>();

        foreach (var group in groups)
        {
            var first = group.First();
            if (version >= LocresVersion.Optimized)
                writer.Write(first.NamespaceHash);

            WriteFString(writer, group.Key);
            writer.Write((uint)group.Count());

            foreach (var entry in group)
            {
                if (version >= LocresVersion.Optimized)
                    writer.Write(entry.KeyHash);

                WriteFString(writer, entry.Key);
                writer.Write(entry.SourceHash);

                if (!stringIndices.TryGetValue(entry.Text, out var stringIndex))
                {
                    stringIndex = stringRefs.Count;
                    stringIndices[entry.Text] = stringIndex;
                    stringRefs.Add(new LocresStringEntry(entry.Text, 1));
                }
                else
                {
                    stringRefs[stringIndex] = stringRefs[stringIndex] with { RefCount = stringRefs[stringIndex].RefCount + 1 };
                }

                writer.Write(stringIndex);
            }
        }

        var localizedStringOffset = writer.BaseStream.Position;
        writer.Write(stringRefs.Count);
        foreach (var entry in stringRefs)
        {
            WriteFString(writer, entry.Text);
            if (version >= LocresVersion.Optimized)
                writer.Write(entry.RefCount);
        }

        var end = writer.BaseStream.Position;
        writer.BaseStream.Position = localizedStringOffsetPosition;
        writer.Write(localizedStringOffset);
        writer.BaseStream.Position = end;
    }

    private static string ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0)
            return string.Empty;

        if (length < 0)
        {
            var charCount = checked(-length);
            var bytes = reader.ReadBytes(charCount * 2);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        var asciiBytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(asciiBytes).TrimEnd('\0');
    }

    private static void WriteFString(BinaryWriter writer, string? value, bool forceUnicode = false)
    {
        value ??= string.Empty;
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        var withNull = value + '\0';
        if (!forceUnicode && IsAscii(withNull))
        {
            var bytes = Encoding.UTF8.GetBytes(withNull);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            return;
        }

        var unicodeBytes = Encoding.Unicode.GetBytes(withNull);
        writer.Write(-(unicodeBytes.Length / 2));
        writer.Write(unicodeBytes);
    }

    private static bool IsAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 0x7F)
                return false;
        }

        return true;
    }

    private static string VersionLabel(LocresVersion version)
    {
        return version switch
        {
            LocresVersion.Legacy => "Legacy",
            LocresVersion.Compact => "Compact",
            LocresVersion.Optimized => "Optimized",
            LocresVersion.OptimizedCityHash64Utf16 => "Optimized_CityHash64_UTF16",
            _ => ((byte)version).ToString()
        };
    }

    private static LocresVersion ParseVersion(string value)
    {
        return value switch
        {
            "Legacy" => LocresVersion.Legacy,
            "Compact" => LocresVersion.Compact,
            "Optimized" => LocresVersion.Optimized,
            "Optimized_CityHash64_UTF16" => LocresVersion.OptimizedCityHash64Utf16,
            _ when byte.TryParse(value, out var raw) => (LocresVersion)raw,
            _ => throw new InvalidOperationException($"Unsupported locres version: {value}")
        };
    }

    private sealed record LocresStringEntry(string Text, int RefCount);
}

public sealed record LocresPreviewDto(
    string Version,
    int NamespaceCount,
    int EntryCount,
    IReadOnlyList<LocresEntryDto> Entries);

public sealed record LocresEntryDto(
    int Index,
    string Namespace,
    string Key,
    string Text,
    uint NamespaceHash = 0,
    uint KeyHash = 0,
    uint SourceHash = 0);
