using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.BlueprintGraph.Animation;

/// <summary>
/// One call of UserWidget::PlayAnimation (or its Forward, Reverse and TimeRange variants) found in a blueprint's bytecode.
/// A UMG animation lasts timeline length / <see cref="Speed"/> on screen: the speed is an argument of this call, not data in the animation.
/// Arguments that are not constants in the bytecode are null.
/// </summary>
public sealed record PlayCall(string Function, string Call, string Animation, bool Reverse, double? StartAt, double? EndAt, int? Loops, double? Speed)
{
    public override string ToString()
    {
        var parts = new List<string> { Reverse ? "reverse" : "forward" };
        if (Speed != null)
            parts.Add($"speed {Speed:0.###}");
        if (StartAt is > 0)
            parts.Add($"start at {StartAt:0.###} s");
        if (EndAt is > 0)
            parts.Add($"end at {EndAt:0.###} s");
        if (Loops != null && Loops != 1)
            parts.Add(Loops == 0 ? "loops forever" : $"{Loops} loops");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Finds the PlayAnimation calls in the functions of a widget blueprint (read only). Parameter order, from UserWidget.h:
/// PlayAnimation(animation, StartAtTime, NumLoopsToPlay, PlayMode, PlaybackSpeed, bRestoreState),
/// PlayAnimationTimeRange(animation, StartAtTime, EndAtTime, NumLoopsToPlay, PlayMode, PlaybackSpeed, bRestoreState),
/// PlayAnimationForward and PlayAnimationReverse(animation, PlaybackSpeed, bRestoreState).
/// Cross-checked against an independent scan of all 186 animated game widgets (docs/data/kards-play-animation-calls.csv).
/// </summary>
public static class PlayCallScanner
{
    private static readonly Dictionary<Type, FieldInfo[]> ChildFields = [];

    public static List<PlayCall> Scan(UWidgetBlueprintGeneratedClass widgetClass)
    {
        var result = new List<PlayCall>();
        foreach (var function in widgetClass.Owner?.GetExports().OfType<UFunction>() ?? [])
        {
            if (function.ScriptBytecode == null)
                continue;
            foreach (var statement in function.ScriptBytecode)
                Walk(statement, function.Name, result);
        }
        return result;
    }

    private static void Walk(KismetExpression? expression, string function, List<PlayCall> result)
    {
        if (expression == null)
            return;

        var (name, parameters) = expression switch
        {
            EX_FinalFunction final => (final.StackNode?.Name, final.Parameters),
            EX_VirtualFunction virtualFunction => (virtualFunction.VirtualFunctionName.Text, virtualFunction.Parameters),
            _ => (null, null),
        };
        if (name != null && parameters is { Length: > 0 } && Parse(function, name, parameters) is { } call)
            result.Add(call);

        foreach (var child in Children(expression))
            Walk(child, function, result);
    }

    private static PlayCall? Parse(string function, string name, KismetExpression[] p)
    {
        if (name.StartsWith("Queue", StringComparison.Ordinal))
            name = name["Queue".Length..];

        double? Number(int index) => index < p.Length ? NumberOf(p[index]) : null;
        var animation = VariableName(p[0]);
        if (animation == null)
            return null;

        switch (name)
        {
            case "PlayAnimation":
                return new PlayCall(function, name, animation, Number(3) == 1, Number(1), null, (int?) Number(2), Number(4));
            case "PlayAnimationTimeRange":
                return new PlayCall(function, name, animation, Number(4) == 1, Number(1), Number(2), (int?) Number(3), Number(5));
            case "PlayAnimationForward":
                return new PlayCall(function, name, animation, false, null, null, null, Number(1));
            case "PlayAnimationReverse":
                return new PlayCall(function, name, animation, true, null, null, null, Number(1));
            default:
                return null;
        }
    }

    private static double? NumberOf(KismetExpression e) => e switch
    {
        EX_FloatConst f => f.Value,
        EX_DoubleConst d => d.Value,
        EX_IntConst i => i.Value,
        EX_IntConstByte b => b.Value,
        EX_ByteConst b => b.Value,
        EX_IntZero => 0,
        EX_IntOne => 1,
        _ => null,
    };

    private static string? VariableName(KismetExpression e)
    {
        if (e is not EX_VariableBase variable)
            return null;
        return variable.Variable?.New?.Path is { Length: > 0 } path ? path[0].Text : variable.Variable?.Old?.Name;
    }

    private static IEnumerable<KismetExpression> Children(KismetExpression expression)
    {
        var type = expression.GetType();
        if (!ChildFields.TryGetValue(type, out var fields))
        {
            fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => typeof(KismetExpression).IsAssignableFrom(f.FieldType) || typeof(KismetExpression[]).IsAssignableFrom(f.FieldType))
                .ToArray();
            ChildFields[type] = fields;
        }

        foreach (var field in fields)
        {
            switch (field.GetValue(expression))
            {
                case KismetExpression one:
                    yield return one;
                    break;
                case KismetExpression[] many:
                    foreach (var item in many)
                        if (item != null)
                            yield return item;
                    break;
            }
        }
    }
}
