using System;
using System.Threading;
using System.Windows;
using ETab.Helpers;
using ETab.Hooks;

namespace ETab;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _trayIcon;
    private ExplorerWatcher? _explorerWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ETabHook__Mutex", out var createdNew);

        if (!createdNew)
        {
            Log.Info("Another E-Tab instance is already running; exiting.");
            MessageBox.Show(
                "E-Tab is already running in the system tray.",
                "E-Tab",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ThemeManager.Initialize(this);
        Log.Info($"E-Tab started ({GetType().Assembly.GetName().Version}).");
        _explorerWatcher = new ExplorerWatcher();
        _trayIcon = new TrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("E-Tab exiting.");
        _trayIcon?.Dispose();
        _explorerWatcher?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
