using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ETab.Helpers;

namespace ETab;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private TrayMenuWindow? _menuWindow;

    public TrayIcon()
    {
        _icon = LoadTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "E-Tab - Open folders in new tabs",
            Visible = true
        };

        _notifyIcon.MouseUp += OnTrayMouseUp;
        _notifyIcon.DoubleClick += (_, _) => ShowMenu();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Icon.ico"));
            if (info?.Stream is { } stream)
            {
                using (stream)
                    return new Icon(stream, 32, 32);
            }
        }
        catch
        {
            // Fall through to the default application icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
            ShowMenu();
    }

    private void ShowMenu()
    {
        _menuWindow?.Close();
        _menuWindow = new TrayMenuWindow();
        _menuWindow.ExitRequested += ExitApplication;
        _menuWindow.ShowAtCursor();
    }

    private static void ExitApplication()
    {
        foreach (var hWnd in Helper.HiddenWindows.Keys.ToList())
            Helper.ShowWindow(hWnd, removeCache: true);

        Log.Info("Exit requested from tray menu.");
        // Use a graceful shutdown so App.OnExit runs and releases the tray
        // icon, WinEvent hooks, COM objects and the single-instance mutex.
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _menuWindow?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
