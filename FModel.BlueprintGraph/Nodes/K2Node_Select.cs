using CUE4Parse.UE4.Kismet;
using System.Collections.Generic;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Nodes
{
    internal class K2Node_Select : BPNode
    {
        public GraphPin IndexPin;
        public List<GraphPin> Cases;
        public K2Node_Select(GraphPin IndexPin, List<GraphPin> Cases, KismetExpression Instr) : base("Select", Instr)
        {
            Pure = true;
            this.IndexPin = IndexPin;
            this.Cases = Cases;
            MakePins();
        }

        protected void MakePins()
        {
            GraphPinType PinType = Cases[0].PinType;

            GraphPin ResultPin = new GraphPin("Return Value", EEdGraphPinDirection.EGPD_Output, PinType);

            foreach (GraphPin Pin in Cases)
            {
                AddInputPin(Pin);
            }

            IndexPin.SetName("Index");

            AddInputPin(IndexPin);
            AddOutputPin(ResultPin);
        }
    }
}
