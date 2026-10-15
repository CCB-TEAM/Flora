using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.Utils;
using static FModel.BlueprintGraph.Engine.EngineBPData;

namespace FModel.BlueprintGraph.Signatures;

/// <summary>
/// Input parameter names of common engine functions, used to label inferred signatures.
/// Macro templates (loops, DoOnce...) match on these names, so they are needed for macro collapsing
/// when mappings carry no function signatures. Names are listed in declaration order and applied as a prefix,
/// so parameters appended by newer engine versions just keep a generic name.
/// </summary>
public static class EngineParamNames
{
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // KismetSystemLibrary
        ["KismetSystemLibrary:IsValid"] = ["Object"],
        ["KismetSystemLibrary:IsValidClass"] = ["Class"],
        ["KismetSystemLibrary:MakeLiteralInt"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralInt64"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralFloat"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralDouble"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralBool"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralByte"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralName"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralString"] = ["Value"],
        ["KismetSystemLibrary:MakeLiteralText"] = ["Value"],
        ["KismetSystemLibrary:PrintString"] = ["WorldContextObject", "InString", "bPrintToScreen", "bPrintToLog", "TextColor", "Duration", "Key"],
        ["KismetSystemLibrary:PrintText"] = ["WorldContextObject", "InText", "bPrintToScreen", "bPrintToLog", "TextColor", "Duration", "Key"],
        ["KismetSystemLibrary:Delay"] = ["WorldContextObject", "Duration", "LatentInfo"],
        ["KismetSystemLibrary:RetriggerableDelay"] = ["WorldContextObject", "Duration", "LatentInfo"],
        ["KismetSystemLibrary:GetDisplayName"] = ["Object"],
        ["KismetSystemLibrary:GetObjectName"] = ["Object"],
        ["KismetSystemLibrary:GetPathName"] = ["Object"],
        ["KismetSystemLibrary:K2_SetTimerDelegate"] = ["Delegate", "Time", "bLooping"],
        ["KismetSystemLibrary:K2_ClearAndInvalidateTimerHandle"] = ["WorldContextObject", "Handle"],

        // KismetNodeHelperLibrary (enum macros)
        ["KismetNodeHelperLibrary:GetEnumeratorValueFromIndex"] = ["Enum", "EnumeratorIndex"],
        ["KismetNodeHelperLibrary:GetValidValue"] = ["Enum", "EnumeratorValue"],
        ["KismetNodeHelperLibrary:GetEnumeratorName"] = ["Enum", "EnumeratorValue"],
        ["KismetNodeHelperLibrary:GetEnumeratorUserFriendlyName"] = ["Enum", "EnumeratorValue"],

        // KismetArrayLibrary (first parameter is always TargetArray, see GetInputNames)
        ["KismetArrayLibrary:Array_Add"] = ["TargetArray", "NewItem"],
        ["KismetArrayLibrary:Array_AddUnique"] = ["TargetArray", "NewItem"],
        ["KismetArrayLibrary:Array_Append"] = ["TargetArray", "SourceArray"],
        ["KismetArrayLibrary:Array_Insert"] = ["TargetArray", "NewItem", "Index"],
        ["KismetArrayLibrary:Array_Remove"] = ["TargetArray", "IndexToRemove"],
        ["KismetArrayLibrary:Array_RemoveItem"] = ["TargetArray", "Item"],
        ["KismetArrayLibrary:Array_Resize"] = ["TargetArray", "Size"],
        ["KismetArrayLibrary:Array_Get"] = ["TargetArray", "Index"],
        ["KismetArrayLibrary:Array_Set"] = ["TargetArray", "Index", "Item", "bSizeToFit"],
        ["KismetArrayLibrary:Array_Swap"] = ["TargetArray", "FirstIndex", "SecondIndex"],
        ["KismetArrayLibrary:Array_Find"] = ["TargetArray", "ItemToFind"],
        ["KismetArrayLibrary:Array_Contains"] = ["TargetArray", "ItemToFind"],
        ["KismetArrayLibrary:Array_IsValidIndex"] = ["TargetArray", "IndexToTest"],
        ["KismetArrayLibrary:Array_Identical"] = ["ArrayA", "ArrayB"],

        // GameplayStatics
        ["GameplayStatics:GetPlayerController"] = ["WorldContextObject", "PlayerIndex"],
        ["GameplayStatics:GetPlayerCharacter"] = ["WorldContextObject", "PlayerIndex"],
        ["GameplayStatics:GetPlayerPawn"] = ["WorldContextObject", "PlayerIndex"],
        ["GameplayStatics:GetPlayerCameraManager"] = ["WorldContextObject", "PlayerIndex"],
        ["GameplayStatics:GetGameMode"] = ["WorldContextObject"],
        ["GameplayStatics:GetGameState"] = ["WorldContextObject"],
        ["GameplayStatics:GetGameInstance"] = ["WorldContextObject"],
        ["GameplayStatics:GetObjectClass"] = ["Object"],
        ["GameplayStatics:GetAllActorsOfClass"] = ["WorldContextObject", "ActorClass"],
        ["GameplayStatics:OpenLevel"] = ["WorldContextObject", "LevelName", "bAbsolute", "Options"],
        ["GameplayStatics:PlaySound2D"] = ["WorldContextObject", "Sound", "VolumeMultiplier", "PitchMultiplier", "StartTime"],
        ["GameplayStatics:BeginDeferredActorSpawnFromClass"] = ["WorldContextObject", "ActorClass", "SpawnTransform", "CollisionHandlingOverride", "Owner", "TransformScaleMethod"],
        ["GameplayStatics:FinishSpawningActor"] = ["Actor", "SpawnTransform", "TransformScaleMethod"],
        ["GameplayStatics:SpawnObject"] = ["ObjectClass", "Outer"],

        // UMG
        ["WidgetBlueprintLibrary:Create"] = ["WorldContextObject", "WidgetType", "OwningPlayer"],
        ["UserWidget:AddToViewport"] = ["ZOrder"],
        ["Widget:SetVisibility"] = ["InVisibility"],

        // text
        ["KismetTextLibrary:Conv_StringToText"] = ["InString"],
        ["KismetTextLibrary:Format"] = ["InPattern", "InArgs"],
    };

