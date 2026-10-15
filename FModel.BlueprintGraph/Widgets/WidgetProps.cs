using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModel.BlueprintGraph.Widgets;

public readonly record struct WidgetMargin(float Left, float Top, float Right, float Bottom)
{
    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;
    public static readonly WidgetMargin Zero = new(0, 0, 0, 0);
}

public readonly record struct WidgetColor(float R, float G, float B, float A)
{
    public static readonly WidgetColor White = new(1, 1, 1, 1);
    public WidgetColor WithAlpha(float alpha) => this with { A = A * alpha };
}

/// <summary>
/// Tolerant readers for (unversioned) UMG properties. Missing properties mean "engine default", so every getter takes one.
/// </summary>
public static class WidgetProps
{
    // preview-only edits made in the designer, never written to the asset; they live as long as the loaded objects
    private static readonly ConditionalWeakTable<IPropertyHolder, ConcurrentDictionary<string, object>> Overrides = new();

    /// <summary>Makes every reader return <paramref name="value"/> for this property (designer preview only).</summary>
    public static void SetOverride(IPropertyHolder holder, string name, object value) =>
        Overrides.GetOrCreateValue(holder)[name] = value;

    public static void ClearOverride(IPropertyHolder holder, string name)
    {
        if (Overrides.TryGetValue(holder, out var values))
            values.TryRemove(name, out _);
    }

    public static void ClearOverrides(IPropertyHolder holder) => Overrides.Remove(holder);

    public static bool HasOverride(IPropertyHolder? holder, string name) =>
        holder != null && Overrides.TryGetValue(holder, out var values) && values.ContainsKey(name);

    public static bool HasOverrides(IPropertyHolder? holder) =>
        holder != null && Overrides.TryGetValue(holder, out var values) && !values.IsEmpty;

    /// <summary>The value stored in the asset, ignoring preview overrides.</summary>
    public static object? GetOriginal(IPropertyHolder? holder, string name)
    {
        if (holder == null)
            return null;

        foreach (var tag in holder.Properties)
        {
            if (!tag.Name.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = tag.Tag?.GenericValue;
            return value is FScriptStruct s ? s.StructType : value;
        }
        return null;
    }

    public static object? GetRaw(IPropertyHolder? holder, string name)
    {
        if (holder == null)
            return null;

        if (Overrides.TryGetValue(holder, out var values) && values.TryGetValue(name, out var overridden))
            return overridden;

        foreach (var tag in holder.Properties)
        {
            if (!tag.Name.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = tag.Tag?.GenericValue;
            return value is FScriptStruct s ? s.StructType : value;
        }
        return null;
    }

    public static bool Has(IPropertyHolder? holder, string name) => GetRaw(holder, name) != null;

    public static float Float(IPropertyHolder? holder, string name, float defaultValue = 0)
    {
        return GetRaw(holder, name) switch
        {
            float f => f,
            double d => (float) d,
            int i => i,
            byte b => b,
            long l => l,
            _ => defaultValue,
        };
    }

    public static int Int(IPropertyHolder? holder, string name, int defaultValue = 0)
    {
        return GetRaw(holder, name) switch
        {
            int i => i,
            byte b => b,
            long l => (int) l,
            float f => (int) f,
            _ => defaultValue,
        };
    }

    public static bool Bool(IPropertyHolder? holder, string name, bool defaultValue = false)
    {
        return GetRaw(holder, name) is bool b ? b : defaultValue;
    }

    /// <summary>Enum value name without its "EType::" prefix.</summary>
    public static string Enum(IPropertyHolder? holder, string name, string defaultValue)
    {
        var text = GetRaw(holder, name) switch
        {
            FName n => n.Text,
            string s => s,
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? defaultValue : text.Contains("::") ? text.SubstringAfter("::") : text;
    }

    public static string Text(IPropertyHolder? holder, string name, string defaultValue = "")
    {
        return GetRaw(holder, name) switch
        {
            FText t => t.Text ?? defaultValue,
            string s => s,
            FName n => n.Text,
            _ => defaultValue,
        };
    }

    public static FStructFallback? Struct(IPropertyHolder? holder, string name) => GetRaw(holder, name) as FStructFallback;

    public static (float X, float Y) Vector2(IPropertyHolder? holder, string name, float defaultX = 0, float defaultY = 0)
    {
        return GetRaw(holder, name) switch
        {
            FVector2D v => (v.X, v.Y),
            FIntPoint p => (p.X, p.Y),
            FStructFallback s => (Float(s, "X", defaultX), Float(s, "Y", defaultY)),
            _ => (defaultX, defaultY),
        };
    }

    public static WidgetMargin Margin(IPropertyHolder? holder, string name, WidgetMargin defaultValue)
    {
        return GetRaw(holder, name) is FStructFallback s
            ? new WidgetMargin(Float(s, "Left"), Float(s, "Top"), Float(s, "Right"), Float(s, "Bottom"))
            : defaultValue;
    }

    public static WidgetColor LinearColor(IPropertyHolder? holder, string name, WidgetColor defaultValue)
    {
        return GetRaw(holder, name) switch
        {
            FLinearColor c => new WidgetColor(c.R, c.G, c.B, c.A),
            FStructFallback s => new WidgetColor(Float(s, "R", 1), Float(s, "G", 1), Float(s, "B", 1), Float(s, "A", 1)),
            _ => defaultValue,
        };
    }

    /// <summary>FSlateColor: only the specified color is known offline (theme/foreground colors can't be resolved).</summary>
    public static WidgetColor SlateColor(IPropertyHolder? holder, string name, WidgetColor defaultValue)
    {
        return GetRaw(holder, name) is FStructFallback s
            ? LinearColor(s, "SpecifiedColor", defaultValue)
            : defaultValue;
    }

    public static UObject? Object(IPropertyHolder? holder, string name)
    {
        return GetRaw(holder, name) switch
        {
            FPackageIndex index when !index.IsNull => index.TryLoad(out UObject obj) ? obj : null,
            FSoftObjectPath path when !path.AssetPathName.IsNone => path.TryLoad(out UObject obj) ? obj : null,
            _ => null,
        };
    }

    public static List<UObject> Objects(IPropertyHolder? holder, string name)
    {
        var result = new List<UObject>();
        if (GetRaw(holder, name) is not UScriptArray array)
            return result;

        foreach (var item in array.Properties)
        {
            if (item.GenericValue is FPackageIndex index && !index.IsNull && index.TryLoad(out UObject obj))
                result.Add(obj);
        }
        return result;
    }

    public static List<FStructFallback> Structs(IPropertyHolder? holder, string name)
    {
        var result = new List<FStructFallback>();
        if (GetRaw(holder, name) is not UScriptArray array)
            return result;

        foreach (var item in array.Properties)
        {
            if (item.GenericValue is FScriptStruct { StructType: FStructFallback fallback })
                result.Add(fallback);
        }
        return result;
    }
}
