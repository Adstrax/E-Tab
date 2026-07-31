using System;
using System.Drawing;
using System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace ETab;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;

    public TrayIcon()
    {
        var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;

        _menu = new ContextMenuStrip();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => WpfApplication.Current.Shutdown();
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "E-Tab - Open folders in new File Explorer tabs",
            ContextMenuStrip = _menu,
            Visible = true
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
