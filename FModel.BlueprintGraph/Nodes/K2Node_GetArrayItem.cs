using CUE4Parse.UE4.Kismet;
using static FModel.BlueprintGraph.Engine.EngineEnums;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Nodes
{
    public class K2Node_GetArrayItem : BPNode
    {
        public GraphPin TargetPin;
        public GraphPin VarPin;
        public GraphPin ArrayPin;
        public GraphPin IndexPin;
        public K2Node_GetArrayItem(GraphPin ArrayPin, GraphPin IndexPin, KismetExpression Instr) : base("GET", Instr)
        {
            HeaderHidden = true;
            ShowNameAsBody = true;
            this.ArrayPin = ArrayPin;
            this.IndexPin = IndexPin;
            VarPin = new GraphPin("", EEdGraphPinDirection.EGPD_Output, MakePinType(ArrayPin.PinType.PinCategory)); ;
            MakePins();
        }

        protected void MakePins()
        {
            AddOutputPin(VarPin);
            AddInputPin(ArrayPin);
            AddInputPin(IndexPin);
        }
    }
}
