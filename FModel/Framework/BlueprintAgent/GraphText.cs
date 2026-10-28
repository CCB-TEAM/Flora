using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FModel.BlueprintGraph;
using FModel.BlueprintGraph.Nodes;
using FModel.Framework.BlueprintEditing;
using FModel.ViewModels;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Decompiled graphs as compact text for the model. Nodes are <c>N{index}</c> in the graph's node list,
/// pins <c>N{index}.i{n}</c> / <c>N{index}.o{n}</c> (input / output position on the node); both stay valid until the graphs are reset.
/// </summary>
public static class GraphText
{
    public static string NodeId(BPGraph graph, BPNode node) => $"N{graph.Nodes.IndexOf(node)}";

    public static string PinId(BPGraph graph, GraphPin pin)
    {
        var node = pin.ParentNode;
        if (node == null)
            return "?";
        return pin.IsInput ? $"{NodeId(graph, node)}.i{node.Input.IndexOf(pin)}" : $"{NodeId(graph, node)}.o{node.Output.IndexOf(pin)}";
    }

    /// <summary>Finds a pin by <c>N3.i1</c>, <c>N3.o0</c> or <c>N3.PinName</c>.</summary>
    public static GraphPin FindPin(BPGraph graph, string reference)
    {
        reference = reference?.Trim() ?? "";
        var dot = reference.IndexOf('.');
        if (dot < 2 || reference[0] is not ('N' or 'n') || !int.TryParse(reference[1..dot], out var nodeIndex) || nodeIndex < 0 || nodeIndex >= graph.Nodes.Count)
            throw new AgentToolException($"'{reference}' is not a pin of this graph (pins look like N3.i1, N3.o0 or N3.PinName).");

        var node = graph.Nodes[nodeIndex];
        var pinPart = reference[(dot + 1)..];
        if (pinPart.Length > 1 && pinPart[0] is 'i' or 'o' && int.TryParse(pinPart[1..], out var pinIndex))
        {
            var list = pinPart[0] == 'i' ? node.Input : node.Output;
            if (pinIndex >= 0 && pinIndex < list.Count)
                return list[pinIndex];
        }

        var matches = node.Input.Concat(node.Output)
            .Where(o => string.Equals(o.PinName, pinPart, StringComparison.OrdinalIgnoreCase) || string.Equals(o.PinFriendlyName, pinPart, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AgentToolException($"N{nodeIndex} ({node.Name}) has no pin '{pinPart}'."),
            _ => throw new AgentToolException($"N{nodeIndex} has several pins named '{pinPart}'; use the i/o index form."),
        };
    }

    public static string DescribeType(GraphPinType type)
    {
        var text = type.PinCategory.ToString();
        if (!string.IsNullOrEmpty(type.PinSubCategoryObject) && type.PinSubCategoryObject != None)
            text += $"<{type.PinSubCategoryObject}>";
        if (type.ContainerType != EPinContainerType.None)
            text = $"{type.ContainerType}[{text}]";
        return type.IsReference ? text + "&" : text;
    }

    /// <summary>A part of a graph: the nodes with their distance (in exec steps) from where the selection started, and what it is.</summary>
    public sealed record Selection(List<(BPNode Node, int Depth)> Nodes, string Description);

    /// <summary>The event's start node: the one named like the event (all events share one graph).</summary>
    public static BPNode FindEventStart(BPGraph graph, params string[] names) =>
        names.Where(o => !string.IsNullOrEmpty(o)).Select(graph.FindFuncStartNode).FirstOrDefault(o => o != null) ??
        graph.Nodes.FirstOrDefault(o => o.NodeType.Contains("Event", StringComparison.Ordinal) && names.Contains(o.Name));

    /// <summary>
    /// The nodes reachable from <paramref name="start"/> along execution wires (downstream, upstream or both), at most <paramref name="maxDepth"/> exec steps
    /// away, plus the data nodes they read (the pure chains feeding their inputs, which are not on the execution path themselves).
    /// </summary>
    public static Selection Reach(BPGraph graph, BPNode start, int maxDepth, bool downstream, bool upstream, string what)
    {
        var depth = new Dictionary<BPNode, int> { [start] = 0 };
        var order = new List<BPNode> { start };
        var queue = new Queue<BPNode>([start]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var next = depth[node] + 1;
            if (next > maxDepth)
                continue;
            var neighbours = new List<BPNode>();
            if (downstream)
                neighbours.AddRange(node.Output.Where(WirePlanner.IsExec).SelectMany(o => o.LinkedTo).Select(o => o.ParentNode));
            if (upstream)
                neighbours.AddRange(node.Input.Where(WirePlanner.IsExec).SelectMany(o => o.LinkedTo).Select(o => o.ParentNode));
            foreach (var neighbour in neighbours.Where(o => o != null))
            {
                if (depth.TryAdd(neighbour, next))
                {
                    order.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
        }

        // the data each of them reads: pure nodes feeding the inputs, recursively (shown one step beyond their reader)
        var execNodes = order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            foreach (var source in order[i].Input.Where(o => !WirePlanner.IsExec(o)).SelectMany(o => o.LinkedTo).Select(o => o.ParentNode))
            {
                if (source != null && depth.TryAdd(source, depth[order[i]]))
                    order.Add(source);
            }
        }

        return new Selection([.. order.Select(o => (o, depth[o]))],
            $"{order.Count} of {graph.Nodes.Count} nodes: {what} ({execNodes} on the execution path, {order.Count - execNodes} data nodes they read" +
            (maxDepth < int.MaxValue ? $"; at most {maxDepth} exec step(s)" : "") + ")");
    }

    /// <summary>The graph (or a part of it) as text. <paramref name="selection"/> null: every node.</summary>
    public static string Describe(BlueprintViewerViewModel viewer, BlueprintGraphResult result, string functionLabel, string filter, int skip, int take, Selection selection = null)
    {
        var graph = result.Graph;
        var nodes = (selection?.Nodes.Select(o => (o.Node, Index: graph.Nodes.IndexOf(o.Node), o.Depth)) ?? graph.Nodes.Select((node, index) => (node, index, 0)))
            .Select(o => (node: o.Item1, index: o.Item2, depth: o.Item3)).Where(o => o.index >= 0).ToList();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var ids = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(o => o.Length > 1 && o[0] is 'N' or 'n' && int.TryParse(o[1..], out _)).Select(o => int.Parse(o[1..])).ToHashSet();
            nodes = ids.Count > 0
                ? nodes.Where(o => ids.Contains(o.index)).ToList()
                : nodes.Where(o => Matches(o.node, filter)).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"graph of {functionLabel} (bytecode function {result.FunctionName}): " + (selection?.Description ?? $"{graph.Nodes.Count} nodes") +
                      (result.IsBytecodeComplete ? "" : "  WARNING: bytecode incomplete, editing is disabled") +
                      (result.Result.HasCriticalErrors ? "  WARNING: decompilation failed partly" : ""));
        if (!string.IsNullOrWhiteSpace(filter))
            sb.AppendLine($"{nodes.Count} node(s) match '{filter}'");
        var page = nodes.Skip(skip).Take(take).ToList();
        foreach (var (node, index, depth) in page)
            AppendNode(sb, viewer, graph, node, index, selection == null ? null : depth);
        if (skip + page.Count < nodes.Count)
            sb.AppendLine($"... {nodes.Count - skip - page.Count} more node(s): call again with skip={skip + page.Count}");
        return sb.ToString();
    }

    private static bool Matches(BPNode node, string term)
    {
        bool Contains(string source) => source != null && source.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Contains(node.Name) || Contains(node.NodeType) ||
               node.Input.Concat(node.Output).Any(p => Contains(p.PinFriendlyName) || Contains(p.PinName) || p.IsInput && Contains(p.Value));
    }

    private static void AppendNode(StringBuilder sb, BlueprintViewerViewModel viewer, BPGraph graph, BPNode node, int index, int? depth = null)
    {
        sb.Append($"N{index} {node.NodeType} \"{node.Name}\"");
        if (depth != null)
            sb.Append($" [exec step {depth}]");
        if (node.NodeInstr is { } instr)
            sb.Append($" stmt@{instr.StatementIndex}..{instr.EndIndex} {instr.Token}");
        if (node.Pure) sb.Append(" pure");
        if (node.IsSignatureInferred) sb.Append(" signature-inferred");
        sb.AppendLine();

        for (var i = 0; i < node.Input.Count; i++)
            AppendPin(sb, viewer, graph, node.Input[i], $"i{i}");
        for (var i = 0; i < node.Output.Count; i++)
            AppendPin(sb, viewer, graph, node.Output[i], $"o{i}");
    }

    private static void AppendPin(StringBuilder sb, BlueprintViewerViewModel viewer, BPGraph graph, GraphPin pin, string id)
    {
        var type = pin.Property?.PinType ?? pin.PinType;
        sb.Append($"  {id} {pin.PinFriendlyName ?? pin.PinName}: {DescribeType(type)}");
        if (pin.IsInput && !pin.IsConnected && !string.IsNullOrEmpty(pin.Value))
            sb.Append($" = {Quote(pin.Value)}");

        var flags = new List<string>();
        if (pin.IsHidden) flags.Add("hidden");
        if (pin.IsEdited) flags.Add("EDITED");
        if (viewer.Editor.CanEditPinValue(pin))
        {
            var kind = viewer.Editor.GetValueKind(pin);
            var enumNames = viewer.GetPinEnumNames(pin);
            flags.Add(enumNames.Count is > 0 and <= 16 ? $"value:{kind} [{string.Join("|", enumNames)}]" : $"value:{kind}");
        }
        foreach (var constant in pin.SourceConstants)
            flags.Add($"const {constant.Token}@{constant.StatementIndex}");
        if (pin.ArgumentExpression is { } argument)
            flags.Add($"arg {argument.Token}@{argument.StatementIndex}");
        if (pin.SourceExpression is { } source)
            flags.Add($"var {source.Token}@{source.StatementIndex}");
        if (pin.EntryPoint is { } entry)
            flags.Add($"entry@{entry}");
        if (flags.Count > 0)
            sb.Append($"  {{{string.Join(", ", flags)}}}");

        if (pin.LinkedTo.Count > 0)
            sb.Append(pin.IsInput ? "  <- " : "  -> ").Append(string.Join(", ", pin.LinkedTo.Select(o => PinId(graph, o))));
        sb.AppendLine();
    }

    private static string Quote(string value) => value.Length > 120 ? $"\"{value[..117]}...\"" : $"\"{value}\"";
}
