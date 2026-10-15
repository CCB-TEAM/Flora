using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FModel.BlueprintGraph.Widgets;
using FModel.Framework;
using FModel.Localization;

namespace FModel.ViewModels;

public enum EWidgetPropertyKind
{
    Header,
    Number,
    Integer,
    Bool,
    Enum,
    Text,
    ReadOnly,
}

/// <summary>
/// One editable property of the selected widget or its slot. Edits are preview-only overrides
/// (<see cref="WidgetProps.SetOverride"/>): the designer redraws with them, the asset is never changed.
/// </summary>
public class WidgetPropertyRow : ViewModel
{
    private readonly Func<object> _get;
    private readonly Action<object> _set;
    private readonly Func<bool> _isOverridden;
    private readonly Action _reset;
    private readonly Action _changed;

    public WidgetPropertyRow(string label, int depth, EWidgetPropertyKind kind, Func<object> get, Action<object> set,
        Func<bool> isOverridden, Action reset, Action changed, IReadOnlyList<string> options = null, string tooltip = null)
    {
        Label = label;
        Depth = depth;
        Kind = kind;
        Options = options ?? [];
        Tooltip = tooltip ?? label;
        _get = get;
        _set = set;
        _isOverridden = isOverridden;
        _reset = reset;
        _changed = changed;
        _valueText = Format(_get?.Invoke());
    }

    public static WidgetPropertyRow HeaderRow(string label, int depth) =>
        new(label, depth, EWidgetPropertyKind.Header, null, null, () => false, () => { }, () => { });

    public string Label { get; }
    public int Depth { get; }
    public EWidgetPropertyKind Kind { get; }
    public IReadOnlyList<string> Options { get; }
    public string Tooltip { get; }

    public System.Windows.Thickness Indent => new(Depth * 12, 0, 0, 0);
    public bool IsHeader => Kind == EWidgetPropertyKind.Header;
    public bool IsTextEditor => Kind is EWidgetPropertyKind.Number or EWidgetPropertyKind.Integer or EWidgetPropertyKind.Text;
    public bool IsEnumEditor => Kind == EWidgetPropertyKind.Enum;
    public bool IsBoolEditor => Kind == EWidgetPropertyKind.Bool;
    public bool IsReadOnlyValue => Kind == EWidgetPropertyKind.ReadOnly;
    public bool IsOverridden => _isOverridden();

