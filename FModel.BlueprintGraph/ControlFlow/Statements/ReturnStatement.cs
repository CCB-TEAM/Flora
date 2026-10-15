using System.Collections.Generic;
using System.Linq;
using FModel.BlueprintGraph.Decompiler;
using FModel.BlueprintGraph.Nodes;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.ControlFlow.Statements
{
    public class ReturnStatement : ControlFlowStatement
    {
        public ReturnStatement(DecompilerContext context) : base(context) { }

        public override void Decompile()
        {
            Context.MarkAsParsed();

            List<GraphPin> returnPin = [.. Context.LocalVars.GetOutPins().Select(o => o.Clone())];

            // no need to create return node on events and without params
            if (!Context.Global.IsUbergraph && returnPin.Count > 0)
            {
                K2Node_FunctionResult returnNode = new K2Node_FunctionResult(returnPin, Context.GetInstr());
                Context.AddNode(returnNode);
                Connect(Context.LastPin, returnNode.ExecPin);
            }
        }
    }
}
