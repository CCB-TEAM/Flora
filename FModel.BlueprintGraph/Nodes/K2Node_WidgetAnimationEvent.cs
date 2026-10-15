using CUE4Parse.UE4.FMod.Nodes;
using CUE4Parse.UE4.Kismet;
using NAudio.SoundFont;
using System.Collections.Generic;
using FModel.BlueprintGraph.Engine;
using static FModel.BlueprintGraph.Engine.EngineBPData;
using static FModel.BlueprintGraph.Engine.Utils;

namespace FModel.BlueprintGraph.Nodes
{
    internal class K2Node_WidgetAnimationEvent : K2Node_Event
    {
        public readonly string AnimationPropertyName;
        public readonly WidgetAnimationEventType Action;

        public K2Node_WidgetAnimationEvent(string funcName, WidgetAnimationEventType action, string animationName, string userTag, KismetExpression? instr) : base(funcName, [],
            instr)
        {
            Name = $"Animation {(action == WidgetAnimationEventType.Started ? "Started" : "Finished")} {(userTag == None ? "" : $"Tag:{userTag} ")}({animationName})";
            Action = action;
            AnimationPropertyName = animationName;
        }
    }
}
