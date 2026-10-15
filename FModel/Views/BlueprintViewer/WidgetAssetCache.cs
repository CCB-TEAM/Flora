#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse_Conversion.Textures;
using FModel.BlueprintGraph.Widgets;
using FModel.Settings;
using Serilog;
using SkiaSharp;

namespace FModel.Views.BlueprintViewer;

/// <summary>
/// Decoded brush images and game fonts for the widget designer, shared by every open viewer and keyed by asset path
/// (loading the same asset twice gives two UObject instances).
/// </summary>
public static class WidgetAssetCache
{
    // images are dropped (not disposed, the canvas may still draw one) once they take more than this
    private const long MaxImageBytes = 768L * 1024 * 1024;
    private const int MaxMaterialDepth = 8;

    private static readonly ConcurrentDictionary<string, SKBitmap?> Images = new();
    private static readonly ConcurrentDictionary<string, SKTypeface?> Faces = new();
    private static readonly ConcurrentDictionary<string, WidgetFont> Fonts = new();
    private static long _imageBytes;

    private static string KeyOf(UObject obj)
    {
        // the same path can exist in two loaded games
        var provider = obj.Owner?.Provider;
        return $"{(provider != null ? RuntimeHelpers.GetHashCode(provider) : 0)}|{obj.GetPathName()}";
    }

    #region images

    /// <summary>The decoded image if it's ready; null while loading or when it can't be shown.</summary>
    public static SKBitmap? TryGetImage(UObject resource) => Images.TryGetValue(KeyOf(resource), out var bitmap) ? bitmap : null;

    public static bool IsImageLoaded(UObject resource) => Images.ContainsKey(KeyOf(resource));

    /// <summary>Decodes a brush resource (texture, sprite, material) once; safe to call from worker threads.</summary>
    public static SKBitmap? LoadImage(UObject resource)
    {
        var key = KeyOf(resource);
        if (Images.TryGetValue(key, out var cached))
            return cached;

        SKBitmap? bitmap = null;
        try
        {
            bitmap = DecodeResource(resource, 0);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to decode widget brush resource {Name}", resource.GetPathName());
        }

        if (Images.TryAdd(key, bitmap) && bitmap != null && Interlocked.Add(ref _imageBytes, bitmap.ByteCount) > MaxImageBytes)
        {
            Images.Clear();
            Interlocked.Exchange(ref _imageBytes, 0);
        }
        return bitmap;
    }

