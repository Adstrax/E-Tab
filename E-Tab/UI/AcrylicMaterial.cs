using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ETab.UI;

/// <summary>
/// Applies the same non-layered WCA acrylic recipe used by the reusable WCA
/// reference, without taking a dependency on another UI toolkit.
/// </summary>
internal static class AcrylicMaterial
{
    private const double MenuOpacity = 0.30;
    private static readonly Color LightTint = Color.FromRgb(0xF8, 0xF6, 0xF4);
    private static readonly Color DarkTint = Color.FromRgb(0x20, 0x24, 0x2A);
    private static readonly Brush LightFallback = Solid(LightTint);
    private static readonly Brush DarkFallback = Solid(DarkTint);

    internal static Brush SurfaceBrush(bool isDark)
    {
        if (!SystemParameters.IsGlassEnabled)
            return isDark ? DarkFallback : LightFallback;

        return Brushes.Transparent;
    }

    internal static bool Apply(Window window, bool isDark)
    {
        ArgumentNullException.ThrowIfNull(window);

        var tint = isDark ? DarkTint : LightTint;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == nint.Zero || !SystemParameters.IsGlassEnabled)
            return FallBack(window, tint);

        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        window.Background = Brushes.Transparent;
        var margins = new Margins(-1, -1, -1, -1);
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        SetDarkMode(hwnd, isDark);

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var captionColor = -2;
            _ = DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
            var corner = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        }

        var accent = new AccentPolicy
        {
            State = 4,
            GradientColor = ToAbgr(tint, MenuOpacity),
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = pointer,
                Size = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0 ||
                   FallBack(window, tint);
        }
        catch (EntryPointNotFoundException)
        {
            return FallBack(window, tint);
        }
        catch (DllNotFoundException)
        {
            return FallBack(window, tint);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool FallBack(Window window, Color color)
    {
        window.Background = Solid(color);
        return false;
    }

    private static void SetDarkMode(nint hwnd, bool enabled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        var value = enabled ? 1 : 0;
        var attribute = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) ? 20 : 19;
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
    }

    private static uint ToAbgr(Color color, double opacity)
    {
        var alpha = (uint)Math.Round(255 * opacity);
        return color.R | ((uint)color.G << 8) | ((uint)color.B << 16) | (alpha << 24);
    }

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        nint hwnd,
        ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left;
        public int Right = right;
        public int Top = top;
        public int Bottom = bottom;
    }
}
