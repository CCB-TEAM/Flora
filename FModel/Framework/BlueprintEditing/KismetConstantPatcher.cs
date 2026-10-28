using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
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

public enum ObjectEditKind
{
    /// <summary>The expression (or statement) at the offset is replaced by the built object.</summary>
    Replace,

    /// <summary>The statements are inserted right after the statement at the offset.</summary>
    InsertAfter,

    /// <summary>The statement at the offset becomes EX_Nothing (offsets pointing at it stay valid).</summary>
    Remove,
}

/// <summary>
/// A bytecode object edit: Kismet expressions written as <see cref="KismetObjects"/> JSON, applied to the original bytecode.
/// </summary>
public sealed class ObjectEdit
{
    public string FunctionName { get; init; } = "";
    public ObjectEditKind Kind { get; init; }

    /// <summary>The original offset of the replaced expression, or of the statement to insert after / remove.</summary>
    public uint Offset { get; init; }
    public string Token { get; init; } = "";

    /// <summary>Replace: one expression object. InsertAfter: an array of statement objects. Remove: null.</summary>
    public Newtonsoft.Json.Linq.JToken? Payload { get; init; }

    /// <summary>What the agent said the edit does (shown in the Changes list).</summary>
    public string Description { get; init; } = "";

    public (string Function, ObjectEditKind Kind, uint Offset) Key => (FunctionName, Kind == ObjectEditKind.Remove ? ObjectEditKind.Replace : Kind, Offset);

    public override string ToString() => $"{FunctionName}@{Offset} {Kind}";
}

/// <summary>
/// Rewrites literal constants in UAssetAPI bytecode. The code offsets that depend on the size change (jumps, execution flow pushes,
/// switch cases, latent resume points, ubergraph entry points, context skips) are laid out again by UAssetAPI's <see cref="KismetCodeLayout"/>.
/// </summary>
public static class KismetConstantPatcher
{
    public static void Apply(UAsset asset, IReadOnlyCollection<ConstantEdit> edits) => Apply(asset, edits, [], []);

    public static void Apply(UAsset asset, IReadOnlyCollection<ConstantEdit> constants, IReadOnlyCollection<WireEdit> wires) => Apply(asset, constants, wires, []);

