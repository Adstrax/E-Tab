using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ETab.Helpers;

namespace ETab;

/// <summary>
/// Minimal WinForms tray icon. The UI is a plain system-themed context menu:
/// the tray menu is rarely inspected, so looks do not matter and this keeps the
/// resident footprint far smaller than a WPF window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;
    private bool _disposed;

    public TrayIcon()
    {
        _icon = LoadTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "E-Tab - Open folders in new tabs",
            Visible = true,
        };

        var version = typeof(TrayIcon).Assembly.GetName().Version;
        _autoStartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _autoStartItem.Click += (_, _) => AutoStartManager.SetEnabled(_autoStartItem.Checked);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(new ToolStripMenuItem($"E-Tab  v{version?.ToString(3) ?? "?"}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => _autoStartItem.Checked = AutoStartManager.IsEnabled();

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _menu.Show(Cursor.Position);
        };
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // Fall through to the default application icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void ExitApplication()
    {
        // Never leave Explorer windows hidden when the app goes away.
        foreach (var hWnd in Helper.HiddenWindows.Keys.ToList())
            Helper.ShowWindow(hWnd, removeCache: true);

        Log.Info("Exit requested from tray menu.");
        // Graceful exit so the watcher, hooks, COM objects and the single-instance
        // mutex are released (ApplicationContext.ExitThreadCore runs).
        Application.ExitThread();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}