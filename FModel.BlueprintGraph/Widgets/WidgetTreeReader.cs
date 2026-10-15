using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.BlueprintGraph.Widgets;

public class WidgetNode
{
    public string Name = "";
    public string ClassName = "";
    public UObject? Export;

    /// <summary>Slot connecting this widget to its parent panel (CanvasPanelSlot, VerticalBoxSlot...).</summary>
    public UObject? Slot;
    public string SlotClassName => Slot?.ExportType ?? "";

    public WidgetNode? Parent;
    public readonly List<WidgetNode> Children = [];

    /// <summary>Instance of another widget blueprint; its children come from that blueprint's own tree.</summary>
    public UWidgetBlueprintGeneratedClass? UserWidgetClass;

    /// <summary>Set when a nested user widget wasn't expanded (depth limit, cycle or missing tree).</summary>
    public string? Note;

    // layout results, in design space
    public float X, Y, Width, Height;
    public float DesiredWidth, DesiredHeight;
    public bool IsVisible = true;

    /// <summary>
    /// Scale applied to the children (ScaleBox): children are laid out at their own size in a local space
    /// that maps to this widget's space as origin + (p - origin) * scale.
    /// </summary>
    public float ContentScaleX = 1, ContentScaleY = 1, ContentOriginX, ContentOriginY;
    public bool HasContentTransform => ContentScaleX != 1 || ContentScaleY != 1;

    /// <summary>Maps a point from this widget's children space to its own space.</summary>
    public (float X, float Y) ContentToParent(float x, float y) =>
        (ContentOriginX + (x - ContentOriginX) * ContentScaleX, ContentOriginY + (y - ContentOriginY) * ContentScaleY);

    /// <summary>Maps a point from this widget's space to its children space.</summary>
    public (float X, float Y) ParentToContent(float x, float y) =>
        (ContentOriginX + (x - ContentOriginX) / ContentScaleX, ContentOriginY + (y - ContentOriginY) / ContentScaleY);

    /// <summary>Rectangle in design space, including ancestor ScaleBox transforms.</summary>
    public (float X, float Y, float Width, float Height) DesignRect()
    {
        float left = X, top = Y, right = X + Width, bottom = Y + Height;
        for (var ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!ancestor.HasContentTransform) continue;
            (left, top) = ancestor.ContentToParent(left, top);
            (right, bottom) = ancestor.ContentToParent(right, bottom);
        }
        return (left, top, right - left, bottom - top);
    }

    /// <summary>ESlateVisibility of the widget (Visible, Collapsed, Hidden, HitTestInvisible, SelfHitTestInvisible).</summary>
    public string Visibility => WidgetProps.Enum(Export, "Visibility", "Visible");

    public bool IsCollapsed => Visibility == "Collapsed";
    public bool IsUserWidget => UserWidgetClass != null;

    public string DisplayName => IsUserWidget ? $"{Name} [{ClassName}]" : Name == ClassName ? Name : $"{Name} [{ClassName}]";

    public IEnumerable<WidgetNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    public override string ToString() => DisplayName;
}

public class WidgetBinding
{
    public string ObjectName = "";
    public string PropertyName = "";
    public string FunctionName = "";
    public override string ToString() => $"{ObjectName}.{PropertyName} <- {FunctionName}";
}

public class WidgetBlueprintInfo
{
    public WidgetNode? Root;
    public readonly List<WidgetBinding> Bindings = [];
    public readonly List<string> Animations = [];
}

/// <summary>
/// Reads the designer hierarchy of a cooked widget blueprint:
/// WidgetBlueprintGeneratedClass.WidgetTree -> RootWidget -> panel Slots[] -> slot Content -> ...
/// </summary>
public static class WidgetTreeReader
{
    public const int DefaultNestedDepth = 3;

    public static UWidgetBlueprintGeneratedClass? FindWidgetClass(IPackage package)
    {
        return package.GetExports().OfType<UWidgetBlueprintGeneratedClass>().FirstOrDefault();
    }

    public static WidgetBlueprintInfo Read(UWidgetBlueprintGeneratedClass widgetClass, int nestedDepth = DefaultNestedDepth)
    {
        var info = new WidgetBlueprintInfo
        {
            Root = ReadTree(widgetClass, nestedDepth, [widgetClass.GetPathName()]),
        };

        foreach (var binding in WidgetProps.Structs(widgetClass, "Bindings"))
        {
            info.Bindings.Add(new WidgetBinding
            {
                ObjectName = WidgetProps.Text(binding, "ObjectName"),
                PropertyName = WidgetProps.Text(binding, "PropertyName"),
                FunctionName = WidgetProps.Text(binding, "FunctionName"),
            });
        }

        foreach (var animation in WidgetProps.Objects(widgetClass, "Animations"))
            info.Animations.Add(animation.Name);

        return info;
    }

    private static WidgetNode? ReadTree(UWidgetBlueprintGeneratedClass widgetClass, int nestedDepth, HashSet<string> visited)
    {
        var tree = WidgetProps.Object(widgetClass, "WidgetTree");
        if (tree == null)
        {
            // older cooks keep the tree as a plain export of the package only
            tree = widgetClass.Owner?.GetExports().FirstOrDefault(o => o.ExportType == "WidgetTree");
        }

        var root = WidgetProps.Object(tree, "RootWidget");
        return root == null ? null : ReadWidget(root, null, null, nestedDepth, visited);
    }

    private static WidgetNode ReadWidget(UObject widget, UObject? slot, WidgetNode? parent, int nestedDepth, HashSet<string> visited)
    {
        var node = new WidgetNode
        {
            Name = widget.Name,
            ClassName = widget.ExportType,
            Export = widget,
            Slot = slot,
            Parent = parent,
        };

        // panels list their children through slots; content widgets (Border, Button, SizeBox...) use the same array with one slot
        foreach (var childSlot in WidgetProps.Objects(widget, "Slots"))
        {
            var content = WidgetProps.Object(childSlot, "Content");
            if (content != null)
                node.Children.Add(ReadWidget(content, childSlot, node, nestedDepth, visited));
        }

        if (widget.Class?.Object?.Value is UWidgetBlueprintGeneratedClass userWidgetClass)
        {
            node.UserWidgetClass = userWidgetClass;
            var path = userWidgetClass.GetPathName();
            if (nestedDepth <= 0)
            {
                node.Note = "nested widget not expanded (depth limit)";
            }
            else if (!visited.Add(path))
            {
                node.Note = "nested widget not expanded (recursive)";
            }
            else
            {
                try
                {
                    var nestedRoot = ReadTree(userWidgetClass, nestedDepth - 1, visited);
                    if (nestedRoot != null)
                    {
                        nestedRoot.Parent = node;
                        node.Children.Add(nestedRoot);
                    }
                    else
                    {
                        node.Note = "nested widget has no widget tree";
                    }
                }
                catch (Exception e)
                {
                    node.Note = $"failed to read nested widget: {e.Message}";
                }
                finally
                {
                    visited.Remove(path);
                }
            }
        }

        return node;
    }
}
