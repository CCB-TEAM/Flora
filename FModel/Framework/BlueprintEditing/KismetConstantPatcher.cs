using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>
/// A literal constant of a function's bytecode to rewrite. Offsets are in-memory bytecode offsets, as reported by
/// CUE4Parse (KismetExpression.StatementIndex) and UAssetAPI (KismetExpression.Visit) alike.
/// </summary>
public sealed class ConstantEdit
{
    public string FunctionName { get; init; } = "";
    public uint Offset { get; init; }
    public string ExpectedToken { get; init; } = "";
    public string NewValue { get; init; } = "";

    public override string ToString() => $"{FunctionName}@{Offset} ({ExpectedToken}) = {NewValue}";
}

/// <summary>
/// Rewrites literal constants in UAssetAPI bytecode and relocates every code offset that depends on the size change:
/// absolute targets (jumps, execution flow pushes, switch cases, latent resume points, ubergraph entry points of event stubs)
/// and relative skips (context/skip expressions containing an edited constant).
/// </summary>
public static class KismetConstantPatcher
{
    private sealed record AppliedEdit(uint Offset, uint OldSize, uint NewSize)
    {
        public long Delta => (long) NewSize - OldSize;
        public uint End => Offset + OldSize;
    }

    public static void Apply(UAsset asset, IReadOnlyCollection<ConstantEdit> edits)
    {
        foreach (var group in edits.GroupBy(o => o.FunctionName, StringComparer.Ordinal))
        {
            var function = FindFunction(asset, group.Key)
                           ?? throw new InvalidOperationException($"Function '{group.Key}' has no parsed bytecode in the asset.");
            var applied = ApplyToFunction(asset, function, group.ToList());
            if (applied.Count == 0 || applied.All(o => o.Delta == 0))
                continue;

            // event stubs call the ubergraph with the entry point offset of their event
            if (function.ObjectName.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal))
                RelocateUbergraphEntryPoints(asset, function, applied);
        }
    }

    public static StructExport? FindFunction(UAsset asset, string name) =>
        asset.Exports.OfType<StructExport>().FirstOrDefault(o => o.ScriptBytecode != null && o.ObjectName.ToString() == name);

    private static List<AppliedEdit> ApplyToFunction(UAsset asset, StructExport function, List<ConstantEdit> edits)
    {
        // old in-memory offsets of every expression, before anything changes
        var offsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
        var byOffset = new Dictionary<uint, List<KismetExpression>>();
        uint position = 0;
        foreach (var statement in function.ScriptBytecode)
        {
            statement.Visit(asset, ref position, (expression, offset) =>
            {
                offsets[expression] = offset;
                if (!byOffset.TryGetValue(offset, out var list))
                    byOffset[offset] = list = [];
                list.Add(expression);
            });
        }

        var applied = new List<AppliedEdit>();
        var replacements = new Dictionary<KismetExpression, KismetExpression>(ReferenceEqualityComparer.Instance);
        foreach (var edit in edits.GroupBy(o => o.Offset).Select(o => o.Last()))
        {
            var target = byOffset.GetValueOrDefault(edit.Offset)?.FirstOrDefault(o => o.Token.ToString() == edit.ExpectedToken)
                         ?? throw new InvalidOperationException(
                             $"No {edit.ExpectedToken} at offset {edit.Offset} in {function.ObjectName}: the bytecode differs from the decompiled graph.");

            var oldSize = target.GetSize(asset);
            var replacement = BuildReplacement(asset, target, edit.NewValue);
            if (!ReferenceEquals(replacement, target))
                replacements[target] = replacement;
            applied.Add(new AppliedEdit(edit.Offset, oldSize, replacement.GetSize(asset)));
        }

        if (replacements.Count > 0)
            ReplaceExpressions(function, offsets.Keys, replacements);

        if (applied.Any(o => o.Delta != 0))
            RelocateFunction(asset, offsets, applied);

        return applied;
    }

    #region constants

    private static KismetExpression BuildReplacement(UAsset asset, KismetExpression target, string text)
    {
        var inv = CultureInfo.InvariantCulture;
        text = target is EX_StringConst or EX_UnicodeStringConst ? text : text.Trim();
        switch (target)
        {
            case EX_True or EX_False:
                return bool.Parse(text) ? new EX_True() : new EX_False();

            case EX_IntConst intConst:
                intConst.Value = int.Parse(text, NumberStyles.Integer, inv);
                return intConst;
            case EX_IntZero or EX_IntOne:
            {
                var value = int.Parse(text, NumberStyles.Integer, inv);
                if (value == 0) return target is EX_IntZero ? target : new EX_IntZero();
                if (value == 1) return target is EX_IntOne ? target : new EX_IntOne();
                return new EX_IntConst { Value = value };
            }
            case EX_Int64Const int64Const:
                int64Const.Value = long.Parse(text, NumberStyles.Integer, inv);
                return int64Const;
            case EX_UInt64Const uint64Const:
                uint64Const.Value = ulong.Parse(text, NumberStyles.Integer, inv);
                return uint64Const;
            case EX_FloatConst floatConst:
                floatConst.Value = float.Parse(text, NumberStyles.Float, inv);
                return floatConst;
            case EX_DoubleConst doubleConst:
                doubleConst.Value = double.Parse(text, NumberStyles.Float, inv);
                return doubleConst;
            case EX_ByteConst byteConst:
                byteConst.Value = byte.Parse(text, NumberStyles.Integer, inv);
                return byteConst;
            case EX_IntConstByte intConstByte:
                intConstByte.Value = byte.Parse(text, NumberStyles.Integer, inv);
                return intConstByte;

            case EX_NameConst nameConst:
                asset.AddNameReference(new FString(text));
                nameConst.Value = FName.FromString(asset, text);
                return nameConst;

            case EX_StringConst or EX_UnicodeStringConst:
            {
                // ANSI strings can't hold other characters: switch to the UTF-16 form when needed
                var isAnsi = text.All(c => c < 128);
                if (isAnsi && target is EX_StringConst ansi) { ansi.Value = text; return ansi; }
                if (!isAnsi && target is EX_UnicodeStringConst unicode) { unicode.Value = text; return unicode; }
                return isAnsi ? new EX_StringConst { Value = text } : new EX_UnicodeStringConst { Value = text };
            }

            default:
                throw new InvalidOperationException($"{target.Token} constants can't be edited.");
        }
    }

    // swap expression objects wherever they are referenced (statement array, expression fields, arrays, switch cases)
    private static void ReplaceExpressions(StructExport function, IEnumerable<KismetExpression> expressions,
        Dictionary<KismetExpression, KismetExpression> replacements)
    {
        for (var i = 0; i < function.ScriptBytecode.Length; i++)
        {
            if (replacements.TryGetValue(function.ScriptBytecode[i], out var top))
                function.ScriptBytecode[i] = top;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var expression in expressions.ToList())
            ReplaceInObject(expression, replacements, visited);
    }

    private static void ReplaceInObject(object holder, Dictionary<KismetExpression, KismetExpression> replacements, HashSet<object> visited)
    {
        if (!visited.Add(holder))
            return;

        foreach (var field in holder.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var value = field.GetValue(holder);
            switch (value)
            {
                case KismetExpression expression when replacements.TryGetValue(expression, out var replacement):
                    field.SetValue(holder, replacement);
                    break;
                case KismetExpression[] array:
                    for (var i = 0; i < array.Length; i++)
                    {
                        if (array[i] != null && replacements.TryGetValue(array[i], out var element))
                            array[i] = element;
                    }
                    break;
                case FKismetSwitchCase[] cases:
                    for (var i = 0; i < cases.Length; i++)
                    {
                        var @case = cases[i];
                        if (@case.CaseIndexValueTerm != null && replacements.TryGetValue(@case.CaseIndexValueTerm, out var index))
                            @case.CaseIndexValueTerm = index;
                        if (@case.CaseTerm != null && replacements.TryGetValue(@case.CaseTerm, out var term))
                            @case.CaseTerm = term;
                        cases[i] = @case;
                    }
                    break;
                case object nested when nested.GetType().Namespace?.StartsWith("UAssetAPI.Kismet", StringComparison.Ordinal) == true &&
                                        !nested.GetType().IsValueType && nested is not KismetExpression:
                    ReplaceInObject(nested, replacements, visited); // e.g. text constants' FScriptText
                    break;
            }
        }

        // generic constants keep their value in RawValue (e.g. EX_StructConst elements)
        if (holder is KismetExpression { RawValue: KismetExpression[] rawArray })
        {
            for (var i = 0; i < rawArray.Length; i++)
            {
                if (rawArray[i] != null && replacements.TryGetValue(rawArray[i], out var element))
                    rawArray[i] = element;
            }
        }
    }

    #endregion

    #region relocation

    private static uint Shift(uint offset, IReadOnlyList<AppliedEdit> edits)
    {
        long shifted = offset;
        foreach (var edit in edits)
        {
            if (edit.End <= offset)
                shifted += edit.Delta;
        }
        return checked((uint) shifted);
    }

    private static long DeltaInside(uint start, uint end, IReadOnlyList<AppliedEdit> edits) =>
        edits.Where(o => o.Offset >= start && o.End <= end).Sum(o => o.Delta);

    private static void RelocateFunction(UAsset asset, Dictionary<KismetExpression, uint> oldOffsets, List<AppliedEdit> edits)
    {
        foreach (var (expression, _) in oldOffsets)
        {
            switch (expression)
            {
                case EX_Jump jump:
                    jump.CodeOffset = Shift(jump.CodeOffset, edits);
                    break;
                case EX_JumpIfNot jumpIfNot:
                    jumpIfNot.CodeOffset = Shift(jumpIfNot.CodeOffset, edits);
                    break;
                case EX_PushExecutionFlow push:
                    push.PushingAddress = Shift(push.PushingAddress, edits);
                    break;
                case EX_SkipOffsetConst skipConst:
                    skipConst.Value = Shift(skipConst.Value, edits);
                    break;
                case EX_SwitchValue switchValue:
                    switchValue.EndGotoOffset = Shift(switchValue.EndGotoOffset, edits);
                    for (var i = 0; i < switchValue.Cases.Length; i++)
                    {
                        var @case = switchValue.Cases[i];
                        @case.NextOffset = Shift(@case.NextOffset, edits);
                        switchValue.Cases[i] = @case;
                    }
                    break;
                case EX_Context context when context.ContextExpression != null && oldOffsets.TryGetValue(context.ContextExpression, out var contextStart):
                    // relative: bytes to skip over the context expression when the object is null
                    context.Offset = checked((uint) (context.Offset + DeltaInside(contextStart, contextStart + context.Offset, edits)));
                    break;
                case EX_Skip skip when skip.SkipExpression != null && oldOffsets.TryGetValue(skip.SkipExpression, out var skipStart):
                    skip.CodeOffset = checked((uint) (skip.CodeOffset + DeltaInside(skipStart, skipStart + skip.CodeOffset, edits)));
                    break;
                case EX_StructConst structConst when IsLatentActionInfo(asset, structConst) &&
                                                      structConst.Value is { Length: > 0 } fields && fields[0] is EX_IntConst { Value: >= 0 } linkage:
                    // pre-4.18 cooks store the latent resume point as a plain int
                    linkage.Value = checked((int) Shift((uint) linkage.Value, edits));
                    break;
            }
        }
    }

    private static bool IsLatentActionInfo(UAsset asset, EX_StructConst structConst)
    {
        var index = structConst.Struct;
        if (index == null || index.IsNull()) return false;
        var name = index.IsImport() ? index.ToImport(asset).ObjectName.ToString() : index.ToExport(asset).ObjectName.ToString();
        return name == "LatentActionInfo";
    }

    private static void RelocateUbergraphEntryPoints(UAsset asset, StructExport ubergraph, List<AppliedEdit> edits)
    {
        var ubergraphName = ubergraph.ObjectName.ToString();
        foreach (var function in asset.Exports.OfType<StructExport>())
        {
            if (function == ubergraph || function.ScriptBytecode == null)
                continue;

            uint position = 0;
            foreach (var statement in function.ScriptBytecode)
            {
                statement.Visit(asset, ref position, (expression, _) =>
                {
                    var (calls, parameters) = expression switch
                    {
                        EX_FinalFunction final => (CallsExport(asset, final.StackNode, ubergraphName), final.Parameters),
                        EX_VirtualFunction virtualCall => (virtualCall.VirtualFunctionName?.ToString() == ubergraphName, virtualCall.Parameters),
                        _ => (false, null),
                    };
                    if (calls && parameters is { Length: > 0 } && parameters[0] is EX_IntConst { Value: >= 0 } entryPoint)
                        entryPoint.Value = checked((int) Shift((uint) entryPoint.Value, edits));
                });
            }
        }
    }

    private static bool CallsExport(UAsset asset, FPackageIndex? index, string name) =>
        index is { } && index.IsExport() && index.ToExport(asset).ObjectName.ToString() == name;

    #endregion

    #region validation

    /// <summary>
    /// Checks that every absolute code offset of the function lands on a statement (or switch case) boundary.
    /// Run on the patched asset before writing it.
    /// </summary>
    public static List<string> Validate(UAsset asset, StructExport function)
    {
        var problems = new List<string>();
        var statementStarts = new HashSet<uint>();
        var offsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
        uint position = 0;
        foreach (var statement in function.ScriptBytecode)
        {
            statementStarts.Add(position);
            statement.Visit(asset, ref position, (expression, offset) => offsets[expression] = offset);
        }
        statementStarts.Add(position);

        void Check(uint target, string what)
        {
            if (!statementStarts.Contains(target))
                problems.Add($"{function.ObjectName}: {what} points to {target}, which is not a statement boundary");
        }

        foreach (var (expression, offset) in offsets)
        {
            switch (expression)
            {
                case EX_Jump jump: Check(jump.CodeOffset, $"jump at {offset}"); break;
                case EX_JumpIfNot jumpIfNot: Check(jumpIfNot.CodeOffset, $"conditional jump at {offset}"); break;
                case EX_PushExecutionFlow push: Check(push.PushingAddress, $"execution flow push at {offset}"); break;
                case EX_SkipOffsetConst skipConst: Check(skipConst.Value, $"latent resume point at {offset}"); break;
                case EX_SwitchValue switchValue:
                {
                    var end = offset + switchValue.GetSize(asset);
                    if (switchValue.EndGotoOffset != end)
                        problems.Add($"{function.ObjectName}: switch at {offset} ends at {end} but jumps to {switchValue.EndGotoOffset}");
                    for (var i = 0; i < switchValue.Cases.Length; i++)
                    {
                        var next = i + 1 < switchValue.Cases.Length ? switchValue.Cases[i + 1].CaseIndexValueTerm : switchValue.DefaultTerm;
                        if (next != null && offsets.TryGetValue(next, out var nextOffset) && switchValue.Cases[i].NextOffset != nextOffset)
                            problems.Add($"{function.ObjectName}: switch case {i} at {offset} points to {switchValue.Cases[i].NextOffset} instead of {nextOffset}");
                    }
                    break;
                }
            }
        }
        return problems;
    }

    /// <summary>Entry point offsets passed by event stubs must be statements of the ubergraph.</summary>
    public static List<string> ValidateUbergraphEntryPoints(UAsset asset)
    {
        var problems = new List<string>();
        var ubergraph = asset.Exports.OfType<StructExport>()
            .FirstOrDefault(o => o.ScriptBytecode != null && o.ObjectName.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal));
        if (ubergraph == null)
            return problems;

        var starts = new HashSet<uint>();
        uint position = 0;
        foreach (var statement in ubergraph.ScriptBytecode)
        {
            starts.Add(position);
            statement.Visit(asset, ref position, (_, _) => { });
        }

        var name = ubergraph.ObjectName.ToString();
        foreach (var function in asset.Exports.OfType<StructExport>().Where(o => o != ubergraph && o.ScriptBytecode != null))
        {
            uint p = 0;
            foreach (var statement in function.ScriptBytecode)
            {
                statement.Visit(asset, ref p, (expression, _) =>
                {
                    if (expression is EX_FinalFunction final && CallsExport(asset, final.StackNode, name) &&
                        final.Parameters is { Length: > 0 } && final.Parameters[0] is EX_IntConst entry && !starts.Contains((uint) entry.Value))
                        problems.Add($"{function.ObjectName}: ubergraph entry point {entry.Value} is not a statement boundary");
                });
            }
        }
        return problems;
    }

    #endregion
}
