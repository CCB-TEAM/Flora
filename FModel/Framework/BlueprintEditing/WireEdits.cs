#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FModel.BlueprintGraph.Nodes;
using CK = CUE4Parse.UE4.Kismet;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;

namespace FModel.Framework.BlueprintEditing;

public enum WireEditKind
{
    /// <summary>An input pin reads a variable (another node's output) instead of its original argument.</summary>
    ConnectData,

    /// <summary>An input pin gets a literal instead of its original argument.</summary>
    LiteralData,

    /// <summary>Execution continues at another statement after a node (an inserted jump).</summary>
    ConnectExec,

    /// <summary>Execution ends after a node (an inserted end of thread or jump to the return).</summary>
    BreakExec,

    /// <summary>An event starts the event graph at another statement (its stub passes a different entry point).</summary>
    ConnectEntry,

    /// <summary>An event ends right away (its entry point is a statement that ends the execution).</summary>
    BreakEntry,
}

/// <summary>
/// A change of a wire, expressed on the bytecode the graph was decompiled from (in-memory offsets of the original code).
/// </summary>
public sealed class WireEdit
{
    public string FunctionName { get; init; } = "";
    public WireEditKind Kind { get; init; }

    /// <summary>Data edits: the argument expression to replace. Exec edits: the statement after which execution is redirected.</summary>
    public uint Offset { get; init; }
    public string Token { get; init; } = "";

    /// <summary>ConnectData: the variable read to copy.</summary>
    public uint SourceOffset { get; init; }
    public string SourceToken { get; init; } = "";

    /// <summary>LiteralData: the literal.</summary>
    public string LiteralKind { get; init; } = "";
    public string LiteralText { get; set; } = "";

    /// <summary>ConnectExec: the statement to continue at. BreakExec: set when the end is a jump to the function's return.</summary>
    public uint TargetOffset { get; init; }
    public bool EndWithReturn { get; init; }

    public (string Function, WireEditKind Group, uint Offset) Key =>
        (FunctionName, Kind switch
        {
            WireEditKind.ConnectExec or WireEditKind.BreakExec => WireEditKind.ConnectExec,
            WireEditKind.ConnectEntry or WireEditKind.BreakEntry => WireEditKind.ConnectEntry,
            _ => WireEditKind.ConnectData,
        }, Offset);

    public override string ToString() => $"{FunctionName}@{Offset} {Kind}";
}

/// <summary>
/// Checks graph wire edits against the bytecode they would change and turns them into <see cref="WireEdit"/>s.
/// Only edits the compiled code can express without recompiling are accepted.
/// </summary>
public static class WirePlanner
{
    /// <summary>Looks up an interface text by key (null: use the English text).</summary>
    public static Func<string, string?>? Translate { get; set; }

    private static string T(string key, string english, params object[] args) =>
        string.Format(Translate?.Invoke(key) ?? english, args);

    public const string LiteralBool = "Bool", LiteralInt = "Int", LiteralInt64 = "Int64", LiteralFloat = "Float", LiteralDouble = "Double",
        LiteralByte = "Byte", LiteralName = "Name", LiteralString = "String", LiteralObject = "Object", LiteralInterface = "Interface";

    /// <summary>BreakEntry target: the patcher inserts a new statement that ends the execution.</summary>
    public const uint NewEnd = uint.MaxValue;

    public static bool IsExec(GraphPin pin) => pin.PinType.PinCategory == PinType.exec;

    private static GraphPinType TypeOf(GraphPin pin) => pin.Property?.PinType ?? pin.PinType;

    private static bool SameType(GraphPin a, GraphPin b)
    {
        var x = TypeOf(a);
        var y = TypeOf(b);
        static PinType Category(PinType c) => c == PinType.Enum ? PinType.Byte : c;
        static string Sub(string? s) => string.IsNullOrEmpty(s) || s == "None" ? "" : s;
        return Category(x.PinCategory) == Category(y.PinCategory) && x.ContainerType == y.ContainerType &&
               string.Equals(Sub(x.PinSubCategoryObject), Sub(y.PinSubCategoryObject), StringComparison.OrdinalIgnoreCase);
    }

    private static string VariableKey(CK.KismetExpression expression) =>
        expression is CK.EX_VariableBase variable ? $"{expression.Token}:{variable.Variable}" : $"{expression.Token}@{expression.StatementIndex}";

    #region data

