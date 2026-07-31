using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ETab.Helpers;

namespace ETab;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private TrayMenuWindow? _menuWindow;
    private SettingsWindow? _settingsWindow;

    public TrayIcon()
    {
        var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "E-Tab - 文件夹自动在新标签页打开",
            Visible = true
        };

        _notifyIcon.MouseUp += OnTrayMouseUp;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
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
        _menuWindow.SettingsRequested += OpenSettings;
        _menuWindow.ExitRequested += ExitApplication;
        _menuWindow.ShowAtCursor();
    }

    private static void ExitApplication()
    {
        foreach (var hWnd in Helper.HiddenWindows.Keys.ToList())
            Helper.ShowWindow(hWnd, removeCache: true);

        Environment.Exit(0);
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        _menuWindow?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
