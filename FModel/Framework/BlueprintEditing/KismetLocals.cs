#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>
/// A new local variable of a function (a property of its frame; for the event graph, of the persistent ubergraph frame), declared so
/// bytecode objects can read and write it, e.g. to receive an out parameter. Applied to the package before the object edits.
/// </summary>
public sealed class LocalDeclaration
{
    public string FunctionName { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>int, int64, float, double, bool, byte, name, string, text, object:PATH, class:PATH, softobject:PATH, struct:PATH, enum:PATH, array:TYPE.</summary>
    public string Type { get; init; } = "";

    /// <summary>A property to copy the type from ({"path": [...], "owner": "export:..."}), instead of <see cref="Type"/>.</summary>
    public JToken? Like { get; init; }

    public string Description { get; init; } = "";

    public (string Function, string Name) Key => (FunctionName, Name);

    public override string ToString() => $"{FunctionName}.{Name}: {(Like != null ? "like " + Like.ToString(Newtonsoft.Json.Formatting.None) : Type)}";
}

public static class KismetLocals
{
    private const EPropertyFlags PlainOldData = EPropertyFlags.CPF_ZeroConstructor | EPropertyFlags.CPF_IsPlainOldData | EPropertyFlags.CPF_NoDestructor |
                                                EPropertyFlags.CPF_HasGetValueTypeHash;

    /// <summary>Whether the package stores properties as FFields (UE 4.25 and later), the only layout locals can be added to.</summary>
    public static bool Supported(UAsset asset) => asset.GetCustomVersion<FCoreObjectVersion>() >= FCoreObjectVersion.FProperties;

    /// <summary>Declares the locals in the package (create mode); returns notes about what was guessed or added.</summary>
    public static List<string> Apply(UAsset asset, IReadOnlyCollection<LocalDeclaration> declarations)
    {
        var notes = new List<string>();
        foreach (var declaration in declarations)
        {
            var function = KismetConstantPatcher.FindFunction(asset, declaration.FunctionName)
                           ?? throw new InvalidOperationException($"{declaration}: the function has no bytecode in the package.");
            var property = Build(asset, function, declaration, true, notes);
            function.LoadedProperties = [.. function.LoadedProperties, property!];
        }
        return notes;
    }

