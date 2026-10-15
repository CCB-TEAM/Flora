using System;
using System.Collections.Generic;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FModel.BlueprintGraph.Engine;

namespace FModel.BlueprintGraph.Signatures;

/// <summary>
/// Native properties and enums from the loaded type mappings (.usmap or .jmap).
/// Mappings carry no function signatures, so function lookups always miss.
/// </summary>
public class MappingsSignatureProvider : ISignatureProvider
{
    private const string None = "None";

    private readonly TypeMappings _mappings;

    public MappingsSignatureProvider(TypeMappings mappings)
    {
        _mappings = mappings;
    }

    public FunctionData? FindFunctionData(string objPath, string funcName) => null;

    public EnumData? TryFindEnum(string name)
    {
        if (string.IsNullOrEmpty(name) || !_mappings.Enums.TryGetValue(ShortName(name), out var values))
            return null;

        var data = new EnumData(name);
        foreach (var (value, elementName) in values)
            data.Elements.TryAdd(elementName, value);
        return data;
    }

    public bool TryFindProperty(string name, FPackageIndex objIndex, out PropertyData? prop)
    {
        prop = null;
        return objIndex.ResolvedObject?.GetPathName() is { } path && TryFindProperty(name, path, out prop);
    }

    public bool TryFindProperty(string name, string objPath, out PropertyData? prop)
    {
        prop = null;
        var type = FindStruct(objPath);
        while (type != null)
        {
            foreach (var info in type.Properties.Values)
            {
                if (info.Index == 0 && info.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    prop = ToPropertyData(info, objPath);
                    return true;
                }
            }
            type = type.Super.Value;
        }
        return false;
    }

    public bool TryFindProperties(FPackageIndex objIndex, out List<PropertyData>? props)
    {
        props = [];
        var path = objIndex.ResolvedObject?.GetPathName();
        var type = path == null ? null : FindStruct(path);
        if (type == null)
            return false;

        while (type != null)
        {
            foreach (var info in type.Properties.Values)
            {
                if (info.Index == 0)
                    props.Add(ToPropertyData(info, path!));
            }
            type = type.Super.Value;
        }
        return true;
    }

    private Struct? FindStruct(string objPath)
    {
        return _mappings.Types.GetValueOrDefault(ShortName(objPath));
    }

    private static string ShortName(string path) => path.Contains('.') ? path.SubstringAfterLast('.') : path;

    private static PropertyData ToPropertyData(PropertyInfo info, string owner)
    {
        var type = info.MappingType;
        var className = type.StructType ?? type.EnumName ?? type.InnerType?.StructType ?? type.InnerType?.EnumName ?? None;
        var isMap = type.Type == "MapProperty";
        return new PropertyData(
            info.Name,
            owner,
            type.Type,
            0,
            className,
            isMap ? None : type.InnerType?.Type ?? None,
            isMap ? type.InnerType?.Type ?? None : None,
            isMap ? type.ValueType?.Type ?? None : None,
            "",
            "");
    }
}

/// <summary>
/// Queries several providers in order and returns the first hit.
/// </summary>
public class CompositeSignatureProvider : ISignatureProvider
{
    private readonly IReadOnlyList<ISignatureProvider> _providers;

    public CompositeSignatureProvider(IEnumerable<ISignatureProvider?> providers)
    {
        var list = new List<ISignatureProvider>();
        foreach (var provider in providers)
        {
            if (provider != null)
                list.Add(provider);
        }
        _providers = list;
    }

    public FunctionData? FindFunctionData(string objPath, string funcName)
    {
        foreach (var provider in _providers)
        {
            if (provider.FindFunctionData(objPath, funcName) is { } func)
                return func;
        }
        return null;
    }

    public EnumData? TryFindEnum(string name)
    {
        foreach (var provider in _providers)
        {
            if (provider.TryFindEnum(name) is { } data)
                return data;
        }
        return null;
    }

    public bool TryFindProperty(string name, FPackageIndex objIndex, out PropertyData? prop)
    {
        foreach (var provider in _providers)
        {
            if (provider.TryFindProperty(name, objIndex, out prop))
                return true;
        }
        prop = null;
        return false;
    }

    public bool TryFindProperty(string name, string objPath, out PropertyData? prop)
    {
        foreach (var provider in _providers)
        {
            if (provider.TryFindProperty(name, objPath, out prop))
                return true;
        }
        prop = null;
        return false;
    }

    public bool TryFindProperties(FPackageIndex objIndex, out List<PropertyData>? props)
    {
        foreach (var provider in _providers)
        {
            if (provider.TryFindProperties(objIndex, out props))
                return true;
        }
        props = null;
        return false;
    }
}
