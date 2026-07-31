using System;
using System.Linq;
using System.Threading;
using System.Windows;
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
            MessageBox.Show(
                "E-Tab is already running in the system tray.",
                "E-Tab",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _explorerWatcher = new ExplorerWatcher();
        _trayIcon = new TrayIcon();

        if (e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)))
            _trayIcon.OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _explorerWatcher?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
