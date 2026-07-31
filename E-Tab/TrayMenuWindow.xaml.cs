using System;
using System.Windows;

namespace ETab;

public partial class TrayMenuWindow : Window
{
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    public void ShowAtCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        var dpiScale = graphics.DpiX / 96.0;

        Left = cursor.X / dpiScale - Width + 14;
        Top = cursor.Y / dpiScale - Height - 4;

        var workArea = SystemParameters.WorkArea;
        if (Left < workArea.Left) Left = workArea.Left + 8;
        if (Top < workArea.Top) Top = workArea.Top + 8;

        Show();
        Activate();
    }

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