    /// <summary>Connects <paramref name="input"/> to the variable behind <paramref name="output"/>. Returns an error or the edit.</summary>
    public static WireEdit? PlanConnectData(string functionName, GraphPin input, GraphPin output, IEnumerable<GraphPin> graphOutputs, out string error)
    {
        error = string.Empty;
        if (input.ArgumentExpression is not { } argument)
        {
            error = T("Wire_NoArgument", "This input has no argument in the bytecode that could be replaced.");
            return null;
        }
        if (output.SourceExpression is not { } source)
        {
            error = T("Wire_NoVariable", "This output is not kept in a variable, so other nodes can't read it.");
            return null;
        }
        if (ReferenceEquals(input.ParentNode, output.ParentNode))
        {
            error = T("Wire_SelfFeed", "A node can't feed itself.");
            return null;
        }
        if (!SameType(input, output))
        {
            error = T("Wire_TypeMismatch", "The types don't match ({0} -> {1}).", Describe(TypeOf(output)), Describe(TypeOf(input)));
            return null;
        }

        // a temp variable reused by several nodes holds whichever value was written last
        var key = VariableKey(source);
        if (graphOutputs.Any(o => !ReferenceEquals(o, output) && o.SourceExpression is { } other && VariableKey(other) == key && o.ParentNode is not K2Node_VariableGet))
        {
            error = T("Wire_VariableReused", "The variable holding this output is reused by other nodes; reading it here could return another node's value.");
            return null;
        }

        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.ConnectData,
            Offset = (uint) argument.StatementIndex, Token = argument.Token.ToString(),
            SourceOffset = (uint) source.StatementIndex, SourceToken = source.Token.ToString(),
        };
    }

    /// <summary>The literal kind an input of this type can take, or null.</summary>
    public static string? LiteralKindOf(GraphPin input)
    {
        var type = TypeOf(input);
        if (type.ContainerType != EPinContainerType.None || type.IsReference)
            return null;
        return type.PinCategory switch
        {
            PinType.Bool => LiteralBool,
            PinType.Int => LiteralInt,
            PinType.Int64 => LiteralInt64,
            PinType.Float => LiteralFloat,
            PinType.Double => LiteralDouble,
            PinType.Byte or PinType.Enum => LiteralByte,
            PinType.Name => LiteralName,
            PinType.String => LiteralString,
            PinType.Object or PinType.Class or PinType.SoftObject or PinType.SoftClass => LiteralObject,
            PinType.Interface => LiteralInterface,
            _ => null,
        };
    }

    public static string DefaultLiteral(string kind) => kind switch
    {
        LiteralBool => "false",
        LiteralName or LiteralObject or LiteralInterface => "None",
        LiteralString => "",
        _ => "0",
    };

    public static bool IsValidLiteral(string kind, string text)
    {
        var inv = CultureInfo.InvariantCulture;
        text = text.Trim();
        return kind switch
        {
            LiteralBool => bool.TryParse(text, out _),
            LiteralInt => int.TryParse(text, NumberStyles.Integer, inv, out _),
            LiteralInt64 => long.TryParse(text, NumberStyles.Integer, inv, out _),
            LiteralFloat => float.TryParse(text, NumberStyles.Float, inv, out _),
            LiteralDouble => double.TryParse(text, NumberStyles.Float, inv, out _),
            LiteralByte => byte.TryParse(text, NumberStyles.Integer, inv, out _),
            LiteralName => text.Length > 0,
            LiteralString => true,
            LiteralObject or LiteralInterface => text is "None" or "",
            _ => false,
        };
    }

    /// <summary>Replaces the argument of <paramref name="input"/> with a literal (breaking its link).</summary>
    public static WireEdit? PlanLiteral(string functionName, GraphPin input, string? text, out string error)
    {
        error = string.Empty;
        if (input.ArgumentExpression is not { } argument)
        {
            error = T("Wire_NoArgument", "This input has no argument in the bytecode that could be replaced.");
            return null;
        }
        if (LiteralKindOf(input) is not { } kind)
        {
            error = T("Wire_NoLiteral", "A {0} input can't take a literal value.", Describe(TypeOf(input)));
            return null;
        }
        if (input.PinName is "self" or "Target" || input.PinFriendlyName is "Target")
        {
            error = T("Wire_TargetLiteral", "The target of a call can't be replaced by a literal.");
            return null;
        }
        if (input.ParentNode?.NodeType.StartsWith("K2Node_Switch", StringComparison.Ordinal) == true)
        {
            error = T("Wire_SwitchLiteral", "A switch selection can't be replaced by a literal.");
            return null;
        }
        text ??= DefaultLiteral(kind);
        if (!IsValidLiteral(kind, text))
        {
            error = T("Wire_InvalidLiteral", "'{0}' is not a valid {1} value.", text, kind);
            return null;
        }
        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.LiteralData,
            Offset = (uint) argument.StatementIndex, Token = argument.Token.ToString(),
            LiteralKind = kind, LiteralText = text,
        };
    }

    #endregion

    #region exec

    private static int StatementIndexOf(CK.KismetExpression[] code, CK.KismetExpression? expression) =>
        expression == null ? -1 : Array.FindIndex(code, o => ReferenceEquals(o, expression));

    /// <summary>
    /// The statement to jump to for entering <paramref name="to"/>: pure nodes the node reads compile into statements right before it,
    /// and jumping past them would leave their temporaries stale.
    /// </summary>
    private static CK.KismetExpression EnterStatement(BPNode to, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes)
    {
        var index = StatementIndexOf(code, to.NodeInstr);
        while (index > 0)
        {
            var previous = code[index - 1];
            var owner = nodes.FirstOrDefault(o => ReferenceEquals(o.NodeInstr, previous));
            // a Create Event node is no node of its own: it only stores the delegate in a temporary
            if (owner == null && previous is CK.EX_LetDelegate { Assignment: CK.EX_InstanceDelegate })
            {
                index--;
                continue;
            }
            if (owner == null || owner.ExecPin != null || owner.ExecOutPin != null || owner.Input.Concat(owner.Output).Any(IsExec))
                break;
            index--;
        }
        return code[index];
    }

    /// <summary>Why execution after <paramref name="node"/> can't be redirected, or null when it can.</summary>
    public static string? CheckExecSource(BPNode node, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes)
    {
        if (node.Output.Count(IsExec) != 1)
            return T("Wire_SingleExecOutput", "Only nodes with a single execution output can be rewired (not Branch, Sequence, loops...).");
        if (node.NodeType is "K2Node_MacroInstance" or "K2Node_Event" or "K2Node_CustomEvent" or "K2Node_ComponentBoundEvent" or "K2Node_FunctionEntry")
            return T("Wire_EventsUnsupported", "Events, function entries and macros can't be rewired yet.");
        if (node.Input.Concat(node.Output).Any(o => (TypeOf(o).PinSubCategoryObject ?? "").Contains("LatentActionInfo", StringComparison.OrdinalIgnoreCase)))
            return T("Wire_Latent", "Latent nodes (Delay...) continue at a resume point, not after their statement.");

        var index = StatementIndexOf(code, node.NodeInstr);
        if (index < 0)
            return T("Wire_NotStatement", "The node is not a statement of its own in the bytecode.");
        if (index + 1 >= code.Length)
            return T("Wire_LastStatement", "The node is the last statement.");

        // the statement right after must start another node (or be plain flow); otherwise it belongs to this node
        var next = code[index + 1];
        var isFlow = next.Token is CK.EExprToken.EX_Jump or CK.EExprToken.EX_PopExecutionFlow or CK.EExprToken.EX_PopExecutionFlowIfNot
            or CK.EExprToken.EX_Return or CK.EExprToken.EX_PushExecutionFlow or CK.EExprToken.EX_JumpIfNot or CK.EExprToken.EX_ComputedJump;
        if (!isFlow && !nodes.Any(o => ReferenceEquals(o.NodeInstr, next)))
            return T("Wire_TrailingStatements", "The node is followed by compiler-generated statements that belong to it.");
        return null;
    }

    public static WireEdit? PlanConnectExec(string functionName, BPNode from, BPNode to, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes, out string error)
    {
        error = CheckExecSource(from, code, nodes) ?? string.Empty;
        if (error.Length > 0)
            return null;
        if (ReferenceEquals(from, to))
        {
            error = T("Wire_SelfLoop", "A node can't continue into itself.");
            return null;
        }
        if (to.ExecPin == null || to.NodeType is "K2Node_MacroInstance")
        {
            error = T("Wire_TargetNoExec", "The target has no execution input that can be jumped to.");
            return null;
        }
        if (StatementIndexOf(code, to.NodeInstr) < 0)
        {
            error = T("Wire_TargetNotStatement", "The target is not a statement of its own in the bytecode.");
            return null;
        }
        if (to.NodeType == "K2Node_AddDelegate")
        {
            error = T("Wire_TargetDelegate", "Event binding nodes depend on a Create Event statement that comes before them; they can't be jumped to.");
            return null;
        }
        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.ConnectExec,
            Offset = (uint) from.NodeInstr!.StatementIndex, Token = from.NodeInstr.Token.ToString(),
            TargetOffset = (uint) EnterStatement(to, code, nodes).StatementIndex,
        };
    }

    public static WireEdit? PlanBreakExec(string functionName, BPNode from, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes, out string error)
    {
        error = CheckExecSource(from, code, nodes) ?? string.Empty;
        if (error.Length > 0)
            return null;

        // like the compiler: end of thread where the flow stack is used, else return
        var usesFlowStack = functionName.StartsWith("ExecuteUbergraph", StringComparison.Ordinal) ||
                            code.Any(o => o.Token == CK.EExprToken.EX_PushExecutionFlow);
        var ret = code.LastOrDefault(o => o.Token == CK.EExprToken.EX_Return);
        if (!usesFlowStack && ret == null)
        {
            error = T("Wire_NoReturn", "The function has no return to jump to.");
            return null;
        }
        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.BreakExec,
            Offset = (uint) from.NodeInstr!.StatementIndex, Token = from.NodeInstr.Token.ToString(),
            EndWithReturn = !usesFlowStack, TargetOffset = usesFlowStack ? 0 : (uint) ret!.StatementIndex,
        };
    }

    /// <summary>Why the event pin's start can't be redirected, or null when it can.</summary>
    private static string? CheckEntrySource(GraphPin pin)
    {
        if (pin.EntryPoint is not { } || pin.ParentNode == null)
            return T("Wire_NoEntry", "This event has no entry point in the event graph.");
        // key and enhanced input events run initialisation statements at the start of their chain that later nodes read
        if (pin.ParentNode.NodeType is "K2Node_InputKey" or "K2Node_EnhancedInputAction")
            return T("Wire_InputEventEntry", "Input events set up temporary values at their start; they can't be redirected yet.");
        return null;
    }

    /// <summary>Starts the event of <paramref name="from"/> (an event's exec output) at the node <paramref name="to"/>.</summary>
    public static WireEdit? PlanConnectEntry(string functionName, GraphPin from, BPNode to, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes, out string error)
    {
        error = CheckEntrySource(from) ?? string.Empty;
        if (error.Length > 0)
            return null;
        if (ReferenceEquals(from.ParentNode, to))
        {
            error = T("Wire_SelfLoop", "A node can't continue into itself.");
            return null;
        }
        if (to.ExecPin == null || to.NodeType is "K2Node_MacroInstance")
        {
            error = T("Wire_TargetNoExec", "The target has no execution input that can be jumped to.");
            return null;
        }
        if (StatementIndexOf(code, to.NodeInstr) < 0)
        {
            error = T("Wire_TargetNotStatement", "The target is not a statement of its own in the bytecode.");
            return null;
        }
        if (to.NodeType == "K2Node_AddDelegate")
        {
            error = T("Wire_TargetDelegate", "Event binding nodes depend on a Create Event statement that comes before them; they can't be jumped to.");
            return null;
        }
        var start = EnterStatement(to, code, nodes);
        // the decompiler tells events apart by their start; two events can't start at the same statement
        if (nodes.SelectMany(o => o.Output).Any(o => o.EntryPoint is { } entry && entry == (uint) start.StatementIndex && !ReferenceEquals(o, from)))
        {
            error = T("Wire_EntryShared", "Another event already starts there.");
            return null;
        }
        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.ConnectEntry,
            Offset = from.EntryPoint!.Value, Token = "EX_IntConst",
            TargetOffset = (uint) start.StatementIndex,
        };
    }

    /// <summary>Makes the event end right away, by starting it at a statement that ends the execution.</summary>
    public static WireEdit? PlanBreakEntry(string functionName, GraphPin from, CK.KismetExpression[] code, IReadOnlyCollection<BPNode> nodes, out string error)
    {
        error = CheckEntrySource(from) ?? string.Empty;
        if (error.Length > 0)
            return null;
        // not one another event starts at: the decompiler tells events apart by their start
        var starts = nodes.SelectMany(o => o.Output).Select(o => o.EntryPoint).Where(o => o != null).ToHashSet();
        var end = code.FirstOrDefault(o => o.Token == CK.EExprToken.EX_PopExecutionFlow && !starts.Contains((uint) o.StatementIndex));
        return new WireEdit
        {
            FunctionName = functionName, Kind = WireEditKind.BreakEntry,
            Offset = from.EntryPoint!.Value, Token = "EX_IntConst",
            // no statement to reuse: the patcher adds one
            TargetOffset = end == null ? NewEnd : (uint) end.StatementIndex,
        };
    }

    #endregion

    private static string Describe(GraphPinType type) =>
        (type.ContainerType == EPinContainerType.None ? "" : type.ContainerType + " of ") + type.PinCategory +
        (string.IsNullOrEmpty(type.PinSubCategoryObject) || type.PinSubCategoryObject == "None" ? "" : $" ({type.PinSubCategoryObject.Split('.').Last()})");
}