    // KismetMathLibrary functions taking (A, B) besides the promotable operators
    private static readonly HashSet<string> BinaryAB = new(StringComparer.Ordinal)
    {
        "BooleanAND", "BooleanOR", "BooleanXOR", "BooleanNAND", "BooleanNOR",
        "Max", "Min", "FMax", "FMin",
    };

    private static readonly string[] BinaryPrefixes = [.. PromotableOperators.Keys.Select(o => o + "_"), "Percent_", "And_", "Or_", "Xor_"];

    // Conv_{From}To{To}(In{From}) holds for these source types in the math and string libraries
    private static readonly HashSet<string> ConvSourceTypes = new(StringComparer.Ordinal)
    {
        "Int", "Int64", "Byte", "Bool", "Float", "Double", "Name", "String",
    };

    // library classes whose functions are all BlueprintPure
    private static readonly HashSet<string> PureLibraries = new(StringComparer.Ordinal)
    {
        "KismetMathLibrary", "KismetStringLibrary", "KismetTextLibrary", "KismetNodeHelperLibrary",
    };

    private static readonly HashSet<string> PureFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "KismetArrayLibrary:Array_Length", "KismetArrayLibrary:Array_LastIndex", "KismetArrayLibrary:Array_Get",
        "KismetArrayLibrary:Array_Find", "KismetArrayLibrary:Array_Contains", "KismetArrayLibrary:Array_IsValidIndex",
        "KismetArrayLibrary:Array_IsEmpty", "KismetArrayLibrary:Array_IsNotEmpty", "KismetArrayLibrary:Array_Identical",
        "KismetSystemLibrary:IsValid", "KismetSystemLibrary:IsValidClass", "KismetSystemLibrary:GetDisplayName",
        "KismetSystemLibrary:GetObjectName", "KismetSystemLibrary:GetPathName",
        "GameplayStatics:GetPlayerController", "GameplayStatics:GetPlayerCharacter", "GameplayStatics:GetPlayerPawn",
        "GameplayStatics:GetPlayerCameraManager", "GameplayStatics:GetGameMode", "GameplayStatics:GetGameState",
        "GameplayStatics:GetGameInstance", "GameplayStatics:GetObjectClass",
    };

    /// <summary>
    /// Purity can't be told from bytecode (pure calls are compiled as regular statements), so it comes from known engine functions.
    /// </summary>
    public static bool IsKnownPure(string outerPath, string funcName)
    {
        var className = ClassName(outerPath);
        return PureLibraries.Contains(className) ||
               PureFunctions.Contains($"{className}:{funcName}") ||
               className == "KismetSystemLibrary" && funcName.StartsWith("MakeLiteral", StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex PureGetterName =
        new(@"^(K2_)?(Get|Is|Has|Can)[A-Z0-9]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Name based guess for inferred signatures only (e.g. GetChildrenComponents, IsValid, K2_GetActorLocation).
    /// "GetAll..." world queries (GetAllActorsOfClass, GetAllWidgetsOfClass) are callable, not pure.
    /// </summary>
    public static bool LooksLikePureGetter(string funcName) =>
        PureGetterName.IsMatch(funcName) && !funcName.StartsWith("GetAll", StringComparison.Ordinal);

    private static string ClassName(string outerPath) => outerPath.Contains('.') ? outerPath.SubstringAfterLast('.') : outerPath;

    public static string[]? GetInputNames(string outerPath, string funcName)
    {
        var className = ClassName(outerPath);
        if (Known.TryGetValue($"{className}:{funcName}", out var names))
            return names;

        switch (className)
        {
            case "KismetMathLibrary":
                if (funcName.StartsWith("Not_", StringComparison.Ordinal))
                    return ["A"];
                if (BinaryAB.Contains(funcName) || BinaryPrefixes.Any(p => funcName.StartsWith(p, StringComparison.Ordinal)))
                    return ["A", "B"];
                return ConvName(funcName);
            case "KismetStringLibrary":
                return ConvName(funcName);
            case "KismetArrayLibrary" when funcName.StartsWith("Array_", StringComparison.Ordinal):
                return ["TargetArray"];
            default:
                return null;
        }
    }

    private static string[]? ConvName(string funcName)
    {
        if (!funcName.StartsWith("Conv_", StringComparison.Ordinal))
            return null;

        var toIndex = funcName.IndexOf("To", "Conv_".Length, StringComparison.Ordinal);
        if (toIndex <= "Conv_".Length)
            return null;

        var source = funcName["Conv_".Length..toIndex];
        return ConvSourceTypes.Contains(source) ? ["In" + source] : null;
    }
}
