using System.Collections.Generic;
using System.Linq;
using FModel.BlueprintGraph.Nodes;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Decompiler
{
    /// <summary>
    /// Collapses inlined Gate macros back into a Gate node.
    /// Gate can't be matched with a fixed template: every used input (Enter/Open/Close/Toggle) inlines its own
    /// Sequence whose first outputs run the shared "apply Start Closed once" initialization and whose last output
    /// is the input's action on the gate state G:
    ///   Enter: Branch(G) -> then is Exit,  Open: G = true,  Close: G = false,  Toggle: G = NOT G.
    /// The initialization chains never reach user code; anything that doesn't fit this shape is left untouched.
    /// </summary>
    internal static class GateRecognizer
    {
        private const string GateStateVariable = "Temp_bool_Whether_the_gate_is_currently_open_or_close_Variable";

        private enum GateInput { Enter, Open, Close, Toggle }

        public static void Collapse(BPGraph graph)
        {
            foreach (var state in graph.Nodes.OfType<K2Node_TemporaryVariable>().Where(o => o.VarName.StartsWith(GateStateVariable)).ToList())
                TryCollapse(graph, state);
        }

        private static void TryCollapse(BPGraph graph, K2Node_TemporaryVariable state)
        {
            var users = state.VarPin.LinkedTo.ToList();
            var inputs = new Dictionary<GateInput, BPNode>(); // input -> its inlined Sequence
            BPNode? exitBranch = null;
            GraphPin? startClosedCondition = null;
            var removed = new HashSet<BPNode> { state };

            foreach (var use in users)
            {
                var node = use.ParentNode;
                GateInput input;
                if (node is K2Node_IfThenElse && use.PinName == "Condition")
                {
                    input = GateInput.Enter;
                    exitBranch = node;
                    if (node.Output.FirstOrDefault(o => o.PinName == "else") is { IsConnected: true })
                        return;
                }
                else if (node is K2Node_AssignmentStatement assign && use == assign.VariablePin)
                {
                    var source = assign.ExecPin?.LinkedTo.SingleOrDefault();
                    if (source?.ParentNode is K2Node_IfThenElse initBranch)
                    {
                        // initialization: Branch(Start Closed) -> then: G = false, else: G = true
                        startClosedCondition ??= initBranch.Input.FirstOrDefault(o => o.PinName == "Condition");
                        removed.Add(node);
                        removed.Add(initBranch);
                        continue;
                    }

                    var value = assign.ValuePin;
                    if (!value.IsConnected && value.Value == "true") input = GateInput.Open;
                    else if (!value.IsConnected && value.Value == "false") input = GateInput.Close;
                    else if (value.LinkedTo.SingleOrDefault()?.ParentNode is { Name: "NOT" } not &&
                             not.Input.Any(o => o.LinkedTo.Contains(state.VarPin))) input = GateInput.Toggle;
                    else return;
                }
                else if (node is K2Node_CallFunction { Name: "NOT" })
                {
                    removed.Add(node); // operand of Toggle
                    continue;
                }
                else
                {
                    return;
                }

                // the action must be the last output of the input's own Sequence
                var sequencePin = node.ExecPin?.LinkedTo.SingleOrDefault();
                if (sequencePin?.ParentNode is not K2Node_ExecutionSequence sequence ||
                    sequence.Output.LastOrDefault() != sequencePin ||
                    !inputs.TryAdd(input, sequence))
                    return;
                removed.Add(node);
            }

            if (inputs.Count == 0 || startClosedCondition == null)
                return;

            // every other Sequence output runs initialization only: branches/assignments that never leave the macro
            foreach (var sequence in inputs.Values)
            {
                removed.Add(sequence);
                foreach (var init in sequence.Output.Take(sequence.Output.Count - 1))
                {
                    if (!CollectInitialization(init, removed))
                        return;
                }
            }

            // local variables and pure helpers only used inside the removed nodes go too
            var removedSet = removed.ToList();
            foreach (var node in removedSet)
            {
                foreach (var pin in node.Input)
                {
                    foreach (var source in pin.LinkedTo)
                    {
                        if (source.ParentNode is K2Node_TemporaryVariable temp &&
                            temp.VarPin.LinkedTo.All(o => removed.Contains(o.ParentNode)))
                            removed.Add(temp);
                    }
                }
            }

            var execType = MakePinType(PinType.exec);
            var enter = new GraphPin("Enter", EEdGraphPinDirection.EGPD_Input, execType);
            var open = new GraphPin("Open", EEdGraphPinDirection.EGPD_Input, execType);
            var close = new GraphPin("Close", EEdGraphPinDirection.EGPD_Input, execType);
            var toggle = new GraphPin("Toggle", EEdGraphPinDirection.EGPD_Input, execType);
            var startClosed = new GraphPin("Start Closed", startClosedCondition.Value, EEdGraphPinDirection.EGPD_Input, MakePinType(PinType.Bool));
            startClosed.AddSourcesFrom(startClosedCondition);
            var exit = new GraphPin("Exit", EEdGraphPinDirection.EGPD_Output, execType);
            var pins = new Dictionary<GateInput, GraphPin>
            {
                [GateInput.Enter] = enter, [GateInput.Open] = open, [GateInput.Close] = close, [GateInput.Toggle] = toggle,
            };

            foreach (var (input, sequence) in inputs)
            {
                foreach (var source in sequence.ExecPin!.LinkedTo.ToList())
                {
                    source.Disconnect(sequence.ExecPin);
                    sequence.ExecPin.Disconnect(source);
                    Connect(source, pins[input]);
                }
            }

            if (exitBranch?.Output.FirstOrDefault(o => o.PinName == "then") is { } then)
            {
                foreach (var target in then.LinkedTo.ToList())
                {
                    target.Disconnect(then);
                    then.Disconnect(target);
                    Connect(exit, target);
                }
            }

            foreach (var source in startClosedCondition.LinkedTo.ToList())
                Connect(source, startClosed);

            var anchor = inputs.TryGetValue(GateInput.Enter, out var enterSequence) ? enterSequence : inputs.Values.First();
            var gate = new K2Node_MacroInstance([enter, open, close, toggle, startClosed], [exit], "Gate", anchor.NodeInstr);
            graph.AddNode(gate);

            foreach (var node in removed)
                graph.RemoveNode(node);
        }

        // follow exec links from an initialization output; fails if the chain reaches anything but gate internals
        private static bool CollectInitialization(GraphPin start, HashSet<BPNode> collected)
        {
            var pending = new Stack<GraphPin>();
            pending.Push(start);
            var visited = new HashSet<BPNode>();
            while (pending.Count > 0)
            {
                foreach (var target in pending.Pop().LinkedTo)
                {
                    var node = target.ParentNode;
                    if (!visited.Add(node))
                        continue;
                    if (node is not (K2Node_IfThenElse or K2Node_AssignmentStatement or K2Node_ExecutionSequence))
                        return false;

                    collected.Add(node);
                    foreach (var output in node.Output.Where(o => o.PinType.PinCategory == PinType.exec))
                        pending.Push(output);
                }
            }
            return true;
        }
    }
}
