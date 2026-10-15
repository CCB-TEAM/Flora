using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace FModel.Framework.Mappings;

/// <summary>
/// Serializes <see cref="TypeMappings"/> into an uncompressed .usmap (version ExplicitEnumValues),
/// readable by both CUE4Parse's UsmapParser and UAssetAPI's Usmap.
/// Used to feed UAssetAPI when mappings were loaded from a .jmap.
/// </summary>
public static class UsmapWriter
{
    private const ushort UsmapMagic = 0x30C4;

    public static void Write(TypeMappings mappings, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, Serialize(mappings));
        File.Move(tempPath, path, true);
    }

    public static byte[] Serialize(TypeMappings mappings)
    {
        var names = new NameTable();
        var enums = mappings.Enums.Where(e => !string.IsNullOrEmpty(e.Key)).ToList();
        var structs = mappings.Types.Values.Where(s => !string.IsNullOrEmpty(s.Name)).ToList();

        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, true))
        {
            // names are referenced by index, so enums and structs are serialized first and the name table is written before them
            using var payload = new MemoryStream();
            using (var pw = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                pw.Write(enums.Count);
                foreach (var (enumName, values) in enums)
                {
                    pw.Write(names.Get(enumName));
                    var entries = values.Take(ushort.MaxValue >> 1).ToList();
                    pw.Write((ushort) entries.Count);
                    foreach (var (value, name) in entries)
                    {
                        pw.Write(value);
                        pw.Write(names.Get(name));
                    }
                }

                pw.Write(structs.Count);
                foreach (var s in structs)
                    WriteStruct(pw, s, names);
            }

            w.Write(names.Names.Count);
            foreach (var name in names.Names)
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                w.Write((ushort) bytes.Length);
                w.Write(bytes);
            }
            w.Write(payload.ToArray());
        }

        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, Encoding.UTF8, true))
        {
            w.Write(UsmapMagic);
            w.Write((byte) EUsmapVersion.ExplicitEnumValues);
            w.Write(0); // no package versioning
            w.Write((byte) EUsmapCompressionMethod.None);
            w.Write((uint) body.Length);
            w.Write((uint) body.Length);
            w.Write(body.ToArray());
        }
        return file.ToArray();
    }

    private static void WriteStruct(BinaryWriter w, Struct s, NameTable names)
    {
        // array clones (Index > 0) are re-expanded by readers from ArraySize
        var serializable = s.Properties
            .Where(p => p.Value.Index == 0 && !string.IsNullOrEmpty(p.Value.Name))
            .OrderBy(p => p.Key)
            .ToList();

        w.Write(names.Get(s.Name));
        w.Write(string.IsNullOrEmpty(s.SuperType) ? -1 : names.Get(s.SuperType));
        w.Write((ushort) Math.Max(s.PropertyCount, s.Properties.Count));
        w.Write((ushort) serializable.Count);
        foreach (var (schemaIndex, info) in serializable)
        {
            w.Write((ushort) schemaIndex);
            w.Write((byte) Math.Clamp(info.ArraySize ?? 1, 1, byte.MaxValue));
            w.Write(names.Get(info.Name));
            WritePropertyType(w, info.MappingType, names);
        }
    }

    private static void WritePropertyType(BinaryWriter w, PropertyType type, NameTable names)
    {
        var typeEnum = ToUsmapType(type.Type);

        // TEnumAsByte: jmap stores a ByteProperty with an enum, usmap (Dumper-7 convention) an EnumProperty over a byte,
        // which keeps enum names in UAssetAPI output instead of raw numbers
        if (typeEnum == EPropertyType.ByteProperty && !string.IsNullOrEmpty(type.EnumName))
        {
            w.Write((byte) EPropertyType.EnumProperty);
            w.Write((byte) EPropertyType.ByteProperty);
            w.Write(names.Get(type.EnumName));
            return;
        }

        w.Write((byte) typeEnum);
        switch (typeEnum)
        {
            case EPropertyType.EnumProperty:
                WritePropertyType(w, type.InnerType ?? new PropertyType(nameof(EPropertyType.ByteProperty)), names);
                w.Write(string.IsNullOrEmpty(type.EnumName) ? -1 : names.Get(type.EnumName));
                break;
            case EPropertyType.StructProperty:
                w.Write(string.IsNullOrEmpty(type.StructType) ? -1 : names.Get(type.StructType));
                break;
            case EPropertyType.SetProperty:
            case EPropertyType.ArrayProperty:
            case EPropertyType.OptionalProperty:
                WritePropertyType(w, type.InnerType ?? new PropertyType(nameof(EPropertyType.Unknown)), names);
                break;
            case EPropertyType.MapProperty:
                WritePropertyType(w, type.InnerType ?? new PropertyType(nameof(EPropertyType.Unknown)), names);
                WritePropertyType(w, type.ValueType ?? new PropertyType(nameof(EPropertyType.Unknown)), names);
                break;
        }
    }

    /// <summary>
    /// Maps a property type name to the subset of <see cref="EPropertyType"/> that standard usmap readers (UAssetAPI) understand.
    /// </summary>
    private static EPropertyType ToUsmapType(string type)
    {
        if (!Enum.TryParse(type, out EPropertyType typeEnum))
        {
            return type switch
            {
                _ when type.EndsWith("DelegateProperty", StringComparison.Ordinal) => EPropertyType.MulticastDelegateProperty,
                _ => EPropertyType.Unknown
            };
        }

        return typeEnum switch
        {
            EPropertyType.ClassProperty => EPropertyType.ObjectProperty,
            EPropertyType.SoftClassProperty => EPropertyType.SoftObjectProperty,
            EPropertyType.MulticastInlineDelegateProperty => EPropertyType.MulticastDelegateProperty,
            > EPropertyType.AnsiStrProperty and < EPropertyType.Unknown => EPropertyType.Unknown,
            _ => typeEnum
        };
    }

    private sealed class NameTable
    {
        public readonly List<string> Names = [];
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);

        public int Get(string name)
        {
            if (_indices.TryGetValue(name, out var index))
                return index;

            index = Names.Count;
            Names.Add(name);
            _indices[name] = index;
            return index;
        }
    }
}
