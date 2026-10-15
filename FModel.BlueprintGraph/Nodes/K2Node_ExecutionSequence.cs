using CUE4Parse.UE4.Kismet;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Nodes
{
    internal class K2Node_ExecutionSequence : BPNode
    {
        public K2Node_ExecutionSequence(int PinsCount, KismetExpression Instr) : base("Sequence", Instr)
        {
            MakePins(PinsCount);
        }

        protected void MakePins(int PinsCount)
        {
            GraphPinType ExecPinType = MakePinType(PinType.exec);

            ExecPin = new GraphPin("execute", EEdGraphPinDirection.EGPD_Input, ExecPinType);
            ExecPin.IsNameHidden = true;
            AddInputPin(ExecPin);

            for (int i = 0; i < PinsCount; i++)
            {
                AddOutputPin(new GraphPin($"Then {i}", EEdGraphPinDirection.EGPD_Output, ExecPinType));
            }
        }
    }
}
