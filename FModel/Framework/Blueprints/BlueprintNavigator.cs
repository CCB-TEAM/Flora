#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Framework.Blueprints;

/// <summary>Where a blueprint member is declared: the blueprint file and the function or variable in it.</summary>
public sealed record DefinitionTarget(GameFile File, string ClassName, string? FunctionName, string? VariableName);

/// <summary>A game asset referenced by an object's properties.</summary>
public sealed class AssetReference
{
    public required string Property { get; init; }
    public required string ObjectPath { get; init; }

    public string Name
    {
        get
        {
            var name = BlueprintNavigator.PackagePath(ObjectPath);
            return name[(name.LastIndexOf('/') + 1)..];
        }
    }
    public string Display => $"{Name}  ({Property})";
}

/// <summary>
/// Finds the assets behind object paths: the blueprint that declares a function or variable (walking up the class hierarchy)
/// and the game assets an object references. Engine (/Script) types have no asset and are never resolved.
/// </summary>
public static class BlueprintNavigator
{
    /// <summary>Whether the path names something in a game package (not a native /Script type).</summary>
    public static bool IsGameObjectPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path != "None" &&
        !path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) &&
        path.Contains('/');

    /// <summary>The package part of an object path ("/Game/UI/W_Menu.W_Menu_C:Sub" -> "/Game/UI/W_Menu").</summary>
    public static string PackagePath(string objectPath)
    {
        var path = objectPath.Trim().Trim('"', '\'');
        // class literals may be written as Class'/Game/...'
        var quote = path.IndexOf('\'');
        if (quote >= 0) path = path[(quote + 1)..].TrimEnd('\'');
        var colon = path.IndexOf(':');
        if (colon >= 0) path = path[..colon];
        var lastSlash = path.LastIndexOf('/');
        var dot = path.IndexOf('.', lastSlash + 1);
        return dot >= 0 ? path[..dot] : path;
    }

    public static bool TryFindGameFile(IFileProvider provider, string objectPath, out GameFile file)
    {
        file = null!;
        if (!IsGameObjectPath(objectPath))
            return false;

        var package = PackagePath(objectPath);
        foreach (var extension in new[] { ".uasset", ".umap" })
        {
            try
            {
                if (provider.TryGetGameFile(provider.FixPath(package + extension), out file) || provider.TryGetGameFile(package.TrimStart('/') + extension, out file))
                    return true;
            }
            catch
            {
                // malformed path
            }
        }
        return false;
    }

    /// <summary>
    /// Finds the blueprint declaring <paramref name="member"/>, starting at the class <paramref name="ownerPath"/> and walking up its parents.
    /// A null member resolves the class itself. Returns null with a reason when the member is native or can't be found.
    /// </summary>
    public static DefinitionTarget? Resolve(IFileProvider provider, string ownerPath, string? member, out string reason)
    {
        reason = string.Empty;
        var path = ownerPath;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!IsGameObjectPath(path))
            {
                reason = member == null ? $"{path} is a native type." : $"{member} is declared in native code ({path}).";
                return null;
            }
            if (!TryFindGameFile(provider, path, out var file))
            {
                reason = $"{PackagePath(path)} was not found in the loaded archives.";
                return null;
            }

            List<UObject> exports;
            try
            {
                exports = provider.LoadPackage(file).GetExports().ToList();
            }
            catch (Exception e)
            {
                reason = $"{file.Path} could not be read: {e.Message}";
                return null;
            }

            var lastSlash = path.LastIndexOf('/');
            var dot = path.IndexOf('.', lastSlash + 1);
            var className = dot >= 0 ? path[(dot + 1)..].Split(':')[0] : null;
            var structs = exports.OfType<UStruct>().Where(o => o is not UFunction).ToList();
            var owner = structs.FirstOrDefault(o => o.Name == className) ?? structs.FirstOrDefault(o => o is UClass) ?? structs.FirstOrDefault();
            if (owner == null || member == null)
                return new DefinitionTarget(file, owner?.Name ?? file.NameWithoutExtension, null, null);

            // the functions of a blueprint package all belong to its generated class
            if (exports.OfType<UFunction>().Any(o => o.Name == member))
                return new DefinitionTarget(file, owner.Name, member, null);
            if (owner.ChildProperties?.Any(o => o.Name.Text == member) == true)
                return new DefinitionTarget(file, owner.Name, null, member);

            var parent = owner.SuperStruct?.ResolvedObject?.GetPathName();
            if (string.IsNullOrEmpty(parent))
            {
                reason = $"{member} was not found in {owner.Name} or its parents.";
                return null;
            }
            path = parent;
        }

        reason = "The class hierarchy is too deep.";
        return null;
    }

    /// <summary>The game assets referenced by an object's properties (outside its own package).</summary>
    public static List<AssetReference> FindReferences(UObject? obj)
    {
        var result = new List<AssetReference>();
        if (obj == null)
            return result;

        var ownPackage = obj.Owner?.Name is { } ownerName ? PackagePath(ownerName) : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string property, string? path)
        {
            if (!IsGameObjectPath(path))
                return;
            var package = PackagePath(path!);
            if (ownPackage != null && SamePackage(package, ownPackage))
                return;
            if (seen.Add(path!))
                result.Add(new AssetReference { Property = property, ObjectPath = path! });
        }

        void Visit(string property, object? value, int depth)
        {
            if (value == null || depth > 8)
                return;
            switch (value)
            {
                case FPropertyTagType tag:
                    Visit(property, tag.GenericValue, depth);
                    break;
                case FPackageIndex index:
                    if (!index.IsNull)
                        Add(property, index.ResolvedObject?.GetPathName());
                    break;
                case FSoftObjectPath soft:
                    Add(property, soft.AssetPathName.Text);
                    break;
                case UScriptArray array:
                    for (var i = 0; i < array.Properties.Count; i++)
                        Visit($"{property}[{i}]", array.Properties[i], depth + 1);
                    break;
                case UScriptSet set:
                    foreach (var element in set.Properties)
                        Visit(property, element, depth + 1);
                    break;
                case UScriptMap map:
                    foreach (var (key, item) in map.Properties)
                    {
                        Visit(property, key, depth + 1);
                        Visit(property, item, depth + 1);
                    }
                    break;
                case FScriptStruct { StructType: { } inner }:
                    Visit(property, inner, depth + 1);
                    break;
                case FStructFallback fallback:
                    foreach (var child in fallback.Properties)
                        Visit($"{property}.{child.Name.Text}", child.Tag, depth + 1);
                    break;
            }
        }

        foreach (var property in obj.Properties)
            Visit(property.Name.Text, property.Tag, 0);

        // the widget's own class when it's a blueprint (user widgets)
        Add("Class", obj.Class?.GetPathName());
        return result;
    }

    private static bool SamePackage(string a, string b)
    {
        static string Normalize(string path)
        {
            path = path.TrimStart('/');
            var content = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (content >= 0) path = path[(content + "/Content/".Length)..];
            else if (path.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) path = path[5..];
            return path;
        }
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
