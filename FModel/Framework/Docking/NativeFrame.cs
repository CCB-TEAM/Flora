using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FModel.Framework.Docking;

/// <summary>Makes the system frame of a window match the dark application theme.</summary>
public static class NativeFrame
{
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    /// <summary>
    /// Makes Windows measure the non-client area again. A window whose style changed after it was created (the docking library
    /// creates its floating windows first) keeps the frame size it had, i.e. none.
    /// </summary>
    public static void RefreshFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>
    /// Dark title bar and borders (Windows 10 version 1903 and later); on Windows 11 the caption also takes the colors of the theme.
    /// Does nothing on systems that don't know the attributes.
    /// </summary>
    public static void ApplyDarkTheme(Window window)
    {
        void Apply()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            var dark = 1;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

            if (window.TryFindResource(AdonisUI.Brushes.Layer1BackgroundBrush) is SolidColorBrush layer && window.TryFindResource(AdonisUI.Brushes.ForegroundBrush) is SolidColorBrush text)
            {
                var caption = ToColorRef(layer.Color);
                var foreground = ToColorRef(text.Color);
                DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, DwmBorderColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, DwmTextColor, ref foreground, sizeof(int));
            }
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>An entry added to the system menu of a window (right click on the title bar).</summary>
    public sealed record SystemMenuItem(string Text, Action Run, Func<bool> IsChecked = null);

    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern uint CheckMenuItem(IntPtr menu, uint id, uint flags);

    private const uint MfString = 0x0, MfSeparator = 0x800, MfChecked = 0x8, MfUnchecked = 0x0, MfByCommand = 0x0;
    private const int WmSysCommand = 0x0112, WmInitMenuPopup = 0x0117, FirstCommandId = 0x1000;

    /// <summary>Appends entries (after a separator) to the system menu of the window.</summary>
    public static void AddSystemMenu(Window window, IReadOnlyList<SystemMenuItem> items)
    {
        void Install()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
                return;

            var menu = GetSystemMenu(handle, false);
            if (menu == IntPtr.Zero)
                return;
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            for (var i = 0; i < items.Count; i++)
                AppendMenu(menu, MfString, (UIntPtr) (uint) (FirstCommandId + i * 16), items[i].Text);

            source.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                switch (message)
                {
                    case WmSysCommand:
                    {
                        // the low four bits belong to the system
                        var command = ((int) wParam & 0xFFF0) - FirstCommandId;
                        if (command >= 0 && command % 16 == 0 && command / 16 < items.Count)
                        {
                            handled = true;
                            items[command / 16].Run();
                        }
                        break;
                    }
                    case WmInitMenuPopup when wParam == menu:
                        for (var i = 0; i < items.Count; i++)
                            if (items[i].IsChecked != null)
                                CheckMenuItem(menu, (uint) (FirstCommandId + i * 16), MfByCommand | (items[i].IsChecked() ? MfChecked : MfUnchecked));
                        break;
                }
                return IntPtr.Zero;
            });
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Install();
        else
            window.SourceInitialized += (_, _) => Install();
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
