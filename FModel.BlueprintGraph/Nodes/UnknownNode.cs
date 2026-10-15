using CUE4Parse.UE4.Kismet;

namespace FModel.BlueprintGraph.Nodes
{
    internal class UnknownNode : BPNode
    {
        public UnknownNode(string Name, KismetExpression Instr) : base(Name, Instr)
        {
            MakePins(true, true);
        }
    }
}
