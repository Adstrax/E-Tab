using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ETab.Helpers;
using FormsTimer = System.Windows.Forms.Timer;

namespace ETab.UI;

internal sealed record TrayMenuEntry
{
    internal required string Header { get; init; }
    internal object? Icon { get; init; }
    internal string? Shortcut { get; init; }
    internal bool IsToggle { get; init; }
    internal bool IsChecked { get; init; }
    internal bool IsEnabled { get; init; } = true;
    internal bool IsDanger { get; init; }
    internal bool IsAccent { get; init; }
    internal Action? Command { get; init; }
    internal bool IsSeparator { get; init; }

    internal static TrayMenuEntry Separator() => new()
    {
        Header = string.Empty,
        IsSeparator = true,
    };
}

internal static class FluentGlyph
{
    internal const string Power = "\uE7E8";
    internal const string Switch = "\uE8AB";
    internal const string Folder = "\uE8B7";
    internal const string Sync = "\uE895";
    internal const string Download = "\uE896";
    internal const string UpdateRestore = "\uE777";
    internal const string SignOut = "\uF3B1";
}

/// <summary>
/// A non-activating Acrylic flyout for the E-Tab tray icon. Keeping the popup
/// outside the shell's native menu avoids the fixed square WinForms surface
/// while preserving the existing tray interaction.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const int MenuMinWidth = 248;
    private const int MenuMaxWidth = 304;
    private const int RowHeight = 28;
    private const int IconColumnWidth = 16;
    private const int IconSlotWidth = 30;
    private const double MenuFontSize = 13;
    private const int ScreenMargin = 8;
    private const int WhMouseLowLevel = 14;
    private const int WhKeyboardLowLevel = 13;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint VkEscape = 0x1B;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private static TrayMenuWindow? _current;

    private readonly bool _isDark;
    private readonly Brush _text;
    private readonly Brush _secondary;
    private readonly Brush _hover;
    private readonly Brush _separator;
    private readonly Brush _border;
    private readonly Brush _check;
    private readonly Brush _accentTint;
    private readonly Brush _danger;

    private FormsTimer? _closeTimer;
    private nint _mouseHook;
    private nint _keyboardHook;
    private LowLevelHookProc? _mouseProc;
    private LowLevelHookProc? _keyboardProc;
    private bool _closing;
    private bool _accentApplied;

    internal static bool IsOpen => _current?.IsVisible == true;

    private TrayMenuWindow(
        IReadOnlyList<TrayMenuEntry> entries,
        string version,
        bool isPreview,
        bool isDark)
    {
        _isDark = isDark;
        _text = Brush(isDark ? "#F5F5F5" : "#1A1A1A");
        _secondary = Brush(isDark ? "#C8C8C8" : "#5C5C5C");
        _hover = Brush(isDark ? "#20FFFFFF" : "#18000000");
        _separator = Brush(isDark ? "#1FFFFFFF" : "#18000000");
        _border = Brush(isDark ? "#30FFFFFF" : "#28000000");
        _check = Brush(isDark ? "#79DB92" : "#178A3D");
        _accentTint = Brush(isDark ? "#2460CDFF" : "#160067C0");
        _danger = Brush(isDark ? "#FF8A80" : "#C42B1C");

        Title = "E-Tab tray menu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Background = Brushes.Transparent;

        Content = new Border
        {
            Background = AcrylicMaterial.SurfaceBrush(isDark),
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = BuildMenu(entries, version, isPreview),
        };

        Closed += (_, _) =>
        {
            UninstallHooks();
            _closeTimer?.Stop();
            _closeTimer?.Dispose();
            _closeTimer = null;
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    internal static void ShowMenu(
        Point screenPoint,
        IReadOnlyList<TrayMenuEntry> entries,
        string version,
        bool isPreview)
    {
        CloseCurrent();
        var menu = new TrayMenuWindow(entries, version, isPreview, ThemeManager.IsDark);
        _current = menu;
        menu.ShowAt(screenPoint);
    }

    internal static void CloseCurrent()
    {
        _current?.CloseMenu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MarkNonActivating();
        _accentApplied = AcrylicMaterial.Apply(this, _isDark);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_accentApplied)
            _accentApplied = AcrylicMaterial.Apply(this, _isDark);
    }

    private StackPanel BuildMenu(
        IReadOnlyList<TrayMenuEntry> entries,
        string version,
        bool isPreview)
    {
        var panel = new StackPanel
        {
            MinWidth = MenuMinWidth,
            MaxWidth = MenuMaxWidth,
            Margin = new Thickness(0, 2, 0, 2),
        };
        panel.Children.Add(BuildHeader(version, isPreview));
        panel.Children.Add(BuildSeparator(new Thickness(10, 2, 10, 3)));

        foreach (var entry in entries)
        {
            panel.Children.Add(entry.IsSeparator
                ? BuildSeparator(new Thickness(12, 3, 12, 3))
                : BuildItem(entry));
        }

        return panel;
    }

    private FrameworkElement BuildHeader(string version, bool isPreview)
    {
        var grid = new Grid
        {
            Height = 32,
            Margin = new Thickness(12, 0, 12, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "E-Tab",
            Foreground = _text,
            FontSize = MenuFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var versionLabel = new TextBlock
        {
            Text = version,
            Foreground = isPreview
                ? Brush(_isDark ? "#FFCC80" : "#8A5200")
                : _secondary,
            FontSize = MenuFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(versionLabel, 1);
        grid.Children.Add(versionLabel);

        return grid;
    }

    private Border BuildItem(TrayMenuEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlotWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = BuildIcon(entry.Icon, entry.IsEnabled);
        if (icon is not null)
        {
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = entry.Header,
            Foreground = !entry.IsEnabled
                ? _secondary
                : entry.IsDanger
                    ? _danger
                    : _text,
            FontSize = MenuFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(icon is null ? 4 : 6, 0, 8, 0),
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var accessory = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (!string.IsNullOrWhiteSpace(entry.Shortcut))
        {
            accessory.Children.Add(new TextBlock
            {
                Text = entry.Shortcut,
                Foreground = _secondary,
                FontSize = MenuFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (entry.IsToggle)
        {
            accessory.Children.Add(BuildCheckmark(entry.IsChecked));
        }
        Grid.SetColumn(accessory, 2);
        grid.Children.Add(accessory);

        var normal = entry.IsAccent ? _accentTint : Brushes.Transparent;
        var item = new Border
        {
            Child = grid,
            Height = RowHeight,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(5),
            Background = normal,
            IsEnabled = entry.IsEnabled,
            Opacity = entry.IsEnabled ? 1 : 0.48,
            Cursor = entry.IsEnabled ? Cursors.Hand : Cursors.Arrow,
        };

        if (!entry.IsEnabled)
            return item;

        item.MouseEnter += (_, _) => item.Background = _hover;
        item.MouseLeave += (_, _) => item.Background = normal;
        item.MouseLeftButtonUp += (_, _) =>
        {
            var command = entry.Command;
            CloseMenu();
            command?.Invoke();
        };
        return item;
    }

    private FrameworkElement BuildCheckmark(bool isChecked)
    {
        return new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = MenuFontSize,
            Foreground = _check,
            Width = IconColumnWidth,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = isChecked ? Visibility.Visible : Visibility.Hidden,
        };
    }

    private FrameworkElement? BuildIcon(object? icon, bool isEnabled)
    {
        if (icon is string glyph && !string.IsNullOrWhiteSpace(glyph))
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = MenuFontSize,
                Foreground = isEnabled ? _text : _secondary,
                Width = IconColumnWidth,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        if (icon is ImageSource source)
        {
            return new Image
            {
                Source = source,
                Width = IconColumnWidth,
                Height = IconColumnWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return null;
    }

    private Border BuildSeparator(Thickness margin) => new()
    {
        Height = 1,
        Background = _separator,
        Margin = margin,
    };

    private void ShowAt(Point screenPoint)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        MarkNonActivating();

        var monitor = MonitorFromPoint(
            new NativePoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)),
            MonitorDefaultToNearest);
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var scale = dpi / 96d;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        _ = GetMonitorInfo(monitor, ref info);
        var workLeft = info.Work.Left / scale;
        var workTop = info.Work.Top / scale;
        var workRight = info.Work.Right / scale;
        var workBottom = info.Work.Bottom / scale;

        var content = (FrameworkElement)Content;
        content.Measure(new Size(MenuMaxWidth, double.PositiveInfinity));
        var width = Math.Clamp(content.DesiredSize.Width, MenuMinWidth, MenuMaxWidth);
        var height = content.DesiredSize.Height;
        var left = Math.Clamp(screenPoint.X / scale, workLeft + ScreenMargin,
            Math.Max(workLeft + ScreenMargin, workRight - width - ScreenMargin));
        var top = Math.Min(screenPoint.Y / scale, workBottom - height - ScreenMargin);
        top = Math.Max(workTop + ScreenMargin, top);

        Left = left;
        Top = top;

        _closeTimer = new FormsTimer { Interval = 1 };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer?.Stop();
            CloseMenu();
        };

        InstallHooks();
        Show();
    }

    private void MarkNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
            return;

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExNoActivate | WsExToolWindow;
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new nint(style));
    }

    private void InstallHooks()
    {
        if (_mouseHook != nint.Zero || _keyboardHook != nint.Zero)
            return;

        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
        var module = GetModuleHandle("user32.dll");
        _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLowLevel, _keyboardProc, module, 0);
    }

    private void UninstallHooks()
    {
        if (_mouseHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        if (_keyboardHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }
        _mouseProc = null;
        _keyboardProc = null;
    }

    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_closing &&
            (uint)wParam.ToInt64() is WmLeftButtonDown or WmRightButtonDown or
                WmMiddleButtonDown or WmXButtonDown)
        {
            var point = Marshal.PtrToStructure<MouseHookData>(lParam).Point;
            if (!ContainsPoint(point) && !IsTaskbar(point))
                ScheduleClose();
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_closing &&
            (uint)wParam.ToInt64() is WmKeyDown or WmSysKeyDown)
        {
            var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if (data.VirtualKey == VkEscape)
            {
                ScheduleClose();
                return 1;
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool ContainsPoint(NativePoint point)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect))
            return false;

        return point.X >= rect.Left && point.X <= rect.Right &&
               point.Y >= rect.Top && point.Y <= rect.Bottom;
    }

    private static bool IsTaskbar(NativePoint point)
    {
        var hwnd = WindowFromPoint(point);
        if (hwnd == nint.Zero)
            return false;

        var root = GetAncestor(hwnd, 2);
        if (root == nint.Zero)
            return false;

        var name = new System.Text.StringBuilder(64);
        _ = GetClassName(root, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "NotifyIconOverflowWindow";
    }

    private void ScheduleClose()
    {
        if (_closeTimer is { Enabled: false } timer)
            timer.Start();
    }

    private void CloseMenu()
    {
        if (_closing)
            return;

        _closing = true;
        UninstallHooks();
        if (IsVisible)
            Close();
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private delegate nint LowLevelHookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hook,
        LowLevelHookProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string module);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    private static nint GetWindowLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : SetWindowLong32(hwnd, index, value.ToInt32());
}
