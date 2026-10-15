using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.JSON;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace FModel.Framework.BlueprintEditing;

/// <summary>
/// One property a class default object can hold: either overridden in the CDO (<see cref="Current"/> is set)
/// or inherited from the parent class / native default.
/// </summary>
public sealed class CdoProperty
{
    public string Name { get; init; } = "";
    public int ArrayIndex { get; init; }
    public string Owner { get; init; } = "";
    public string TypeText { get; init; } = "";
    public EPropertyType Type { get; init; } = EPropertyType.Unknown;
    public UsmapPropertyData? Mapping { get; init; }
    public PropertyData? Current { get; internal set; }

    public bool IsOverridden => Current != null;
    public bool IsSimple => CdoEditor.IsSimpleType(Type);
    public string DisplayName => ArrayIndex > 0 ? $"{Name}[{ArrayIndex}]" : Name;
}

/// <summary>
/// Reads and edits the class default object of a blueprint loaded with UAssetAPI.
/// Scalars, enums, names and strings are edited as text; everything else as UAssetAPI property JSON.
/// </summary>
public sealed class CdoEditor
{
    public UAsset Asset { get; }
    public NormalExport Cdo { get; }
    public string ClassName { get; }

    private CdoEditor(UAsset asset, NormalExport cdo)
    {
        Asset = asset;
        Cdo = cdo;
        cdo.Data ??= [];
        ClassName = cdo.GetClassTypeForAncestry(asset, out _)?.ToString() ?? "";
    }