    private static SKBitmap? DecodeResource(UObject resource, int depth)
    {
        if (depth > MaxMaterialDepth)
            return null;

        switch (resource)
        {
            case UTexture2D texture:
                return texture.Decode(UserSettings.Default.CurrentDir.TexturePlatform)?.ToSkBitmap();
            case UPaperSprite sprite:
                return DecodeSprite(sprite);
            case UMaterialInstanceConstant instance:
            {
                // UI materials usually sample one main texture: show it, looking through the parents when the instance overrides none
                foreach (var parameter in instance.TextureParameterValues)
                {
                    if (parameter.ParameterValue.TryLoad(out UObject texture) && DecodeResource(texture, depth + 1) is { } bitmap)
                        return bitmap;
                }
                return instance.Parent is { } parent ? DecodeResource(parent, depth + 1) : null;
            }
            case UMaterialInstance instance:
                return instance.Parent is { } instanceParent ? DecodeResource(instanceParent, depth + 1) : null;
            case UMaterial material:
                foreach (var texture in material.ReferencedTextures)
                {
                    if (texture != null && DecodeResource(texture, depth + 1) is { } bitmap)
                        return bitmap;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Paper2D sprites (texture atlases) only use a region of their baked texture.</summary>
    private static SKBitmap? DecodeSprite(UPaperSprite sprite)
    {
        if (sprite.BakedSourceTexture == null || !sprite.BakedSourceTexture.TryLoad(out UTexture2D texture))
            return null;

        var atlas = LoadImage(texture);
        if (atlas == null)
            return null;

        var region = SKRectI.Create(
            (int) Math.Round(sprite.BakedSourceUV.X), (int) Math.Round(sprite.BakedSourceUV.Y),
            (int) Math.Round(sprite.BakedSourceDimension.X), (int) Math.Round(sprite.BakedSourceDimension.Y));
        region.Intersect(SKRectI.Create(0, 0, atlas.Width, atlas.Height));
        if (region.Width <= 0 || region.Height <= 0 || region.Width == atlas.Width && region.Height == atlas.Height)
            return atlas;

        using var subset = new SKBitmap();
        return atlas.ExtractSubset(subset, region) ? subset.Copy() : atlas;
    }

    #endregion

    #region fonts

    /// <summary>The game font of an FSlateFontInfo (TextBlock "Font"), or the default UI font when it can't be loaded.</summary>
    public static WidgetFont GetFont(FStructFallback? fontInfo)
    {
        var fontObject = WidgetProps.Object(fontInfo, "FontObject");
        var typefaceName = WidgetProps.Text(fontInfo, "TypefaceFontName");
        var outline = WidgetProps.Struct(fontInfo, "OutlineSettings");
        var letterSpacing = WidgetProps.Int(fontInfo, "LetterSpacing");
        var outlineSize = WidgetProps.Int(outline, "OutlineSize");
        var outlineColor = WidgetProps.LinearColor(outline, "OutlineColor", new WidgetColor(0, 0, 0, 1));

        var key = $"{(fontObject != null ? KeyOf(fontObject) : "default")}|{typefaceName}|{letterSpacing}|{outlineSize}|{outlineColor}";
        return Fonts.GetOrAdd(key, _ =>
        {
            var (primary, subTypefaces) = fontObject != null ? ResolveFont(fontObject, typefaceName) : (null, []);
            return new WidgetFont(primary ?? GraphStyle.Typeface, subTypefaces, letterSpacing, outlineSize, outlineColor);
        });
    }

    private static (SKTypeface? Primary, List<WidgetSubTypeface> SubTypefaces) ResolveFont(UObject fontObject, string typefaceName)
    {
        try
        {
            if (fontObject is UFontFace face)
                return (LoadFace(face, 0), []);

            var composite = WidgetProps.Struct(fontObject, "CompositeFont");
            var primary = PickTypeface(WidgetProps.Struct(composite, "DefaultTypeface"), typefaceName);

            var subTypefaces = new List<WidgetSubTypeface>();
            foreach (var sub in WidgetProps.Structs(composite, "SubTypefaces"))
            {
                var typeface = PickTypeface(WidgetProps.Struct(sub, "Typeface"), typefaceName);
                var ranges = WidgetProps.Structs(sub, "CharacterRanges").Select(ReadRange).Where(o => o.High >= o.Low).ToList();
                if (typeface != null && ranges.Count > 0)
                    subTypefaces.Add(new WidgetSubTypeface(typeface, ranges));
            }
            return (primary, subTypefaces);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to load widget font {Name}", fontObject.GetPathName());
            return (null, []);
        }
    }

    /// <summary>Typeface entry with the requested name (Regular, Bold...), the first one otherwise.</summary>
    private static SKTypeface? PickTypeface(FStructFallback? typeface, string name)
    {
        var entries = WidgetProps.Structs(typeface, "Fonts");
        var entry = entries.FirstOrDefault(o => WidgetProps.Text(o, "Name").Equals(name, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
        if (WidgetProps.GetRaw(entry, "Font") is not FFontData data)
            return null;

        if (data.LocalFontFaceAsset is { IsNull: false } index && index.TryLoad(out UFontFace face))
            return LoadFace(face, data.SubFaceIndex);
        return null;
    }

    private static SKTypeface? LoadFace(UFontFace face, int subFaceIndex)
    {
        return Faces.GetOrAdd($"{KeyOf(face)}|{subFaceIndex}", _ =>
        {
            var bytes = face.FontFaceData?.Data is { Length: > 0 } inline ? inline : ReadStreamedFace(face);
            if (bytes == null)
                return null;

            var offset = FontDataOffset(bytes);
            if (offset < 0)
                return null;

            using var data = SKData.CreateCopy(bytes.AsSpan(offset).ToArray());
            return SKTypeface.FromData(data, subFaceIndex);
        });
    }

    /// <summary>Cooked font faces that stream their data keep it in a .ufont file next to the package.</summary>
    private static byte[]? ReadStreamedFace(UFontFace face)
    {
        if (face.Owner?.Provider is not IFileProvider provider)
            return null;

        var packagePath = face.Owner.Name;
        var directory = packagePath.Contains('/') ? packagePath[..packagePath.LastIndexOf('/')] : packagePath;
        foreach (var path in new[] { $"{packagePath}.ufont", $"{directory}/{face.Name}.ufont" })
        {
            if (provider.TrySaveAsset(path, out var data))
                return data;
        }
        return null;
    }

    /// <summary>Start of the TTF/OTF data: .ufont files prefix it with its byte count.</summary>
    private static int FontDataOffset(byte[] bytes)
    {
        static bool IsFontMagic(byte[] b, int at) => b.Length >= at + 4 &&
            (b[at] == 0 && b[at + 1] == 1 && b[at + 2] == 0 && b[at + 3] == 0 ||
             Encoding.ASCII.GetString(b, at, 4) is "OTTO" or "true" or "ttcf");

        if (IsFontMagic(bytes, 0))
            return 0;
        return IsFontMagic(bytes, 4) ? 4 : -1;
    }

    private static (int Low, int High) ReadRange(FStructFallback range)
    {
        static int Bound(FStructFallback? bound, int open, int exclusiveStep)
        {
            var type = WidgetProps.Enum(bound, "Type", "Inclusive");
            var value = WidgetProps.Int(bound, "Value");
            return type switch
            {
                "Open" => open,
                "Exclusive" => value + exclusiveStep,
                _ => value,
            };
        }

        return (Bound(WidgetProps.Struct(range, "LowerBound"), 0, 1), Bound(WidgetProps.Struct(range, "UpperBound"), 0x10FFFF, -1));
    }

    #endregion
}

public sealed record WidgetSubTypeface(SKTypeface Typeface, List<(int Low, int High)> Ranges)
{
    public bool Covers(int codepoint) => Ranges.Any(o => codepoint >= o.Low && codepoint <= o.High);
}

/// <summary>
/// A game font as Slate uses it: the default typeface, sub typefaces for character ranges (CJK...), then system
/// fallbacks for anything still missing. Text is drawn in runs of the same typeface.
/// </summary>
public sealed class WidgetFont(SKTypeface primary, List<WidgetSubTypeface> subTypefaces, int letterSpacing, int outlineSize, WidgetColor outlineColor)
{
    private readonly ConcurrentDictionary<int, SKTypeface> _fallbacks = new();

    public SKTypeface Primary { get; } = primary;
    public int OutlineSize { get; } = outlineSize;
    public WidgetColor OutlineColor { get; } = outlineColor;

    private SKTypeface TypefaceFor(string element)
    {
        var codepoint = element.EnumerateRunes().FirstOrDefault().Value;
        foreach (var sub in subTypefaces)
        {
            if (sub.Covers(codepoint))
                return sub.Typeface;
        }

        if (codepoint < 0x80 || Primary.ContainsGlyphs(element))
            return Primary;

        return _fallbacks.GetOrAdd(codepoint, c =>
            subTypefaces.Select(o => o.Typeface).FirstOrDefault(o => o.ContainsGlyphs(element)) ??
            SKFontManager.Default.MatchCharacter(Primary.FamilyName, Primary.FontStyle, null, c) ??
            Primary);
    }

    private IEnumerable<(string Text, SKTypeface Typeface)> Runs(string text)
    {
        var builder = new StringBuilder();
        SKTypeface? current = null;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var typeface = TypefaceFor(element);
            if (current != null && typeface != current)
            {
                yield return (builder.ToString(), current);
                builder.Clear();
            }
            current = typeface;
            builder.Append(element);
        }
        if (current != null && builder.Length > 0)
            yield return (builder.ToString(), current);
    }

    /// <summary>Letter spacing is in 1/1000 em, between text elements.</summary>
    private float Spacing(float size) => letterSpacing * size / 1000f;

    public float Measure(string text, float size)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0f;
        var elements = 0;
        foreach (var (run, typeface) in Runs(text))
        {
            using var paint = new SKPaint { Typeface = typeface, TextSize = size };
            width += paint.MeasureText(run);
            elements += new StringInfo(run).LengthInTextElements;
        }
        return width + Spacing(size) * Math.Max(0, elements - 1);
    }

    public void Draw(SKCanvas canvas, string text, float x, float y, SKTextAlign align, float size, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (align != SKTextAlign.Left)
        {
            var width = Measure(text, size);
            x -= align == SKTextAlign.Center ? width / 2 : width;
        }

        var spacing = Spacing(size);
        foreach (var (run, typeface) in Runs(text))
        {
            using var font = new SKFont(typeface, size);
            using var measure = new SKPaint { Typeface = typeface, TextSize = size };
            if (spacing == 0)
            {
                canvas.DrawText(run, x, y, font, paint);
                x += measure.MeasureText(run);
                continue;
            }

            var enumerator = StringInfo.GetTextElementEnumerator(run);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                canvas.DrawText(element, x, y, font, paint);
                x += measure.MeasureText(element) + spacing;
            }
        }
    }
}
