#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>One edit of a DataTable's rows (see <see cref="DataTableEditor.Apply"/>).</summary>
public sealed class DataTableOperation
{
    /// <summary>set (existing row), add (new row), upsert (set or add), delete, rename.</summary>
    public string Op { get; init; } = "";
    public string Row { get; init; } = "";

    /// <summary>Columns to change, in the canonical form of <see cref="DataTableEditor.ToJson"/>.</summary>
    public JObject? Values { get; init; }

    /// <summary>add/upsert: the row whose other columns the new row starts with (default: the first row).</summary>
    public string? CopyFrom { get; init; }
    public string? NewName { get; init; }
}

public sealed class DataTableChange
{
    public string Row { get; init; } = "";

    /// <summary>added, removed or changed.</summary>
    public string Kind { get; init; } = "";
    public List<(string Path, string? Before, string? After)> Cells { get; } = [];
}

/// <summary>
/// Edits the rows of a DataTable package (UAssetAPI): rows as plain JSON (numbers, strings, nested objects and arrays) instead of
/// UAssetAPI's property objects, changes that are checked and applied all together or not at all, a row-level diff against how the
/// table was opened, and checks before it is saved.
/// </summary>
public sealed class DataTableEditor
{
    private readonly UAsset _asset;
    private readonly DataTableExport _export;
    private readonly List<StructPropertyData> _original;

    public DataTableEditor(UAsset asset)
    {
        _asset = asset;
        _export = asset.Exports.OfType<DataTableExport>().FirstOrDefault()
                  ?? throw new InvalidOperationException("The package has no DataTable export.");
        _original = _export.Table.Data.Select(o => (StructPropertyData) o.Clone()).ToList();
    }

    public UAsset Asset => _asset;
    public List<StructPropertyData> Rows => _export.Table.Data;

    /// <summary>The row struct, from the table's RowStruct property.</summary>
    public string RowStruct
    {
        get
        {
            var property = _export.Data.OfType<ObjectPropertyData>().FirstOrDefault(o => o.Name.ToString() == "RowStruct");
            return property?.Value is { } index && !index.IsNull() ? KismetObjects.IndexSymbol(_asset, index) ?? "" : "";
        }
    }

    public bool IsModified => Diff().Count > 0;

    public StructPropertyData? Find(string name) =>
        Rows.FirstOrDefault(o => string.Equals(o.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));

    #region reading

    /// <summary>The columns of the table: name and a short type (taken from the first row; rows of one table share their layout).</summary>
    public List<(string Name, string Type)> Columns() =>
        Rows.Count == 0 ? [] : Rows[0].Value.Select(o => (ColumnName(Rows[0].Value, o), Describe(o))).ToList();

    /// <summary>A short type: int, float, bool, str, name, text, byte, enum:E, object, softobject, struct:S, array&lt;T&gt;, or the UAssetAPI type.</summary>
    public static string Describe(PropertyData property) => property switch
    {
        BoolPropertyData => "bool",
        Int8PropertyData => "int8",
        Int16PropertyData => "int16",
        IntPropertyData => "int",
        Int64PropertyData => "int64",
        UInt16PropertyData => "uint16",
        UInt32PropertyData => "uint32",
        UInt64PropertyData => "uint64",
        FloatPropertyData => "float",
        DoublePropertyData => "double",
        StrPropertyData => "str",
        NamePropertyData => "name",
        TextPropertyData => "text",
        EnumPropertyData e => $"enum:{e.EnumType}",
        BytePropertyData { ByteType: BytePropertyType.FName } b => $"enum:{b.EnumType}",
        BytePropertyData => "byte",
        SoftObjectPropertyData => "softobject",
        ObjectPropertyData => "object",
        StructPropertyData s => $"struct:{s.StructType}",
        ArrayPropertyData a => $"array<{(a.Value.Length > 0 ? Describe(a.Value[0]) : a.ArrayType?.ToString() ?? "?")}>",
        _ => property.PropertyType?.ToString() ?? property.GetType().Name,
    };

    /// <summary>Static arrays repeat a name with ArrayIndex 0, 1...: those columns are written Name[1].</summary>
    private static string ColumnName(IList<PropertyData> siblings, PropertyData property) =>
        property.ArrayIndex > 0 || siblings.Count(o => o.Name.ToString() == property.Name.ToString()) > 1
            ? $"{property.Name}[{property.ArrayIndex}]"
            : property.Name.ToString();