    public static CdoEditor? TryCreate(UAsset asset)
    {
        NormalExport? cdo = null;
        if (asset.Exports.OfType<ClassExport>().FirstOrDefault() is { ClassDefaultObject: { } index } && index.IsExport())
            cdo = index.ToExport(asset) as NormalExport;
        cdo ??= asset.Exports.OfType<NormalExport>().FirstOrDefault(o => o.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject));
        return cdo == null ? null : new CdoEditor(asset, cdo);
    }

    public static bool IsSimpleType(EPropertyType type) => type is EPropertyType.BoolProperty or EPropertyType.ByteProperty or
        EPropertyType.Int8Property or EPropertyType.Int16Property or EPropertyType.IntProperty or EPropertyType.Int64Property or
        EPropertyType.UInt16Property or EPropertyType.UInt32Property or EPropertyType.UInt64Property or
        EPropertyType.FloatProperty or EPropertyType.DoubleProperty or EPropertyType.StrProperty or
        EPropertyType.NameProperty or EPropertyType.EnumProperty;

    /// <summary>Every property of the class chain (from the mappings), plus overridden ones the mappings don't know.</summary>
    public List<CdoProperty> ListProperties()
    {
        var result = new List<CdoProperty>();
        var overridden = new Dictionary<(string, int), PropertyData>();
        foreach (var data in Cdo.Data)
            overridden.TryAdd((data.Name.ToString(), data.ArrayIndex), data);

        var seen = new HashSet<(string, int)>();
        if (Asset.Mappings != null)
        {
            var schemaName = Cdo.GetClassTypeForAncestry(Asset, out var module)?.ToString();
            var modulePath = module?.ToString();
            for (var depth = 0; !string.IsNullOrEmpty(schemaName) && depth < 64; depth++)
            {
                UsmapSchema? schema;
                try { schema = Asset.Mappings.GetSchemaFromName(schemaName, Asset, modulePath, false); }
                catch { schema = null; }
                if (schema == null)
                    break;

                foreach (var property in schema.Properties.Values.OrderBy(o => o.SchemaIndex))
                {
                    var key = (property.Name, (int) property.ArrayIndex);
                    if (!seen.Add(key))
                        continue;
                    result.Add(new CdoProperty
                    {
                        Name = property.Name,
                        ArrayIndex = property.ArrayIndex,
                        Owner = schema.Name,
                        Type = property.PropertyData?.Type ?? EPropertyType.Unknown,
                        TypeText = Describe(property.PropertyData),
                        Mapping = property.PropertyData,
                        Current = overridden.GetValueOrDefault(key),
                    });
                }

                schemaName = schema.SuperType;
                modulePath = schema.SuperTypeModulePath;
            }
        }

        foreach (var ((name, arrayIndex), data) in overridden)
        {
            if (!seen.Add((name, arrayIndex)))
                continue;
            Enum.TryParse<EPropertyType>(data.PropertyType?.Value, out var type);
            result.Add(new CdoProperty
            {
                Name = name,
                ArrayIndex = arrayIndex,
                Owner = "?",
                Type = type,
                TypeText = data.PropertyType?.Value ?? "?",
                Current = data,
            });
        }
        return result;
    }

    public static string Describe(UsmapPropertyData? data) => data switch
    {
        null => "?",
        UsmapStructData o => o.StructType,
        UsmapEnumData o => o.Name,
        UsmapMapData o => $"Map<{Describe(o.InnerType)}, {Describe(o.ValueType)}>",
        UsmapArrayData o => $"{(o.Type == EPropertyType.SetProperty ? "Set" : "Array")}<{Describe(o.InnerType)}>",
        _ => data.Type.ToString().Replace("Property", ""),
    };

    /// <summary>Enum values for enum properties (from the mappings), empty otherwise.</summary>
    public IReadOnlyList<string> GetEnumValues(CdoProperty property)
    {
        var enumName = property.Mapping switch
        {
            UsmapEnumData o => o.Name,
            _ => property.Current switch
            {
                EnumPropertyData o => o.EnumType?.ToString(),
                BytePropertyData o => o.EnumType?.ToString(),
                _ => null,
            },
        };
        if (enumName == null || Asset.Mappings?.EnumMap == null || !Asset.Mappings.EnumMap.TryGetValue(enumName, out var usmapEnum))
            return [];
        // UAssetAPI writes unversioned enums by looking the value up in exactly these strings
        return usmapEnum.Values.OrderBy(o => o.Key).Select(o => o.Value).ToList();
    }

    #region values

    public static string FormatValue(PropertyData? data)
    {
        var inv = CultureInfo.InvariantCulture;
        return data switch
        {
            null => "",
            BoolPropertyData o => o.Value ? "true" : "false",
            Int8PropertyData o => o.Value.ToString(inv),
            Int16PropertyData o => o.Value.ToString(inv),
            IntPropertyData o => o.Value.ToString(inv),
            Int64PropertyData o => o.Value.ToString(inv),
            UInt16PropertyData o => o.Value.ToString(inv),
            UInt32PropertyData o => o.Value.ToString(inv),
            UInt64PropertyData o => o.Value.ToString(inv),
            FloatPropertyData o => o.Value.ToString("R", inv),
            DoublePropertyData o => o.Value.ToString("R", inv),
            StrPropertyData o => o.Value?.Value ?? "",
            NamePropertyData o => o.Value?.ToString() ?? "None",
            EnumPropertyData o => o.Value?.ToString() ?? "",
            BytePropertyData { ByteType: BytePropertyType.FName } o => o.EnumValue?.ToString() ?? "",
            BytePropertyData o => o.Value.ToString(inv),
            _ => "",
        };
    }

    /// <summary>Short one-line preview for the property list.</summary>
    public string Preview(CdoProperty property)
    {
        if (property.Current == null)
            return "";
        if (property.IsSimple)
            return FormatValue(property.Current);

        string json;
        try { json = Asset.SerializeJsonObject(property.Current, Formatting.None); }
        catch (Exception e) { return e.Message; }
        return json.Length > 160 ? json[..160] + "…" : json;
    }

    public string GetJson(CdoProperty property) => property.Current == null ? "" : Asset.SerializeJsonObject(property.Current, Formatting.Indented);

    /// <summary>Sets a simple value; throws <see cref="FormatException"/> for invalid text.</summary>
    public void SetValue(CdoProperty property, string text)
    {
        var data = property.Current ?? throw new InvalidOperationException("The property is not overridden.");
        var inv = CultureInfo.InvariantCulture;
        var trimmed = data is StrPropertyData ? text : text.Trim();
        switch (data)
        {
            case BoolPropertyData o: o.Value = bool.Parse(trimmed); break;
            case Int8PropertyData o: o.Value = sbyte.Parse(trimmed, NumberStyles.Integer, inv); break;
            case Int16PropertyData o: o.Value = short.Parse(trimmed, NumberStyles.Integer, inv); break;
            case IntPropertyData o: o.Value = int.Parse(trimmed, NumberStyles.Integer, inv); break;
            case Int64PropertyData o: o.Value = long.Parse(trimmed, NumberStyles.Integer, inv); break;
            case UInt16PropertyData o: o.Value = ushort.Parse(trimmed, NumberStyles.Integer, inv); break;
            case UInt32PropertyData o: o.Value = uint.Parse(trimmed, NumberStyles.Integer, inv); break;
            case UInt64PropertyData o: o.Value = ulong.Parse(trimmed, NumberStyles.Integer, inv); break;
            case FloatPropertyData o: o.Value = float.Parse(trimmed, NumberStyles.Float, inv); break;
            case DoublePropertyData o: o.Value = double.Parse(trimmed, NumberStyles.Float, inv); break;
            case StrPropertyData o: o.Value = FString.FromString(trimmed); break;
            case NamePropertyData o: o.Value = MakeName(trimmed); break;
            case EnumPropertyData o:
                CheckEnumValue(property, trimmed);
                o.Value = MakeName(trimmed);
                break;
            case BytePropertyData { ByteType: BytePropertyType.FName } o:
                CheckEnumValue(property, trimmed);
                o.EnumValue = MakeName(trimmed);
                break;
            case BytePropertyData o: o.Value = byte.Parse(trimmed, NumberStyles.Integer, inv); break;
            default: throw new FormatException($"{data.PropertyType} can only be edited as JSON.");
        }
        data.IsZero = false;
    }

    private void CheckEnumValue(CdoProperty property, string value)
    {
        var values = GetEnumValues(property);
        if (values.Count > 0 && !values.Contains(value) && !value.StartsWith(EnumPropertyData.InvalidEnumIndexFallbackPrefix, StringComparison.Ordinal))
            throw new FormatException($"'{value}' is not a value of this enum.");
    }

    /// <summary>Replaces the property with one deserialized from UAssetAPI property JSON.</summary>
    public void SetJson(CdoProperty property, string json)
    {
        var old = property.Current ?? throw new InvalidOperationException("The property is not overridden.");
        var data = DeserializeProperty(json);
        data.Name = old.Name;
        data.ArrayIndex = old.ArrayIndex;
        data.IsZero = false;
        Cdo.Data[Cdo.Data.IndexOf(old)] = data;
        property.Current = data;
        ResolveAncestries();
    }

    /// <summary>Overrides an inherited property in the CDO, starting from a zero value.</summary>
    public void AddOverride(CdoProperty property)
    {
        if (property.Current != null)
            return;
        if (property.Mapping == null)
            throw new InvalidOperationException("The property has no type information in the mappings.");

        var parentName = Cdo.GetClassTypeForAncestry(Asset, out var parentModulePath);
        var data = MainSerializer.TypeToClass(FName.DefineDummy(Asset, property.Type.ToString()), MakeName(property.Name),
            new AncestryInfo(), parentName, parentModulePath, Asset);
        data.ArrayIndex = property.ArrayIndex;
        InitializeValue(data, property);
        Cdo.Data.Add(data);
        property.Current = data;
        ResolveAncestries();
    }

    public void RemoveOverride(CdoProperty property)
    {
        if (property.Current == null)
            return;
        Cdo.Data.Remove(property.Current);
        property.Current = null;
    }

    private void InitializeValue(PropertyData data, CdoProperty property)
    {
        switch (data)
        {
            case StrPropertyData o: o.Value = FString.FromString(""); break;
            case NamePropertyData o: o.Value = MakeName("None"); break;
            case EnumPropertyData o:
                o.EnumType = FName.DefineDummy(Asset, (property.Mapping as UsmapEnumData)?.Name ?? "");
                o.InnerType = FName.DefineDummy(Asset, (property.Mapping as UsmapEnumData)?.InnerType?.Type.ToString() ?? "ByteProperty");
                o.Value = MakeName(GetEnumValues(property).FirstOrDefault() ?? "None");
                break;
            case BytePropertyData o:
                o.ByteType = property.Mapping is UsmapEnumData ? BytePropertyType.FName : BytePropertyType.Byte;
                if (property.Mapping is UsmapEnumData byteEnum)
                {
                    o.EnumType = MakeName(byteEnum.Name);
                    o.EnumValue = MakeName(GetEnumValues(property).FirstOrDefault() ?? "None");
                }
                break;
            case ObjectPropertyData o: o.Value = FPackageIndex.FromRawIndex(0); break;
            case ArrayPropertyData o:
                o.Value = [];
                if (property.Mapping is UsmapArrayData { InnerType: { } inner })
                    o.ArrayType = FName.DefineDummy(Asset, inner.Type.ToString());
                break;
            case StructPropertyData o:
                o.Value = [];
                if (property.Mapping is UsmapStructData structData)
                    o.StructType = MakeName(structData.StructType);
                break;
        }
    }

    private void ResolveAncestries() => Cdo.ResolveAncestries(Asset, new AncestryInfo());

    private FName MakeName(string text)
    {
        var name = FName.FromString(Asset, text);
        if (name == null)
            return name!;
        if (!Asset.ContainsNameReference(name.Value))
            Asset.AddNameReference(name.Value);
        return name;
    }

    private PropertyData DeserializeProperty(string json)
    {
        var toBeFilled = new Dictionary<FName, string>();
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Objects,
            NullValueHandling = NullValueHandling.Include,
            FloatParseHandling = FloatParseHandling.Double,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead,
            ContractResolver = new UAssetContractResolver(toBeFilled),
            Converters =
            [
                new FSignedZeroJsonConverter(),
                new FNameJsonConverter(null),
                new FStringTableJsonConverter(),
                new FStringJsonConverter(),
                new FPackageIndexJsonConverter(),
                new StringEnumConverter(),
                new GuidJsonConverter(),
                new ByteArrayJsonConverter(),
            ],
        };

        var data = JsonConvert.DeserializeObject<PropertyData>(json, settings)
                   ?? throw new FormatException("The JSON does not describe a property.");
        foreach (var (name, text) in toBeFilled)
        {
            var real = MakeName(text);
            name.Asset = Asset;
            name.Value = real?.Value ?? new FString(null);
            name.Number = real?.Number ?? 0;
        }
        return data;
    }

    #endregion
}
