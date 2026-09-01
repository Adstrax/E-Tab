using System;
using System.Threading;
using System.Windows.Forms;
using ETab.Helpers;
using ETab.Hooks;

namespace ETab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Single instance. Keep the same mutex name as the original so a first
        // launch does not get caught by a stale WPF instance.
        using var mutex = new Mutex(true, "ETabHook__Mutex", out var createdNew);
        if (!createdNew)
        {
            Log.Info("Another E-Tab instance is already running; exiting.");
            MessageBox.Show(
                "E-Tab is already running in the system tray.",
                "E-Tab",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ThemeManager.Initialize();
        Log.Info($"E-Tab started ({typeof(Program).Assembly.GetName().Version}).");

        using var context = new ETabApplicationContext();
        Application.Run(context);
    }
}

/// <summary>
/// Owns the long-lived components for the lifetime of the tray app. Both the
/// explorer watcher and the tray icon are created here so they live for as long
/// as the process, and are released when the message loop exits.
/// </summary>
internal sealed class ETabApplicationContext : ApplicationContext
{
    private TrayIcon? _trayIcon;
    private ExplorerWatcher? _explorerWatcher;
    private bool _started;

    public ETabApplicationContext()
    {
        // ExplorerWatcher captures SynchronizationContext.Current on the thread
        // that constructs it and uses it to marshal the WinEvent hook install
        // back onto the UI thread. The WinForms message loop installs a
        // WindowsFormsSynchronizationContext before the first Idle, so start the
        // components only once the loop is actually pumping.
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        Application.Idle -= OnFirstIdle;

        _explorerWatcher = new ExplorerWatcher();
        _trayIcon = new TrayIcon();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon?.Dispose();
        _explorerWatcher?.Dispose();
        base.ExitThreadCore();
    }
}