    /// <summary>
    /// Builds the property of a declaration. In check mode nothing is added to the package and null comes back; errors are thrown either way.
    /// </summary>
    public static FProperty? Build(UAsset asset, StructExport function, LocalDeclaration declaration, bool create, List<string> notes)
    {
        if (!Supported(asset))
            throw new InvalidOperationException("This package stores properties the pre-4.25 way (UProperty exports); declaring locals is not supported for it.");
        if (string.IsNullOrWhiteSpace(declaration.Name) || declaration.Name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new InvalidOperationException($"'{declaration.Name}' is not a valid variable name (letters, digits and _).");
        if (function.LoadedProperties.Any(o => o.Name.ToString().Equals(declaration.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{function.ObjectName} already has a property named {declaration.Name}.");

        var builder = new KismetObjects.Builder(asset, create);
        FProperty candidate;
        if (declaration.Like != null)
        {
            var template = FindProperty(asset, declaration.Like)
                           ?? throw new InvalidOperationException($"No property {declaration.Like.ToString(Newtonsoft.Json.Formatting.None)} in this package to copy the type from.");
            candidate = Clone(template);
        }
        else
        {
            candidate = FromType(builder, declaration.Type.Trim(), "type");
            // the compiler's flags and sizes for the same type, when the package declares one
            if (FindEquivalent(asset, candidate) is { } equivalent)
                candidate = Clone(equivalent);
            else
                notes.Add($"{declaration.Name}: flags and size are Flora's defaults for {declaration.Type} (no property of that type in the package to copy them from)");
        }
        notes.AddRange(builder.Notes);
        if (!create)
            return null;

        Rename(asset, candidate, declaration.Name);
        // a local of the frame, not a parameter
        candidate.PropertyFlags &= ~(EPropertyFlags.CPF_Parm | EPropertyFlags.CPF_OutParm | EPropertyFlags.CPF_ReturnParm | EPropertyFlags.CPF_ReferenceParm |
                                     EPropertyFlags.CPF_ConstParm | EPropertyFlags.CPF_Net | EPropertyFlags.CPF_Edit | EPropertyFlags.CPF_BlueprintVisible);
        candidate.RepIndex = 0;
        candidate.RepNotifyFunc = FName.FromString(asset, "None");
        return candidate;
    }

    #region types

    private static FProperty FromType(KismetObjects.Builder builder, string type, string path)
    {
        var colon = type.IndexOf(':');
        var kind = (colon < 0 ? type : type[..colon]).Trim().ToLowerInvariant();
        var argument = colon < 0 ? "" : type[(colon + 1)..].Trim();

        FPackageIndex? Reference(string what)
        {
            if (argument.Length == 0)
                throw new InvalidOperationException($"{path}: {kind} needs the {what}, e.g. {kind}:/Script/Engine.Actor");
            return builder.Reference(argument.StartsWith("export:", StringComparison.Ordinal) || argument.StartsWith("import:", StringComparison.Ordinal) ? argument : "import:" + argument, path);
        }

        switch (kind)
        {
            case "int": return Generic(builder, "IntProperty", 4, PlainOldData);
            case "int64": return Generic(builder, "Int64Property", 8, PlainOldData);
            case "float": return Generic(builder, "FloatProperty", 4, PlainOldData);
            case "double": return Generic(builder, "DoubleProperty", 8, PlainOldData);
            case "name": return Generic(builder, "NameProperty", 8, PlainOldData);
            case "string": return Generic(builder, "StrProperty", 16, EPropertyFlags.CPF_ZeroConstructor | EPropertyFlags.CPF_HasGetValueTypeHash);
            case "text": return Generic(builder, "TextProperty", 24, 0);
            case "bool":
                return Init(new FBoolProperty { FieldSize = 1, ByteOffset = 0, ByteMask = 0xFF, FieldMask = 0xFF, NativeBool = true, Value = true }, builder, "BoolProperty", 1, PlainOldData);
            case "byte":
                return Init(new FByteProperty { Enum = FPackageIndex.FromRawIndex(0) }, builder, "ByteProperty", 1, PlainOldData);
            case "enum":
                return Init(new FByteProperty { Enum = Reference("enum") ?? FPackageIndex.FromRawIndex(0) }, builder, "ByteProperty", 1, PlainOldData);
            case "object":
                return Init(new FObjectProperty { PropertyClass = Reference("class") ?? FPackageIndex.FromRawIndex(0) }, builder, "ObjectProperty", 8, PlainOldData);
            case "class":
                return Init(new FClassProperty
                {
                    PropertyClass = builder.Reference("import:/Script/CoreUObject.Class", path) ?? FPackageIndex.FromRawIndex(0),
                    MetaClass = Reference("meta class") ?? FPackageIndex.FromRawIndex(0),
                }, builder, "ClassProperty", 8, PlainOldData);
            case "softobject":
                return Init(new FSoftObjectProperty { PropertyClass = Reference("class") ?? FPackageIndex.FromRawIndex(0) }, builder, "SoftObjectProperty", 40,
                    EPropertyFlags.CPF_HasGetValueTypeHash);
            case "struct":
                Reference("struct");
                throw new InvalidOperationException($"{path}: a struct's size isn't known here; declare it with 'like' (a property of that struct type in this package).");
            case "array":
                if (argument.Length == 0)
                    throw new InvalidOperationException($"{path}: array needs the element type, e.g. array:int");
                return Init(new FArrayProperty { Inner = FromType(builder, argument, path + ".inner") }, builder, "ArrayProperty", 16, EPropertyFlags.CPF_ZeroConstructor);
            default:
                throw new InvalidOperationException($"{path}: unknown type '{type}' (int, int64, float, double, bool, byte, name, string, text, object:PATH, class:PATH, softobject:PATH, enum:PATH, array:TYPE, or use 'like')");
        }
    }

    private static FProperty Generic(KismetObjects.Builder builder, string type, int size, EPropertyFlags flags) => Init(new FGenericProperty(), builder, type, size, flags);

    private static FProperty Init(FProperty property, KismetObjects.Builder builder, string type, int size, EPropertyFlags flags)
    {
        var asset = builder.Asset;
        // check mode leaves the name map alone
        FName Name(string text)
        {
            if (!builder.Create)
                return FName.DefineDummy(asset, text);
            asset.AddNameReference(new FString(text));
            return FName.FromString(asset, text);
        }
        property.SerializedType = Name(type);
        property.Name = Name("None");
        property.Flags = EObjectFlags.RF_Public;
        property.ArrayDim = EArrayDim.TArray;
        property.ElementSize = size;
        property.PropertyFlags = flags;
        property.RepNotifyFunc = Name("None");
        property.BlueprintReplicationCondition = ELifetimeCondition.COND_None;
        return property;
    }

    /// <summary>A type signature: serialized type and the referenced class/struct/enum, recursively for containers.</summary>
    private static string Signature(FProperty property) => property switch
    {
        FArrayProperty array => $"Array<{Signature(array.Inner)}>",
        FClassProperty klass => $"Class<{klass.MetaClass?.Index}>",
        FObjectProperty obj => $"{property.SerializedType}<{obj.PropertyClass?.Index}>",
        FByteProperty b => $"Byte<{b.Enum?.Index}>",
        FStructProperty s => $"Struct<{s.Struct?.Index}>",
        FEnumProperty e => $"Enum<{e.Enum?.Index}>",
        FBoolProperty => "Bool",
        _ => property.SerializedType?.ToString() ?? "?",
    };

    private static FProperty? FindEquivalent(UAsset asset, FProperty candidate)
    {
        var signature = Signature(candidate);
        return asset.Exports.OfType<StructExport>().SelectMany(o => o.LoadedProperties ?? [])
            .FirstOrDefault(o => !o.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm) && Signature(o) == signature);
    }

    private static FProperty? FindProperty(UAsset asset, JToken like)
    {
        var path = (like["path"] as JArray)?.Select(o => o.ToString()).FirstOrDefault() ?? like.Value<string>("name");
        var owner = like.Value<string>("owner");
        if (path == null)
            return null;
        return asset.Exports.OfType<StructExport>()
            .Where(o => owner == null || KismetObjects.IndexSymbol(asset, FPackageIndex.FromExport(asset.Exports.IndexOf(o)))?.StartsWith(owner, StringComparison.Ordinal) == true)
            .SelectMany(o => o.LoadedProperties ?? []).FirstOrDefault(o => o.Name.ToString() == path);
    }

    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>A deep copy (inner properties of containers too).</summary>
    private static FProperty Clone(FProperty property)
    {
        var copy = (FProperty) MemberwiseCloneMethod.Invoke(property, null)!;
        foreach (var field in copy.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(o => typeof(FProperty).IsAssignableFrom(o.FieldType)))
            if (field.GetValue(copy) is FProperty inner)
                field.SetValue(copy, Clone(inner));
        copy.MetaDataMap = null;
        return copy;
    }

    /// <summary>Names the property and the inner properties of containers (the compiler names them like the property).</summary>
    private static void Rename(UAsset asset, FProperty property, string name)
    {
        asset.AddNameReference(new FString(name));
        property.Name = FName.FromString(asset, name);
        foreach (var field in property.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(o => typeof(FProperty).IsAssignableFrom(o.FieldType)))
            if (field.GetValue(property) is FProperty inner)
                Rename(asset, inner, name);
    }

    /// <summary>A short description of a declared property, e.g. "Array<IntProperty> CallFunc_Draw_cardsIDs".</summary>
    public static string Describe(FProperty property) => $"{Signature(property)} {property.Name}";

    #endregion
}

/// <summary>Exception text for the model and the user: every inner exception and the top frames of the stack.</summary>
public static class ExceptionText
{
    public static string Describe(Exception exception, int frames = 6)
    {
        var sb = new StringBuilder();
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TargetInvocationException or AggregateException && current.InnerException != null)
                continue;
            if (sb.Length > 0)
                sb.Append(" <- ");
            sb.Append($"{current.GetType().Name}: {current.Message}");
        }
        var stack = (exception.InnerException ?? exception).StackTrace?.Split('\n').Select(o => o.Trim())
            .Where(o => o.StartsWith("at ", StringComparison.Ordinal)).Take(frames).ToList();
        if (stack is { Count: > 0 })
            sb.Append("\n  ").Append(string.Join("\n  ", stack.Select(o => o.Length > 220 ? o[..220] + "…" : o)));
        return sb.ToString();
    }
}