    /// <summary>A row as {column: value}.</summary>
    public JObject ReadRow(StructPropertyData row)
    {
        var json = new JObject();
        foreach (var property in row.Value)
            json[ColumnName(row.Value, property)] = ToJson(property);
        return json;
    }

    /// <summary>The canonical JSON of a property: independent of how UAssetAPI happens to store it.</summary>
    public JToken ToJson(PropertyData property)
    {
        switch (property)
        {
            case BoolPropertyData b: return b.Value;
            case Int8PropertyData v: return v.Value;
            case Int16PropertyData v: return v.Value;
            case IntPropertyData v: return v.Value;
            case Int64PropertyData v: return v.Value;
            case UInt16PropertyData v: return v.Value;
            case UInt32PropertyData v: return v.Value;
            case UInt64PropertyData v: return v.Value;
            case FloatPropertyData f: return double.Parse(f.Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            case DoublePropertyData d: return d.Value;
            case StrPropertyData s: return s.Value?.Value is { } text ? text : JValue.CreateNull();
            case NamePropertyData n: return n.Value?.ToString() is { } name ? name : JValue.CreateNull();
            case EnumPropertyData e: return e.Value?.ToString() is { } value ? value : JValue.CreateNull();
            case BytePropertyData b: return b.ByteType == BytePropertyType.Byte ? b.Value : b.EnumValue?.ToString();
            case TextPropertyData t:
                return t.HistoryType == TextHistoryType.Base && t.TableId == null
                    ? new JObject { ["source"] = t.CultureInvariantString?.Value, ["namespace"] = t.Namespace?.Value, ["key"] = t.Value?.Value }
                    : Raw(property);
            case ObjectPropertyData o: return KismetObjects.IndexSymbol(_asset, o.Value) is { } symbol ? symbol : JValue.CreateNull();
            case SoftObjectPropertyData so: return SoftPath(so.Value);
            case StructPropertyData s:
            {
                var json = new JObject();
                foreach (var child in s.Value)
                    json[ColumnName(s.Value, child)] = ToJson(child);
                return json;
            }
            case ArrayPropertyData a: return new JArray(a.Value.Select(ToJson));
            default: return Raw(property);
        }
    }

    private JObject Raw(PropertyData property) => new() { ["$raw"] = JToken.Parse(_asset.SerializeJsonObject(property)) };

    private static string? SoftPath(FSoftObjectPath path)
    {
        var package = path.AssetPath.PackageName?.ToString();
        var asset = path.AssetPath.AssetName?.ToString();
        var text = string.IsNullOrEmpty(package) || package == "None" ? asset : $"{package}.{asset}";
        if (string.IsNullOrEmpty(text) || text == "None")
            return null;
        return string.IsNullOrEmpty(path.SubPathString?.Value) ? text : $"{text}:{path.SubPathString!.Value}";
    }

    #endregion

    #region writing

    /// <summary>Changes the property to the JSON value; the value's shape is that of <see cref="ToJson"/>. Throws with the path of the problem.</summary>
    public void ApplyValue(PropertyData target, JToken value, string path)
    {
        string Fail(string expected) => $"{path}: expected {expected} for a {Describe(target)} column, got {Shorten(value)}";

        try
        {
            switch (target)
            {
                case BoolPropertyData b:
                    b.Value = value.Type == JTokenType.Boolean ? value.Value<bool>() : bool.TryParse(value.ToString(), out var parsed) ? parsed : throw new FormatException(Fail("true or false"));
                    break;
                case Int8PropertyData v: v.Value = checked((sbyte) Integer(value, path, target)); break;
                case Int16PropertyData v: v.Value = checked((short) Integer(value, path, target)); break;
                case IntPropertyData v: v.Value = checked((int) Integer(value, path, target)); break;
                case Int64PropertyData v: v.Value = Integer(value, path, target); break;
                case UInt16PropertyData v: v.Value = checked((ushort) Integer(value, path, target)); break;
                case UInt32PropertyData v: v.Value = checked((uint) Integer(value, path, target)); break;
                case UInt64PropertyData v: v.Value = checked((ulong) Integer(value, path, target)); break;
                case FloatPropertyData f: f.Value = checked((float) Number(value, path, target)); break;
                case DoublePropertyData d: d.Value = Number(value, path, target); break;
                case StrPropertyData s:
                    s.Value = value.Type == JTokenType.Null ? null : FString.FromString(value.Type == JTokenType.String ? value.Value<string>()! : throw new FormatException(Fail("a string")));
                    break;
                case NamePropertyData n:
                    n.Value = value.Type == JTokenType.String ? Name(value.Value<string>()!) : throw new FormatException(Fail("a name string"));
                    break;
                case EnumPropertyData e:
                    e.Value = value.Type == JTokenType.String ? Name(value.Value<string>()!) : throw new FormatException(Fail("the enumerator name"));
                    break;
                case BytePropertyData { ByteType: BytePropertyType.FName } b:
                    b.EnumValue = value.Type == JTokenType.String ? Name(value.Value<string>()!) : throw new FormatException(Fail("the enumerator name"));
                    break;
                case BytePropertyData b:
                    b.Value = checked((byte) Integer(value, path, target));
                    break;
                case TextPropertyData t:
                    ApplyText(t, value, path);
                    break;
                case ObjectPropertyData o:
                    o.Value = value.Type == JTokenType.Null
                        ? FPackageIndex.FromRawIndex(0)
                        : new KismetObjects.Builder(_asset, true).Reference(value.Type == JTokenType.String ? value.Value<string>()! : throw new FormatException(Fail("\"import:...\", \"export:...\" or null")), path)
                          ?? FPackageIndex.FromRawIndex(0);
                    break;
                case SoftObjectPropertyData so:
                    so.Value = value.Type == JTokenType.Null ? default : ParseSoftPath(value.Type == JTokenType.String ? value.Value<string>()! : throw new FormatException(Fail("an asset path like /Game/Dir/Asset.Asset")), so.Value);
                    break;
                case StructPropertyData s when value is JObject json && json.ContainsKey("$raw") == false:
                    ApplyStruct(s, json, path);
                    break;
                case ArrayPropertyData a when value is JArray array:
                    ApplyArray(a, array, path);
                    break;
                default:
                    if (value is JObject { Count: 1 } raw && raw["$raw"] is { } token)
                    {
                        ReplaceWithRaw(target, token, path);
                        break;
                    }
                    throw new FormatException(Fail(target is StructPropertyData ? "an object {field: value} or {\"$raw\": ...}" : target is ArrayPropertyData ? "an array" : "{\"$raw\": <UAssetAPI JSON of the property>}"));
            }
        }
        catch (OverflowException)
        {
            throw new FormatException($"{path}: {Shorten(value)} does not fit a {Describe(target)}");
        }
    }

    private static long Integer(JToken value, string path, PropertyData target)
    {
        if (value.Type == JTokenType.Integer)
            return value.Value<long>();
        if (value.Type == JTokenType.Float && Math.Abs(value.Value<double>() % 1) < 1e-9)
            return (long) value.Value<double>();
        if (value.Type == JTokenType.String && long.TryParse(value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException($"{path}: expected a whole number for a {Describe(target)} column, got {Shorten(value)}");
    }

    private static double Number(JToken value, string path, PropertyData target)
    {
        if (value.Type is JTokenType.Integer or JTokenType.Float)
            return value.Value<double>();
        if (value.Type == JTokenType.String && double.TryParse(value.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new FormatException($"{path}: expected a number for a {Describe(target)} column, got {Shorten(value)}");
    }

    private FName Name(string text)
    {
        _asset.AddNameReference(new FString(text));
        return FName.FromString(_asset, text);
    }

    private void ApplyText(TextPropertyData text, JToken value, string path)
    {
        if (text.HistoryType != TextHistoryType.Base || text.TableId != null)
        {
            if (value is JObject { Count: 1 } raw && raw["$raw"] is { } token)
                ReplaceWithRaw(text, token, path);
            else
                throw new FormatException($"{path}: this text is a {text.HistoryType} text (string table, formatted...): change it with {{\"$raw\": <UAssetAPI JSON>}}");
            return;
        }

        switch (value)
        {
            case JValue { Type: JTokenType.String } plain:
                // a plain string changes what the text shows; its namespace and key stay
                text.CultureInvariantString = FString.FromString(plain.Value<string>());
                break;
            case JObject json:
                foreach (var property in json.Properties())
                {
                    switch (property.Name)
                    {
                        case "source": text.CultureInvariantString = property.Value.Type == JTokenType.Null ? null : FString.FromString(property.Value.ToString()); break;
                        case "namespace": text.Namespace = property.Value.Type == JTokenType.Null ? null : FString.FromString(property.Value.ToString()); break;
                        case "key": text.Value = property.Value.Type == JTokenType.Null ? null : FString.FromString(property.Value.ToString()); break;
                        default: throw new FormatException($"{path}.{property.Name}: a text has source, namespace and key");
                    }
                }
                break;
            default:
                throw new FormatException($"{path}: expected a string (the displayed text) or {{source, namespace, key}}, got {Shorten(value)}");
        }
    }

    private static FSoftObjectPath ParseSoftPath(string text, FSoftObjectPath like)
    {
        var sub = "";
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            sub = text[(colon + 1)..];
            text = text[..colon];
        }

        // before UE 5.1 the asset name holds the whole path, since then it is split into package and asset
        var split = like.AssetPath.PackageName != null && like.AssetPath.PackageName.ToString() != "None";
        var dot = text.LastIndexOf('.');
        return split && dot > 0
            ? new FSoftObjectPath(FName.DefineDummy(null, text[..dot]), FName.DefineDummy(null, text[(dot + 1)..]), FString.FromString(sub))
            : new FSoftObjectPath(null!, FName.DefineDummy(null, text), FString.FromString(sub));
    }

    private void ApplyStruct(StructPropertyData target, JObject json, string path)
    {
        foreach (var field in json.Properties())
        {
            var child = FindChild(target.Value, field.Name)
                        ?? throw new FormatException($"{path}.{field.Name}: no such column; the columns are {string.Join(", ", target.Value.Select(o => ColumnName(target.Value, o)))}");
            ApplyValue(child, field.Value, $"{path}.{field.Name}");
        }
    }

    private static PropertyData? FindChild(IList<PropertyData> siblings, string column) =>
        siblings.FirstOrDefault(o => string.Equals(ColumnName(siblings, o), column, StringComparison.OrdinalIgnoreCase));

    private void ApplyArray(ArrayPropertyData target, JArray json, string path)
    {
        var template = target.Value.Length > 0 ? target.Value[0] : null;
        var elements = new PropertyData[json.Count];
        for (var i = 0; i < json.Count; i++)
        {
            var element = i < target.Value.Length
                ? target.Value[i]
                : template != null ? (PropertyData) template.Clone() : CreateElement(target, path);
            ApplyValue(element, json[i], $"{path}[{i}]");
            elements[i] = element;
        }
        target.Value = elements;
    }

    private PropertyData CreateElement(ArrayPropertyData array, string path)
    {
        var name = FName.FromString(_asset, array.Name.ToString());
        return array.ArrayType?.ToString() switch
        {
            "IntProperty" => new IntPropertyData(name),
            "Int64Property" => new Int64PropertyData(name),
            "FloatProperty" => new FloatPropertyData(name),
            "DoubleProperty" => new DoublePropertyData(name),
            "BoolProperty" => new BoolPropertyData(name),
            "NameProperty" => new NamePropertyData(name),
            "StrProperty" => new StrPropertyData(name),
            "ObjectProperty" => new ObjectPropertyData(name),
            "ByteProperty" => new BytePropertyData(name),
            var other => throw new FormatException($"{path}: the array is empty and its {other} elements can't be created from nothing; copy a row that has elements (copy_from) or give an element as {{\"$raw\": ...}}"),
        };
    }

    private void ReplaceWithRaw(PropertyData target, JToken raw, string path)
    {
        PropertyData replacement;
        try
        {
            replacement = _asset.DeserializeJsonObject<PropertyData>(raw.ToString());
        }
        catch (Exception e)
        {
            throw new FormatException($"{path}: the $raw value is not a UAssetAPI property: {e.Message}");
        }

        // changing the shape in place: copy every field the replacement carries
        foreach (var field in replacement.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (target.GetType() == replacement.GetType() && field.Name is not ("Name" or "Ancestry"))
                field.SetValue(target, field.GetValue(replacement));
        if (target.GetType() != replacement.GetType())
            throw new FormatException($"{path}: the $raw value is a {replacement.GetType().Name} but the column is a {target.GetType().Name}");
        if (target is PropertyData<object> || replacement.RawValue != null)
            target.RawValue = replacement.RawValue;
    }

    private static string Shorten(JToken value)
    {
        var text = value.ToString(Newtonsoft.Json.Formatting.None);
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    #endregion

    #region row operations

    /// <summary>A copy of every row, to put back with <see cref="Restore"/> (undo, dry runs).</summary>
    public List<StructPropertyData> Snapshot() => Rows.Select(o => (StructPropertyData) o.Clone()).ToList();

    public void Restore(List<StructPropertyData> snapshot)
    {
        Rows.Clear();
        Rows.AddRange(snapshot.Select(o => (StructPropertyData) o.Clone()));
    }

    /// <summary>
    /// Applies the operations in order; if one of them fails nothing is changed. Returns the errors (empty on success), each with the operation's number and the JSON path.
    /// </summary>
    public List<string> Apply(IReadOnlyList<DataTableOperation> operations)
    {
        var before = Rows.ToList();
        var backups = new Dictionary<StructPropertyData, StructPropertyData>(ReferenceEqualityComparer.Instance!);
        var errors = new List<string>();

        void Touch(StructPropertyData row)
        {
            if (!backups.ContainsKey(row))
                backups[row] = (StructPropertyData) row.Clone();
        }

        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var label = $"operation {i + 1} ({operation.Op} {operation.Row})";
            try
            {
                switch (operation.Op.ToLowerInvariant())
                {
                    case "set":
                    {
                        var row = Find(operation.Row) ?? throw new FormatException($"{label}: no row '{operation.Row}'");
                        Touch(row);
                        ApplyValues(row, operation.Values, label);
                        break;
                    }
                    case "add":
                    case "upsert":
                    {
                        var existing = Find(operation.Row);
                        if (existing != null && operation.Op.Equals("add", StringComparison.OrdinalIgnoreCase))
                            throw new FormatException($"{label}: the row already exists (use set, or upsert)");
                        if (existing != null)
                        {
                            Touch(existing);
                            ApplyValues(existing, operation.Values, label);
                            break;
                        }

                        ValidateName(operation.Row, label);
                        var template = operation.CopyFrom != null
                            ? Find(operation.CopyFrom) ?? throw new FormatException($"{label}: copy_from '{operation.CopyFrom}' is not a row")
                            : Rows.FirstOrDefault() ?? throw new FormatException($"{label}: the table is empty, there is no row to take the columns from");
                        var row = (StructPropertyData) template.Clone();
                        row.Name = Name(operation.Row);
                        ApplyValues(row, operation.Values, label);
                        Rows.Add(row);
                        break;
                    }
                    case "delete":
                    {
                        var row = Find(operation.Row) ?? throw new FormatException($"{label}: no row '{operation.Row}'");
                        Rows.Remove(row);
                        break;
                    }
                    case "rename":
                    {
                        var row = Find(operation.Row) ?? throw new FormatException($"{label}: no row '{operation.Row}'");
                        var newName = operation.NewName ?? throw new FormatException($"{label}: new_name is required");
                        ValidateName(newName, label);
                        if (Find(newName) is { } clash && !ReferenceEquals(clash, row))
                            throw new FormatException($"{label}: a row '{newName}' exists already");
                        Touch(row);
                        row.Name = Name(newName);
                        break;
                    }
                    default:
                        throw new FormatException($"{label}: unknown op (set, add, upsert, delete, rename)");
                }
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
            {
                errors.Add(e.Message.StartsWith("operation ", StringComparison.Ordinal) ? e.Message : $"{label}: {e.Message}");
                break;
            }
        }

        if (errors.Count > 0)
        {
            // all or nothing
            Rows.Clear();
            Rows.AddRange(before);
            foreach (var (row, backup) in backups)
            {
                var index = Rows.IndexOf(row);
                if (index >= 0)
                    Rows[index] = backup;
            }
        }
        return errors;
    }

    private void ApplyValues(StructPropertyData row, JObject? values, string label)
    {
        if (values == null)
            return;
        foreach (var field in values.Properties())
        {
            var cell = FindChild(row.Value, field.Name)
                       ?? throw new FormatException($"{label}: no column '{field.Name}'; the columns are {string.Join(", ", row.Value.Select(o => ColumnName(row.Value, o)))}");
            ApplyValue(cell, field.Value, $"{label}.{field.Name}");
        }
    }

    private void ValidateName(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"{label}: the row name is empty");
        if (name != name.Trim() || name.Any(char.IsControl))
            throw new FormatException($"{label}: the row name '{name}' has leading or trailing spaces or control characters");
    }

    /// <summary>Puts a row (or every row) back as it was when the table was opened. Returns the rows reverted.</summary>
    public List<string> Revert(string? rowName)
    {
        var reverted = new List<string>();
        if (rowName == null)
        {
            Rows.Clear();
            Rows.AddRange(_original.Select(o => (StructPropertyData) o.Clone()));
            return [.. _original.Select(o => o.Name.ToString())];
        }

        var original = _original.FirstOrDefault(o => string.Equals(o.Name.ToString(), rowName, StringComparison.OrdinalIgnoreCase));
        var current = Find(rowName);
        if (original == null && current == null)
            throw new InvalidOperationException($"No row '{rowName}', now or when the table was opened.");
        if (original == null)
            Rows.Remove(current!);
        else if (current == null)
            Rows.Insert(Math.Min(_original.IndexOf(original), Rows.Count), (StructPropertyData) original.Clone());
        else
            Rows[Rows.IndexOf(current)] = (StructPropertyData) original.Clone();
        reverted.Add(rowName);
        return reverted;
    }

    #endregion

    #region diff and validation

    /// <summary>Row-level changes since the table was opened, with the changed cells (path, before, after).</summary>
    public List<DataTableChange> Diff()
    {
        var changes = new List<DataTableChange>();
        var originals = _original.ToDictionary(o => o.Name.ToString(), StringComparer.OrdinalIgnoreCase);
        var currents = new Dictionary<string, StructPropertyData>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            currents.TryAdd(row.Name.ToString(), row);

        foreach (var (name, row) in currents)
        {
            if (!originals.TryGetValue(name, out var original))
            {
                changes.Add(new DataTableChange { Row = name, Kind = "added" });
                continue;
            }
            var change = new DataTableChange { Row = name, Kind = "changed" };
            CompareCells(ReadRow(original), ReadRow(row), "", change.Cells);
            if (change.Cells.Count > 0)
                changes.Add(change);
        }
        foreach (var name in originals.Keys.Where(o => !currents.ContainsKey(o)))
            changes.Add(new DataTableChange { Row = name, Kind = "removed" });
        return changes;
    }

    private static void CompareCells(JToken before, JToken after, string path, List<(string Path, string? Before, string? After)> cells)
    {
        if (before is JObject b && after is JObject a)
        {
            foreach (var key in b.Properties().Select(o => o.Name).Union(a.Properties().Select(o => o.Name)))
                CompareCells(b[key] ?? JValue.CreateNull(), a[key] ?? JValue.CreateNull(), path.Length == 0 ? key : $"{path}.{key}", cells);
        }
        else if (before is JArray ba && after is JArray aa && ba.Count == aa.Count)
        {
            for (var i = 0; i < ba.Count; i++)
                CompareCells(ba[i], aa[i], $"{path}[{i}]", cells);
        }
        else if (!JToken.DeepEquals(before, after))
        {
            cells.Add((path, before.ToString(Newtonsoft.Json.Formatting.None), after.ToString(Newtonsoft.Json.Formatting.None)));
        }
    }

    /// <summary>Problems that make the table wrong or its rows inconsistent: duplicate names, rows that don't share the first row's columns and types.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var group in Rows.GroupBy(o => o.Name.ToString(), StringComparer.OrdinalIgnoreCase).Where(o => o.Count() > 1))
            problems.Add($"duplicate row name '{group.Key}' ({group.Count()} rows; row names are not case sensitive)");
        foreach (var row in Rows.Where(o => string.IsNullOrWhiteSpace(o.Name.ToString())))
            problems.Add("a row has an empty name");

        if (Rows.Count > 0)
        {
            var layout = Rows[0].Value.Select(o => $"{ColumnName(Rows[0].Value, o)}:{Describe(o)}").ToList();
            foreach (var row in Rows.Skip(1))
            {
                var other = row.Value.Select(o => $"{ColumnName(row.Value, o)}:{Describe(o)}").ToList();
                if (!layout.SequenceEqual(other))
                    problems.Add($"row '{row.Name}' has other columns than '{Rows[0].Name}': " +
                                 string.Join(", ", layout.Except(other).Select(o => "missing " + o).Concat(other.Except(layout).Select(o => "extra " + o))));
            }
        }
        return problems;
    }

    #endregion

    #region import and export

    /// <summary>{"struct": ..., "columns": {name: type}, "rows": {row: {column: value}}}.</summary>
    public JObject ExportJson() => new()
    {
        ["struct"] = RowStruct,
        ["columns"] = new JObject(Columns().Select(o => new JProperty(o.Name, o.Type))),
        ["rows"] = new JObject(Rows.Select(o => new JProperty(o.Name.ToString(), ReadRow(o)))),
    };

    /// <summary>Rows as CSV: the first column is the row name, nested values are JSON text.</summary>
    public string ExportCsv()
    {
        var columns = Columns().Select(o => o.Name).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Name" }.Concat(columns).Select(Csv)));
        foreach (var row in Rows)
        {
            var json = ReadRow(row);
            sb.AppendLine(string.Join(",", new[] { row.Name.ToString() }.Concat(columns.Select(c => CsvCell(json[c])))
                .Select(Csv)));
        }
        return sb.ToString();
    }

    private static string CsvCell(JToken? value) => value switch
    {
        null or { Type: JTokenType.Null } => "",
        JValue { Type: JTokenType.String } s => s.Value<string>() ?? "",
        JValue v => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString(Newtonsoft.Json.Formatting.None),
    };

    private static string Csv(string text) => text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;

    /// <summary>Turns an import file (this class's JSON, or CSV) into upsert operations.</summary>
    public List<DataTableOperation> ParseImport(string text, bool csv)
    {
        var operations = new List<DataTableOperation>();
        if (!csv)
        {
            var token = JToken.Parse(text);
            var rows = token is JObject { } root ? root["rows"] as JObject ?? root : null;
            if (rows != null)
            {
                foreach (var row in rows.Properties().Where(o => o.Value is JObject))
                    operations.Add(new DataTableOperation { Op = "upsert", Row = row.Name, Values = (JObject) row.Value });
            }
            else if (token is JArray array)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var name = (item["Name"] ?? item["name"] ?? item["RowName"])?.ToString() ?? throw new FormatException("a row object needs a Name");
                    var values = new JObject(item.Properties().Where(o => o.Name is not ("Name" or "name" or "RowName")));
                    operations.Add(new DataTableOperation { Op = "upsert", Row = name, Values = values });
                }
            }
            else
            {
                throw new FormatException("expected {\"rows\": {Name: {column: value}}} or [{\"Name\": ..., column: value}]");
            }
            return operations;
        }

        var lines = ParseCsv(text);
        if (lines.Count == 0)
            return operations;
        var header = lines[0];
        var types = Columns().ToDictionary(o => o.Name, o => o.Type, StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(o => o.Count > 0 && o.Any(c => c.Length > 0)))
        {
            var values = new JObject();
            for (var i = 1; i < header.Count && i < line.Count; i++)
            {
                if (!types.TryGetValue(header[i], out var type))
                    throw new FormatException($"column '{header[i]}' is not a column of the table");
                values[header[i]] = CsvValue(line[i], type);
            }
            operations.Add(new DataTableOperation { Op = "upsert", Row = line[0], Values = values });
        }
        return operations;
    }

    private static JToken CsvValue(string text, string type)
    {
        if (type is "int" or "int8" or "int16" or "int64" or "uint16" or "uint32" or "uint64" or "byte")
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : text;
        if (type is "float" or "double")
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : text;
        if (type == "bool")
            return bool.TryParse(text, out var flag) ? flag : text;
        if (type.StartsWith("struct:", StringComparison.Ordinal) || type.StartsWith("array<", StringComparison.Ordinal))
        {
            try { return JToken.Parse(text); }
            catch (Newtonsoft.Json.JsonException) { return text; }
        }
        if (type == "text")
            return text;
        return text.Length == 0 && type is "object" or "softobject" ? JValue.CreateNull() : text;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    #endregion
}
