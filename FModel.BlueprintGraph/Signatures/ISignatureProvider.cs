using System.Collections.Generic;
using CUE4Parse.UE4.Objects.UObject;
using FModel.BlueprintGraph.Decompiler;
using FModel.BlueprintGraph.Engine;

namespace FModel.BlueprintGraph.Signatures;

/// <summary>
/// Source of native (/Script/...) type information the decompiler can't read from cooked packages:
/// function signatures, native class/struct properties and enums.
/// </summary>
public interface ISignatureProvider
{
    FunctionData? FindFunctionData(string objPath, string funcName);

    EnumData? TryFindEnum(string name);

    bool TryFindProperty(string name, FPackageIndex objIndex, out PropertyData? prop);

    bool TryFindProperty(string name, string objPath, out PropertyData? prop);

    bool TryFindProperties(FPackageIndex objIndex, out List<PropertyData>? props);
}

public static class SignatureProviderExtensions
{
    public static FunctionData GetFunctionData(this ISignatureProvider provider, string objPath, string funcName)
    {
        return provider.FindFunctionData(objPath, funcName)
               ?? throw new DecompilerException($"Failed to find signature of function {funcName} in object {objPath}");
    }
}
