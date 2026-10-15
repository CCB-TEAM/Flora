using CUE4Parse.UE4.Kismet;
using System.Collections.Generic;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Nodes
{
    internal class K2Node_MakeStruct : BPNode
    {
        public K2Node_MakeStruct(string StructName, List<GraphPin> Pins, KismetExpression Instr) : base($"Make {StructName}", Instr)
        {
            MakePins(false, false);

            GraphPin CaseExec = new GraphPin(StructName, Engine.EngineEnums.EEdGraphPinDirection.EGPD_Output, MakePinType(PinType.Struct));
            AddOutputPin(CaseExec);

            foreach (GraphPin pin in Pins)
            {
                AddInputPin(pin);
            }
        }
    }
}
