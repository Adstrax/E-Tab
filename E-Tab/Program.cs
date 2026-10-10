using System;
using System.Threading;
using System.Windows.Forms;
using ETab.Helpers;
using ETab.Hooks;
using ETab.Managers;

namespace ETab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Declare system DPI awareness so the tray icon is drawn at the
        // real physical size (not a DPI-virtualized 16px that gets upscaled).
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
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
        Log.Info($"E-Tab started ({typeof(Program).Assembly.GetName().Version}) from '{Environment.ProcessPath}'.");

        // If "start with Windows" points at a copy that is no longer there, it is
        // pointed at this one: otherwise Windows starts nothing at login and the
        // app looks like it did not start.
        AutoStartManager.RepairIfStale();
        // Last-resort safety net: an Explorer window this app hid must never
        // outlive the app.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Helper.RestoreAllHiddenWindows();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Helper.RestoreAllHiddenWindows();

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
        if (SettingsManager.AutoMerge) _explorerWatcher.StartHook();
        _trayIcon = new TrayIcon(_explorerWatcher!);
    }

    protected override void ExitThreadCore()
    {
        _trayIcon?.Dispose();
        _explorerWatcher?.Dispose();
        base.ExitThreadCore();
    }
}
