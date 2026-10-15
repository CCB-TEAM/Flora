using CUE4Parse.UE4.Kismet;
using System.Collections.Generic;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using static FModel.BlueprintGraph.Engine.Utils;
using static FModel.BlueprintGraph.Engine.EngineBPData;

namespace FModel.BlueprintGraph.Nodes
{
    internal class K2Node_MakeArray : BPNode
    {
        public K2Node_MakeArray(List<GraphPin> ArrayContent, GraphPinType Type, KismetExpression Instr) : base("Make Array", Instr)
        {
            Pure = true;

            for (int i = 0; i < ArrayContent.Count; i++)
            {
                ArrayContent[i].SetName($"[{i}]");
                AddInputPin(ArrayContent[i]);
            }

            GraphPinType PinType = MakePinType(Type.PinCategory);
            PinType.ContainerType = EPinContainerType.Array;

            GraphPin OutPin = new GraphPin("Array", EEdGraphPinDirection.EGPD_Output, PinType);
            AddOutputPin(OutPin);
        }
    }

    // literal set (EX_SetSet), same layout as Make Array
    internal class K2Node_MakeSet : BPNode
    {
        public K2Node_MakeSet(List<GraphPin> SetContent, GraphPinType Type, KismetExpression Instr) : base("Make Set", Instr)
        {
            Pure = true;

            for (int i = 0; i < SetContent.Count; i++)
            {
                SetContent[i].SetName($"[{i}]");
                AddInputPin(SetContent[i]);
            }

            GraphPinType PinType = Type;
            PinType.ContainerType = EPinContainerType.Set;

            GraphPin OutPin = new GraphPin("Set", EEdGraphPinDirection.EGPD_Output, PinType);
            AddOutputPin(OutPin);
        }
    }
}
