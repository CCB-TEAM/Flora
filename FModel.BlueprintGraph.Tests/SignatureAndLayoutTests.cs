using FModel.BlueprintGraph.Signatures;
using FModel.BlueprintGraph.Widgets;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public class EngineParamNamesTests
{
    [Theory]
    [InlineData("/Script/Engine.KismetMathLibrary", "Less_IntInt", new[] { "A", "B" })]
    [InlineData("/Script/Engine.KismetMathLibrary", "BooleanAND", new[] { "A", "B" })]
    [InlineData("/Script/Engine.KismetMathLibrary", "Not_PreBool", new[] { "A" })]
    [InlineData("/Script/Engine.KismetMathLibrary", "Conv_IntToByte", new[] { "InInt" })]
    [InlineData("/Script/Engine.KismetStringLibrary", "Conv_BoolToString", new[] { "InBool" })]
    [InlineData("/Script/Engine.KismetArrayLibrary", "Array_Get", new[] { "TargetArray", "Index" })]
    [InlineData("/Script/Engine.KismetArrayLibrary", "Array_Shuffle", new[] { "TargetArray" })]
    [InlineData("/Script/Engine.KismetSystemLibrary", "IsValid", new[] { "Object" })]
    public void KnownFunctions_HaveInputNames(string outer, string function, string[] expected)
    {
        Assert.Equal(expected, EngineParamNames.GetInputNames(outer, function));
    }

    [Theory]
    [InlineData("/Script/Engine.KismetMathLibrary", "RandomIntegerInRange")]
    [InlineData("/Script/Engine.KismetMathLibrary", "Conv_VectorToRotator")] // parameter is InVec, not InVector
    [InlineData("/Script/MyGame.MyLibrary", "DoThing")]
    public void UnknownFunctions_KeepGenericNames(string outer, string function)
    {
        Assert.Null(EngineParamNames.GetInputNames(outer, function));
    }

    [Theory]
    [InlineData("/Script/Engine.KismetMathLibrary", "Add_IntInt", true)]
    [InlineData("/Script/Engine.KismetArrayLibrary", "Array_Length", true)]
    [InlineData("/Script/Engine.KismetArrayLibrary", "Array_Add", false)]
    [InlineData("/Script/Engine.KismetSystemLibrary", "MakeLiteralInt", true)]
    [InlineData("/Script/Engine.KismetSystemLibrary", "PrintString", false)]
    public void Purity_ComesFromKnownEngineFunctions(string outer, string function, bool pure)
    {
        Assert.Equal(pure, EngineParamNames.IsKnownPure(outer, function));
    }

    [Theory]
    [InlineData("GetChildrenComponents", true)]
    [InlineData("K2_GetActorLocation", true)]
    [InlineData("IsValid", true)]
    [InlineData("HasAuthority", true)]
    [InlineData("GetAllActorsOfClass", false)]
    [InlineData("Getaway", false)]
    [InlineData("SetActorLocation", false)]
    public void GetterNames_AreGuessedPure(string function, bool pure)
    {
        Assert.Equal(pure, EngineParamNames.LooksLikePureGetter(function));
    }
}

public class WidgetLayoutTests
{
    private static readonly WidgetLayout Layout = new((_, text, size, _) => (text.Length * size * 0.5f, size * 1.2f));

    private static WidgetNode Node(string className, params WidgetNode[] children)
    {
        var node = new WidgetNode { Name = className, ClassName = className };
        foreach (var child in children)
        {
            child.Parent = node;
            node.Children.Add(child);
        }
        return node;
    }

    [Fact]
    public void CanvasChild_WithoutSlotData_UsesUmgDefaults()
    {
        // CanvasPanelSlot defaults: anchors (0,0), offsets (0,0,100,30), alignment (0,0)
        var child = Node("Spacer");
        var root = Node("CanvasPanel", child);
        Layout.Layout(root, 1920, 1080);

        Assert.Equal((0f, 0f, 100f, 30f), (child.X, child.Y, child.Width, child.Height));
    }

    [Fact]
    public void Overlay_DefaultsToTopLeftAtDesiredSize()
    {
        var child = Node("Spacer"); // desired 1x1
        var root = Node("Overlay", child);
        Layout.Layout(root, 200, 100);

        Assert.Equal((0f, 0f, 1f, 1f), (child.X, child.Y, child.Width, child.Height));
    }

    [Fact]
    public void VerticalBox_StacksAutoSizedChildren()
    {
        var first = Node("ProgressBar"); // desired 100x16
        var second = Node("ProgressBar");
        var root = Node("VerticalBox", first, second);
        Layout.Layout(root, 300, 200);

        Assert.Equal(0f, first.Y);
        Assert.Equal(16f, second.Y);
        Assert.Equal(300f, second.Width); // box slots fill horizontally by default
    }

    [Fact]
    public void UnknownGameWidget_ResolvesThroughSuperClasses()
    {
        var layout = new WidgetLayout((_, _, s, _) => (0, s), name => name switch
        {
            "CommonTextBlock" => "TextBlock",
            _ => null,
        });
        Assert.Equal(WidgetKind.TextBlock, layout.GetKind(Node("CommonTextBlock")));
        Assert.Equal(WidgetKind.Unknown, layout.GetKind(Node("SomethingElse")));
    }
}
