using System;
using System.Linq;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using FModel.BlueprintGraph.Decompiler;
using FModel.BlueprintGraph.Engine;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.PropertiesUtils;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Signatures;

/// <summary>
/// Builds a best-effort function signature from a call site when no mappings know the function
/// (e.g. native functions with .usmap-only mappings).
/// The blueprint compiler stores out parameters in locals named "CallFunc_{Function}_{Param}",
/// which gives both the direction and the original name of those parameters.
/// </summary>
public static class SignatureInference
{
    public const string ReturnValueName = "ReturnValue";

    /// <summary>Return slot of unknown type, for calls whose result feeds another expression directly.</summary>
    public static readonly PropertyData UnknownReturnValue = MakeParam(ReturnValueName, "", EPropertyFlags.Parm | EPropertyFlags.OutParm | EPropertyFlags.ReturnParm,
        MakePinType(PinType.Unknown));

    public static FunctionData Infer(string outerPath, string funcName, KismetExpression[] parameters,
        PropertyData? returnProperty, bool isPure, GlobalDecompilerContext global)
    {
        var flags = EFunctionFlags.FUNC_Native;
        if (isPure || EngineParamNames.IsKnownPure(outerPath, funcName))
            flags |= EFunctionFlags.FUNC_BlueprintPure;

        var func = new FunctionData(funcName, flags)
        {
            Outer = new ObjectData { Name = outerPath },
            IsInferred = true,
        };

        var outPrefix = $"CallFunc_{funcName}_";
        var knownInputNames = EngineParamNames.GetInputNames(outerPath, funcName);
        var inputIndex = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var expr = parameters[i];
            var varProp = TryGetVariableProperty(expr, global);

            // only this call's own output temps are out parameters, "CallFunc_Other_X" is another node's output passed in
            if (varProp != null && expr is EX_LocalVariable or EX_LocalOutVariable &&
                varProp.Name.StartsWith(outPrefix, StringComparison.Ordinal))
            {
                func.Params.Add(MakeParam(varProp.Name[outPrefix.Length..], outerPath, EPropertyFlags.Parm | EPropertyFlags.OutParm, varProp.PinType));
                continue;
            }

            GraphPinType pinType;
            if (ParseConstExpr(expr, global.Game, out _, out var constType))
                pinType = constType;
            else if (varProp != null)
                pinType = varProp.PinType;
            else
                pinType = MakePinType(PinType.Unknown);
            pinType.IsReference = false;

            var inputName = knownInputNames != null && inputIndex < knownInputNames.Length
                ? knownInputNames[inputIndex]
                : i == 0 && expr is EX_Self ? "Target" : $"Param{i}";
            inputIndex++;
            func.Params.Add(MakeParam(inputName, outerPath, EPropertyFlags.Parm, pinType));
        }

        if (returnProperty != null)
        {
            func.Params.Add(MakeParam(ReturnValueName, outerPath,
                EPropertyFlags.Parm | EPropertyFlags.OutParm | EPropertyFlags.ReturnParm, returnProperty.PinType));
        }

        // getters producing a single value (return value or one out parameter, e.g. GetChildrenComponents)
        // are BlueprintPure in the vast majority of engine/game code
        if (!func.IsPure && EngineParamNames.LooksLikePureGetter(funcName) &&
            func.Params.Count(o => o.Flags.HasFlag(EPropertyFlags.OutParm)) == 1)
            func.Flags |= EFunctionFlags.FUNC_BlueprintPure;

        return func;
    }

    private static PropertyData? TryGetVariableProperty(KismetExpression expr, GlobalDecompilerContext global)
    {
        if (expr is not EX_VariableBase)
            return null;

        try
        {
            return VarInstrToProperty(expr, global);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PropertyData MakeParam(string name, string owner, EPropertyFlags flags, GraphPinType pinType)
    {
        pinType.IsReference = false;
        return new PropertyData((PropertyContainer?) null, null)
        {
            Name = name,
            Owner = owner,
            Flags = flags,
            PinType = pinType,
        };
    }
}