    /// <summary>
    /// Applies constant, wire and bytecode object edits; each function is bound once, against the bytecode the graph was decompiled from.
    /// Returns notes about what the object edits added to the package (names, imports).
    /// </summary>
    public static List<string> Apply(UAsset asset, IReadOnlyCollection<ConstantEdit> constants, IReadOnlyCollection<WireEdit> wires, IReadOnlyCollection<ObjectEdit> objects)
    {
        var notes = new List<string>();
        var functionNames = constants.Select(o => o.FunctionName).Concat(wires.Select(o => o.FunctionName)).Concat(objects.Select(o => o.FunctionName))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var functionName in functionNames)
        {
            var function = FindFunction(asset, functionName)
                           ?? throw new InvalidOperationException($"Function '{functionName}' has no parsed bytecode in the asset.");

            // offsets are bound before anything changes: the edits refer to the bytecode the graph was decompiled from
            var layout = KismetCodeLayout.Bind(asset, function);

            KismetExpression Find(uint offset, string token) =>
                (Enum.TryParse<EExprToken>(token, out var parsed) ? layout.FindOriginal(offset, parsed) : null)
                ?? throw new InvalidOperationException($"No {token} at offset {offset} in {function.ObjectName}: the bytecode differs from the decompiled graph.");

            // find every target first: replacing and inserting changes what the original offsets point at
            var functionWires = wires.Where(o => o.FunctionName == functionName).ToList();
            var dataTargets = functionWires.Where(o => o.Kind is WireEditKind.ConnectData or WireEditKind.LiteralData)
                .Select(o => (Edit: o, Target: Find(o.Offset, o.Token), Source: o.Kind == WireEditKind.ConnectData ? Find(o.SourceOffset, o.SourceToken) : null))
                .ToList();
            var execTargets = functionWires.Where(o => o.Kind is WireEditKind.ConnectExec or WireEditKind.BreakExec)
                .Select(o => (Edit: o, Statement: Find(o.Offset, o.Token),
                    Target: o.Kind == WireEditKind.ConnectExec || o.EndWithReturn ? layout.FindOriginal(o.TargetOffset) : null))
                .ToList();

            // events: the stubs pass the new start of the event graph (found before anything changes)
            var entryEdits = functionWires.Where(o => o.Kind is WireEditKind.ConnectEntry or WireEditKind.BreakEntry).ToList();
            if (entryEdits.Count > 0)
            {
                var entryPoints = KismetCodeLayout.FindUbergraphEntryPoints(asset, function).ToList();
                foreach (var edit in entryEdits)
                {
                    KismetExpression target;
                    if (edit.TargetOffset == WirePlanner.NewEnd)
                    {
                        var code = function.ScriptBytecode;
                        target = new EX_PopExecutionFlow();
                        layout.InsertAfter(code.Length > 1 && code[^1] is EX_EndOfScript ? code[^2] : code[^1], target);
                    }
                    else
                        target = layout.FindOriginal(edit.TargetOffset)
                                 ?? throw new InvalidOperationException($"No statement at offset {edit.TargetOffset} in {function.ObjectName} to start the event at.");
                    var stubs = entryPoints.Where(o => o.EntryPoint.Value == edit.Offset).ToList();
                    if (stubs.Count == 0)
                        throw new InvalidOperationException($"No event passes entry point {edit.Offset} to {function.ObjectName}: the bytecode differs from the decompiled graph.");
                    foreach (var stub in stubs)
                        layout.RedirectEntryPoint(stub.EntryPoint, target);
                }
            }

            // bytecode objects: targets are found and the objects built before anything changes (building may add names and imports)
            var builder = new KismetObjects.Builder(asset, true); // locals are declared in the package by now
            var objectTargets = objects.Where(o => o.FunctionName == functionName)
                .Select(o =>
                {
                    var target = Find(o.Offset, o.Token);
                    if (o.Kind != ObjectEditKind.Replace && !layout.IsStatement(target))
                        throw new InvalidOperationException($"{o.Token} at {o.Offset} in {function.ObjectName} is not a statement.");
                    var built = o.Kind switch
                    {
                        ObjectEditKind.Replace => [builder.Build(o.Payload!, o.ToString())],
                        ObjectEditKind.InsertAfter => ((Newtonsoft.Json.Linq.JArray) o.Payload!).Select((s, i) => builder.Build(s, $"{o}[{i}]")).ToArray(),
                        _ => new KismetExpression[] { new EX_Nothing() },
                    };
                    return (Edit: o, Target: target, Built: built);
                })
                .ToList();
            notes.AddRange(builder.Notes);

            // an edit may not change what another edit of the same save rewrites
            var rewritten = objectTargets.Where(o => o.Edit.Kind != ObjectEditKind.InsertAfter).Select(o => o.Target)
                .Concat(dataTargets.Select(o => o.Target)).ToList();
            for (var i = 0; i < rewritten.Count; i++)
                for (var j = 0; j < rewritten.Count; j++)
                    if (i != j && ContainsExpression(asset, rewritten[i], rewritten[j]))
                        throw new InvalidOperationException($"{function.ObjectName}: two edits rewrite overlapping bytecode (the {rewritten[j].Token} inside the {rewritten[i].Token}); revert one of them.");

            var replacements = new Dictionary<KismetExpression, KismetExpression>(ReferenceEqualityComparer.Instance);
            foreach (var edit in constants.Where(o => o.FunctionName == functionName).GroupBy(o => o.Offset).Select(o => o.Last()))
            {
                var target = Find(edit.Offset, edit.ExpectedToken);
                if (dataTargets.Any(o => ContainsExpression(asset, o.Target, target)) ||
                    objectTargets.Any(o => o.Edit.Kind != ObjectEditKind.InsertAfter && ContainsExpression(asset, o.Target, target)))
                    continue; // the argument holding the constant is replaced by a wire or object edit
                var replacement = BuildReplacement(asset, target, edit.NewValue);
                if (!ReferenceEquals(replacement, target))
                    replacements[target] = replacement;
            }

            foreach (var (edit, target, source) in dataTargets)
            {
                replacements[target] = edit.Kind == WireEditKind.ConnectData
                    ? CloneVariable(source!)
                    : BuildReplacement(asset, CreateLiteral(edit.LiteralKind), edit.LiteralText);
            }
            foreach (var (_, target, built) in objectTargets.Where(o => o.Edit.Kind != ObjectEditKind.InsertAfter))
                replacements[target] = built[0];
            layout.Replace(replacements);

            foreach (var (edit, statement, target) in execTargets)
            {
                if (!layout.IsStatement(statement))
                    throw new InvalidOperationException($"{edit.Token} at {edit.Offset} in {function.ObjectName} is not a statement.");

                KismetExpression inserted;
                if (edit.Kind == WireEditKind.BreakExec && !edit.EndWithReturn)
                {
                    inserted = new EX_PopExecutionFlow();
                }
                else
                {
                    if (target == null || !layout.IsStatement(target))
                        throw new InvalidOperationException($"No statement at offset {edit.TargetOffset} in {function.ObjectName} to continue at.");
                    var jump = new EX_Jump();
                    layout.BindJump(jump, target);
                    inserted = jump;
                }
                layout.InsertAfter(statement, inserted);
            }

            foreach (var (_, target, built) in objectTargets.Where(o => o.Edit.Kind == ObjectEditKind.InsertAfter))
                layout.InsertAfter(replacements.TryGetValue(target, out var replaced) ? replaced : target, built);

            // code offsets of the built objects: "@offset" is a statement of the original code, "#label" an inserted one
            foreach (var pending in builder.Targets)
            {
                KismetExpression? statement;
                if (pending.Reference.StartsWith('#'))
                {
                    statement = builder.Labels.GetValueOrDefault(pending.Reference[1..]);
                    if (statement == null)
                        throw new InvalidOperationException($"{pending.Description}: no inserted statement has the label '{pending.Reference[1..]}'.");
                }
                else
                {
                    if (!uint.TryParse(pending.Reference[1..], out var offset) || layout.FindOriginal(offset) is not { } original)
                        throw new InvalidOperationException($"{pending.Description}: {pending.Reference} is not an offset of the original code.");
                    statement = replacements.TryGetValue(original, out var replaced) ? replaced : original;
                }
                if (!layout.IsStatement(statement))
                    throw new InvalidOperationException($"{pending.Description}: {pending.Reference} is not a statement ({statement.Token} is part of another statement).");
                layout.BindCodeOffset(statement, pending.Write, pending.Description);
            }

            layout.Commit();
        }
        return notes;
    }

    private static bool ContainsExpression(UAsset asset, KismetExpression root, KismetExpression expression)
    {
        var found = false;
        uint offset = 0;
        root.Visit(asset, ref offset, (e, _) => found |= ReferenceEquals(e, expression));
        return found;
    }

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static KismetExpression CloneVariable(KismetExpression source) =>
        source is EX_LocalVariable or EX_LocalOutVariable or EX_InstanceVariable or EX_DefaultVariable
            ? (KismetExpression) MemberwiseCloneMethod.Invoke(source, null)!
            : throw new InvalidOperationException($"{source.Token} is not a plain variable read.");

    private static KismetExpression CreateLiteral(string kind) => kind switch
    {
        WirePlanner.LiteralBool => new EX_False(),
        WirePlanner.LiteralInt => new EX_IntConst(),
        WirePlanner.LiteralInt64 => new EX_Int64Const(),
        WirePlanner.LiteralFloat => new EX_FloatConst(),
        WirePlanner.LiteralDouble => new EX_DoubleConst(),
        WirePlanner.LiteralByte => new EX_ByteConst(),
        WirePlanner.LiteralName => new EX_NameConst(),
        WirePlanner.LiteralString => new EX_StringConst(),
        WirePlanner.LiteralObject => new EX_NoObject(),
        WirePlanner.LiteralInterface => new EX_NoInterface(),
        _ => throw new InvalidOperationException($"No literal of kind {kind}."),
    };


    public static StructExport? FindFunction(UAsset asset, string name) =>
        asset.Exports.OfType<StructExport>().FirstOrDefault(o => o.ScriptBytecode != null && o.ObjectName.ToString() == name);

    /// <summary>Offset problems of every function with bytecode; run on the patched asset before writing it.</summary>
    public static List<string> Validate(UAsset asset) =>
        asset.Exports.OfType<StructExport>()
            .Where(o => o.ScriptBytecode != null)
            .SelectMany(o => KismetCodeLayout.Validate(asset, o))
            .ToList();

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

            case EX_NoObject or EX_NoInterface:
                return target;

            default:
                throw new InvalidOperationException($"{target.Token} constants can't be edited.");
        }
    }

    #endregion
}
