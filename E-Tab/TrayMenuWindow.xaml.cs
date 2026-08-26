using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ETab.Helpers;
using ETab.WinAPI;

namespace ETab;

public partial class TrayMenuWindow : Window
{
    public event Action? ExitRequested;

    private bool _suppressToggle;

    public TrayMenuWindow()
    {
        InitializeComponent();
        var version = typeof(TrayMenuWindow).Assembly.GetName().Version;
        VersionText.Text = version == null ? string.Empty : $"v{version.ToString(3)}";
        ThemeManager.ThemeChanged += ApplyBackdrop;
        SourceInitialized += (_, _) => ApplyBackdrop();
        ContentRendered += (_, _) => ApplyBackdrop();
        Closed += (_, _) => ThemeManager.ThemeChanged -= ApplyBackdrop;
    }

    private void ApplyBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;

        WinApi.MakeWindowBackdropVisible(this);
        WinApi.ExtendGlassFrame(handle);
        WinApi.ApplyRoundedCorners(handle);
        WinApi.ApplyLegacyAcrylic(handle, ThemeManager.GetAcrylicTint(tray: true));
    }

    public void ShowAtCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var dpiScale = GetDpiScaleAt(cursor);

        Left = cursor.X / dpiScale - Width + 14;
        Top = cursor.Y / dpiScale - Height - 4;

        var workArea = SystemParameters.WorkArea;
        if (Left < workArea.Left) Left = workArea.Left + 8;
        if (Top < workArea.Top) Top = workArea.Top + 8;

        _suppressToggle = true;
        AutoStartToggle.IsChecked = AutoStartManager.IsEnabled();
        _suppressToggle = false;

        Show();
        Activate();
    }

    private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AutoStartManager.SetEnabled(AutoStartToggle.IsChecked == true);
    }

    private static double GetDpiScaleAt(System.Drawing.Point point)
    {
        const uint monitorDefaultToNearest = 2;
        const int effectiveDpi = 0;

        var monitor = MonitorFromPoint(point, monitorDefaultToNearest);
        if (monitor == 0) return 1.0;
        if (GetDpiForMonitor(monitor, effectiveDpi, out var dpiX, out _) != 0) return 1.0;
        return dpiX / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private void OnDeactivated(object sender, EventArgs e)
    {
        Hide();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
        Close();
    }
}
