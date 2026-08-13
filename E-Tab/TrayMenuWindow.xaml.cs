using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace ETab;

public partial class TrayMenuWindow : Window
{
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();
        var version = typeof(TrayMenuWindow).Assembly.GetName().Version;
        VersionText.Text = version == null ? string.Empty : $"v{version.ToString(3)}";
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

        Show();
        Activate();
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
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
        Close();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
        Close();
    }
}
