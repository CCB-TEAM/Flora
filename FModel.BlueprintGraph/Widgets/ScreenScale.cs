using System;

namespace FModel.BlueprintGraph.Widgets;

/// <summary>
/// How the game scales its UI to the screen. The numbers in widgets (font sizes, offsets, image sizes) are in a space where the scale is 1,
/// so a widget fills (screen size / scale) units. KARDS: DefaultEngine.ini [/Script/Engine.UserInterfaceSettings] has UIScaleRule=ShortestSide
/// and an external curve /Game/DPICurveTest, evaluated at the shortest side of the viewport in pixels.
/// </summary>
public static class ScreenScale
{
    // keys of /Game/DPICurveTest (linear): shortest side in pixels -> scale
    private static readonly (double Side, double Scale)[] Keys = [(-9.8, 0), (634.6, 0.5022), (1440, 1.0), (3687.8, 2.3972), (9814.4, 3.48)];

    public static double Scale(int screenWidth, int screenHeight)
    {
        double side = Math.Min(screenWidth, screenHeight);
        if (side <= Keys[0].Side)
            return Keys[0].Scale;
        for (var i = 1; i < Keys.Length; i++)
        {
            if (side <= Keys[i].Side)
            {
                var (x0, y0) = Keys[i - 1];
                var (x1, y1) = Keys[i];
                return y0 + (side - x0) / (x1 - x0) * (y1 - y0);
            }
        }
        return Keys[^1].Scale;
    }

    /// <summary>The size of the space a full-screen widget lays out in on a screen of this size, for example 2470 x 1389 on 1920 x 1080.</summary>
    public static (float Width, float Height) WidgetSpace(int screenWidth, int screenHeight)
    {
        var scale = Scale(screenWidth, screenHeight);
        return ((float) (screenWidth / scale), (float) (screenHeight / scale));
    }
}