    private string _valueText;
    /// <summary>Setting it from the UI applies the value (text boxes update on Enter / focus loss).</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (value == _valueText)
                return;
            _valueText = value;
            Commit();
        }
    }

    public bool BoolValue
    {
        get => _get?.Invoke() is true;
        set
        {
            _set(value);
            AfterChange();
        }
    }

    private string _errorText;
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    private void Commit()
    {
        object value = Kind switch
        {
            EWidgetPropertyKind.Number => float.TryParse(_valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null,
            EWidgetPropertyKind.Integer => int.TryParse(_valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
            EWidgetPropertyKind.Enum or EWidgetPropertyKind.Text => _valueText ?? string.Empty,
            _ => null,
        };

        if (value == null)
        {
            ErrorText = LocalizationManager.Instance.Get("WidgetEditor_InvalidValue");
            RaisePropertyChanged(nameof(ValueText));
            return;
        }

        ErrorText = null;
        _set(value);
        AfterChange();
    }

    public void Reset()
    {
        _reset();
        ErrorText = null;
        AfterChange();
    }

    private void AfterChange()
    {
        Refresh();
        _changed();
    }

    public void Refresh()
    {
        _valueText = Format(_get?.Invoke());
        RaisePropertyChanged(nameof(ValueText));
        RaisePropertyChanged(nameof(BoolValue));
        RaisePropertyChanged(nameof(IsOverridden));
    }

    public static string Format(object value) => value switch
    {
        null => string.Empty,
        float f => f.ToString("0.####", CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        FText t => t.Text ?? string.Empty,
        FName n => n.Text.Contains("::") ? n.Text.SubstringAfter("::") : n.Text,
        string s => s.Contains("::") ? s.SubstringAfter("::") : s,
        FPackageIndex index => index.IsNull ? "None" : index.Name,
        FSoftObjectPath path => path.AssetPathName.IsNone ? "None" : path.AssetPathName.Text,
        UScriptArray array => $"[{array.Properties.Count}]",
        IConvertible c => c.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>Flattens a widget's and its slot's properties (nested structs included) into editable rows.</summary>
public static class WidgetPropertyRows
{
    private const int MaxDepth = 4;

    private static readonly string[] SlateVisibility = ["Visible", "Collapsed", "Hidden", "HitTestInvisible", "SelfHitTestInvisible"];

    /// <param name="touched">receives every holder that got an override, so they can all be reset</param>
    public static List<WidgetPropertyRow> Build(WidgetNode node, IFileProvider provider, Action<IPropertyHolder> touched, Action changed)
    {
        var rows = new List<WidgetPropertyRow>();
        var builder = new Builder(rows, provider, touched, changed);

        if (node.Export != null)
        {
            rows.Add(WidgetPropertyRow.HeaderRow($"{node.Name} [{node.ClassName}]", 0));
            // common properties that are often left at their default (and so missing from the asset)
            if (WidgetProps.GetOriginal(node.Export, "Visibility") == null)
                builder.AddScalar(node.Export, "Visibility", "Visibility", 1, EWidgetPropertyKind.Enum, "Visible", SlateVisibility);
            if (WidgetProps.GetOriginal(node.Export, "RenderOpacity") == null)
                builder.AddScalar(node.Export, "RenderOpacity", "RenderOpacity", 1, EWidgetPropertyKind.Number, 1f);
            builder.AddHolder(node.Export, 1);
        }

        if (node.Slot != null)
        {
            rows.Add(WidgetPropertyRow.HeaderRow($"Slot [{node.SlotClassName}]", 0));
            builder.AddHolder(node.Slot, 1);
        }
        return rows;
    }

    private sealed class Builder(List<WidgetPropertyRow> rows, IFileProvider provider, Action<IPropertyHolder> touched, Action changed)
    {
        public void AddHolder(IPropertyHolder holder, int depth)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in holder.Properties)
            {
                var name = tag.Name.Text;
                if (!seen.Add(name))
                    continue; // static arrays: only the first element is read by the designer

                var original = WidgetProps.GetOriginal(holder, name);
                AddValue(holder, name, original, tag.TagData?.EnumName, depth);
            }
        }

        private void AddValue(IPropertyHolder holder, string name, object original, string enumName, int depth)
        {
            switch (original)
            {
                case null:
                    return;
                case FStructFallback structValue when depth < MaxDepth:
                    rows.Add(WidgetPropertyRow.HeaderRow(name, depth));
                    AddHolder(structValue, depth + 1);
                    return;
                case float or double:
                    AddScalar(holder, name, name, depth, EWidgetPropertyKind.Number, 0f);
                    return;
                case int or long or short or sbyte or uint or ushort:
                    AddScalar(holder, name, name, depth, EWidgetPropertyKind.Integer, 0);
                    return;
                case byte when enumName == null:
                    AddScalar(holder, name, name, depth, EWidgetPropertyKind.Integer, 0);
                    return;
                case bool:
                    AddScalar(holder, name, name, depth, EWidgetPropertyKind.Bool, false);
                    return;
                case FName or string or byte when enumName != null || original.ToString()!.Contains("::"):
                {
                    var options = EnumOptions(enumName);
                    AddScalar(holder, name, name, depth, options.Count > 0 ? EWidgetPropertyKind.Enum : EWidgetPropertyKind.Text, string.Empty, options);
                    return;
                }
                case FName or string or FText:
                    AddScalar(holder, name, name, depth, EWidgetPropertyKind.Text, string.Empty);
                    return;
                case FLinearColor:
                    rows.Add(WidgetPropertyRow.HeaderRow(name, depth));
                    AddComponent(holder, name, "R", depth + 1, o => ((FLinearColor) o).R, (o, v) => { var c = (FLinearColor) o; return new FLinearColor(v, c.G, c.B, c.A); });
                    AddComponent(holder, name, "G", depth + 1, o => ((FLinearColor) o).G, (o, v) => { var c = (FLinearColor) o; return new FLinearColor(c.R, v, c.B, c.A); });
                    AddComponent(holder, name, "B", depth + 1, o => ((FLinearColor) o).B, (o, v) => { var c = (FLinearColor) o; return new FLinearColor(c.R, c.G, v, c.A); });
                    AddComponent(holder, name, "A", depth + 1, o => ((FLinearColor) o).A, (o, v) => { var c = (FLinearColor) o; return new FLinearColor(c.R, c.G, c.B, v); });
                    return;
                case FVector2D:
                    rows.Add(WidgetPropertyRow.HeaderRow(name, depth));
                    AddComponent(holder, name, "X", depth + 1, o => ((FVector2D) o).X, (o, v) => new FVector2D(v, ((FVector2D) o).Y));
                    AddComponent(holder, name, "Y", depth + 1, o => ((FVector2D) o).Y, (o, v) => new FVector2D(((FVector2D) o).X, v));
                    return;
                default:
                    rows.Add(new WidgetPropertyRow(name, depth, EWidgetPropertyKind.ReadOnly, () => WidgetProps.GetRaw(holder, name),
                        _ => { }, () => false, () => { }, () => { }, tooltip: $"{name} ({original.GetType().Name}, read-only)"));
                    return;
            }
        }

        public void AddScalar(IPropertyHolder holder, string name, string label, int depth, EWidgetPropertyKind kind, object defaultValue,
            IReadOnlyList<string> options = null)
        {
            rows.Add(new WidgetPropertyRow(label, depth, kind,
                () => WidgetProps.GetRaw(holder, name) ?? defaultValue,
                value =>
                {
                    WidgetProps.SetOverride(holder, name, value);
                    touched(holder);
                },
                () => WidgetProps.HasOverride(holder, name),
                () => WidgetProps.ClearOverride(holder, name),
                changed, options));
        }

        /// <summary>Component of a native struct (color, vector): editing it overrides the whole struct value.</summary>
        private void AddComponent(IPropertyHolder holder, string name, string label, int depth, Func<object, float> read, Func<object, float, object> write)
        {
            rows.Add(new WidgetPropertyRow(label, depth, EWidgetPropertyKind.Number,
                () => WidgetProps.GetRaw(holder, name) is { } value ? read(value) : 0f,
                value =>
                {
                    if (WidgetProps.GetRaw(holder, name) is not { } current)
                        return;
                    WidgetProps.SetOverride(holder, name, write(current, (float) value));
                    touched(holder);
                },
                () => WidgetProps.HasOverride(holder, name),
                () => WidgetProps.ClearOverride(holder, name),
                changed, tooltip: $"{name}.{label}"));
        }

        private IReadOnlyList<string> EnumOptions(string enumName)
        {
            if (string.IsNullOrEmpty(enumName))
                return [];
            if (enumName == "ESlateVisibility")
                return SlateVisibility;
            if (provider?.MappingsForGame?.Enums is not { } enums || !enums.TryGetValue(enumName, out var values))
                return [];

            return values.OrderBy(o => o.Key)
                .Select(o => o.Value.Contains("::") ? o.Value.SubstringAfter("::") : o.Value)
                .Where(o => !o.EndsWith("_MAX", StringComparison.Ordinal))
                .ToList();
        }
    }
}
