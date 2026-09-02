using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ETab.Helpers;

namespace ETab;

/// <summary>
/// Minimal WinForms tray icon. The tray menu is rarely inspected, so the menu
/// is a plain system-themed context menu. The tray icon itself is drawn at the
/// exact physical size the notification area uses at the current DPI so it
/// stays crisp (no downscaling) and fills the cell (not small).
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

    /// <summary>
    /// Builds the tray icon at the exact physical pixel size for the current DPI.
    /// Pixel-snapped, hard-edged two-folder mark (back folder + front folder)
    /// matching the app icon, with a green status dot at 24px and up.
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var bmp = DrawTrayIcon(GetTrayIconSize());
            var hicon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hicon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }
        catch
        {
            // Fall back to the default application icon if drawing fails.
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    /// <summary>
    /// Physical pixel size of the tray icon for the current system DPI
    /// (16 logical px at 100%, scaled up on high-DPI displays).
    /// </summary>
    private static int GetTrayIconSize()
    {
        var dpi = GetDpiForSystem();
        if (dpi == 0)
            return Math.Max(16, SystemInformation.SmallIconSize.Width);
        return Math.Max(16, (int)Math.Round(16.0 * dpi / 96.0));
    }

    private static Bitmap DrawTrayIcon(int size)
    {
        var scale = size / 32.0;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        // Hard-edged (pixel-snapped) so the small tray mark stays crisp at 1:1.
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.Transparent);

        int P(double v) => Math.Max(0, (int)Math.Round(v * scale));

        using var blue = new SolidBrush(Color.FromArgb(255, 0, 120, 212));
        using var white = new SolidBrush(Color.FromArgb(255, 255, 255, 255));

        // Back folder (blue), peeking top-right behind the front folder.
        FillRoundedRect(g, P(8), P(6), P(20), P(14), P(2), blue);
        FillRoundedRect(g, P(8), P(3), P(9), P(5), P(2), blue);

        // Front folder (white), bottom-left, overlapping the back folder.
        FillRoundedRect(g, P(3), P(11), P(18), P(14), P(2), white);
        FillRoundedRect(g, P(3), P(8), P(9), P(5), P(2), white);

        if (size >= 24)
        {
            using var green = new SolidBrush(Color.FromArgb(255, 52, 199, 89));
            g.FillRectangle(green, P(16), P(19), P(5), P(5));
        }

        return bmp;
    }

    private static void FillRoundedRect(Graphics g, int x, int y, int w, int h, int r, Brush brush)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
    {
        var maxR = Math.Min(w / 2, h / 2);
        r = Math.Max(1, Math.Min(r, maxR));
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);

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