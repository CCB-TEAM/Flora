#nullable enable

using System;
using System.Windows;
using SkiaSharp;

namespace FModel.Views.BlueprintViewer;

public partial class WidgetDesignerCanvas
{
    /// <summary>
    /// Draws the current tree and animation state into a new bitmap of the given pixel size, with the design area fitted into it.
    /// The canvas does not have to be on screen, and its own pan and zoom are left as they were. Used for animation filmstrips.
    /// </summary>
    public SKBitmap RenderPreview(int width, int height, int margin = 8)
    {
        var scaling = _scaling;
        var translation = _translation;
        var footer = ShowFooter;
        var outlines = ShowOutlines;
        try
        {
            ShowFooter = false;
            ShowOutlines = false;
            _scaling = (float) Math.Min((width - 2 * margin) / (double) _designWidth, (height - 2 * margin) / (double) _designHeight);
            _translation = new Vector(-(width - _designWidth * _scaling) / 2, -(height - _designHeight * _scaling) / 2);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            Render(surface.Canvas, width, height);
            return SKBitmap.FromImage(surface.Snapshot());
        }
        finally
        {
            _scaling = scaling;
            _translation = translation;
            ShowFooter = footer;
            ShowOutlines = outlines;
        }
    }
}
