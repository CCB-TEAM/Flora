using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>Applies a batch of scalar preview edits after validating each one on a detached export copy.</summary>
public static class WidgetPreviewWriter
{
    public sealed record Change(int ExportIndex, string Property, object Value);

    public static void Apply(UAsset asset, IReadOnlyList<Change> changes)
    {
        var copies = new Dictionary<int, NormalExport>();
        foreach (var change in changes)
        {
            if (change.ExportIndex <= 0 || change.ExportIndex > asset.Exports.Count || asset.Exports[change.ExportIndex - 1] is not NormalExport original)
                throw new InvalidOperationException($"Widget export #{change.ExportIndex} is not parsed.");
            if (!copies.TryGetValue(change.ExportIndex, out var copy))
            {
                copy = (NormalExport)original.Clone();
                copy.Data = original.Data.Select(p => (PropertyData)p.Clone()).ToList();
                copies.Add(change.ExportIndex, copy);
            }
            var editor = CdoEditor.ForDetachedExport(asset, copy);
            var path = change.Property.Split('.');
            var property = editor.ListProperties().SingleOrDefault(p => p.Name == path[0] && p.ArrayIndex == 0)
                ?? throw new InvalidOperationException($"{copy.ObjectName}.{path[0]} has no property mapping.");
            if (!property.IsOverridden) editor.AddOverride(property);
            for (var i = 1; i < path.Length; i++)
            {
                if (property.Current is not StructPropertyData parent)
                    throw new InvalidOperationException($"{change.Property}: {path[i - 1]} is not a structured property.");
                parent.IsZero = false;
                var child = parent.Value?.SingleOrDefault(p => p.Name.ToString() == path[i] && p.ArrayIndex == 0)
                    ?? throw new InvalidOperationException($"{change.Property}: {path[i]} is absent from the serialized structure.");
                Enum.TryParse<EPropertyType>(child.PropertyType?.Value, out var childType);
                property = new CdoProperty { Name = path[i], Type = childType, Current = child };
            }
            if (property.Current is TextPropertyData text)
            {
                if (change.Value is not string value)
                    throw new InvalidOperationException($"{change.Property}: expected text.");
                if (asset.GetCustomVersion<FEditorObjectVersion>() < FEditorObjectVersion.CultureInvariantTextSerializationKeyStability)
                    throw new InvalidOperationException("This engine version cannot safely write culture-invariant preview text.");
                text.Flags = ETextFlag.CultureInvariant;
                text.HistoryType = TextHistoryType.None;
                text.CultureInvariantString = FString.FromString(value);
                text.IsZero = false;
            }
            else if (property.Current is StructPropertyData structure && change.Value is CUE4Parse.UE4.Objects.Core.Math.FLinearColor color &&
                     structure.Value?.SingleOrDefault() is LinearColorPropertyData colorData)
            {
                colorData.Value = new UAssetAPI.UnrealTypes.FLinearColor(color.R, color.G, color.B, color.A);
                colorData.IsZero = false; structure.IsZero = false;
            }
            else if (property.Current is StructPropertyData vector && change.Value is CUE4Parse.UE4.Objects.Core.Math.FVector2D position &&
                     vector.Value?.SingleOrDefault() is Vector2DPropertyData vectorData)
            {
                vectorData.Value = new UAssetAPI.UnrealTypes.FVector2D(position.X, position.Y);
                vectorData.IsZero = false; vector.IsZero = false;
            }
            else if (property.IsSimple && change.Value is string or bool or int or long or float or double or byte or short)
            {
                var value = Convert.ToString(change.Value, CultureInfo.InvariantCulture) ?? "";
                var options = editor.GetEnumValues(property);
                if (options.Count > 0 && !options.Contains(value))
                {
                    var matches = options.Where(o => o.EndsWith("::" + value, StringComparison.Ordinal)).ToList();
                    if (matches.Count == 1) value = matches[0];
                }
                editor.SetValue(property, value);
            }
            else throw new InvalidOperationException($"{copy.ObjectName}.{change.Property} is not a supported scalar preview value.");
        }
        foreach (var (index, copy) in copies)
            ((NormalExport)asset.Exports[index - 1]).Data = copy.Data;
    }
}
