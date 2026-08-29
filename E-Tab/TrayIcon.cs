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
    private readonly TrayMenuWindow _menuWindow;
    private ReleaseInfo? _pendingUpdate;

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
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        // Create the menu once up front so the very first right-click is fast
        // too; the window is hidden instead of recreated on every open.
        _menuWindow = new TrayMenuWindow();
        _menuWindow.ExitRequested += ExitApplication;
        _menuWindow.UpdateCheckRequested += () => _ = UpdateManager.CheckForUpdatesAsync();
        _menuWindow.UpdateInstallRequested += InstallPendingUpdate;

        UpdateManager.CheckCompleted += OnCheckCompleted;
        UpdateManager.InstallCompleted += OnInstallCompleted;
        UpdateManager.InstallFailed += OnInstallFailed;
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
        _menuWindow.ShowAtCursor();
    }

    private void OnCheckCompleted(UpdateCheckResult result)
    {
        // Balloons are WinForms components; marshal onto the UI thread.
        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.BeginInvoke(() => ShowCheckResult(result));
        else
            ShowCheckResult(result);
    }

    private void ShowCheckResult(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                _pendingUpdate = result.Release;
                _menuWindow.SetUpdateAvailable(result.Release);
                _notifyIcon.BalloonTipTitle = "E-Tab update available";
                _notifyIcon.BalloonTipText =
                    $"E-Tab {result.Release!.Version.ToString(3)} is ready. Click here to download and install.";
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(10_000);
                break;
            case UpdateCheckStatus.UpToDate:
                _notifyIcon.BalloonTipTitle = "E-Tab is up to date";
                _notifyIcon.BalloonTipText =
                    $"You are running the latest version ({UpdateManager.CurrentVersion.ToString(3)}).";
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(5_000);
                break;
            case UpdateCheckStatus.Failed:
                _notifyIcon.BalloonTipTitle = "Update check failed";
                _notifyIcon.BalloonTipText = $"Could not check for updates: {result.Message}";
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
                _notifyIcon.ShowBalloonTip(6_000);
                break;
        }
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        InstallPendingUpdate();
    }

    private void InstallPendingUpdate()
    {
        if (_pendingUpdate == null) return;

        var update = _pendingUpdate;
        _pendingUpdate = null;
        _menuWindow.SetUpdateAvailable(null);
        _ = UpdateManager.InstallUpdateAsync(update);
    }

    private void OnInstallCompleted(string version)
    {
        _notifyIcon.BalloonTipTitle = "E-Tab updated";
        _notifyIcon.BalloonTipText = $"E-Tab has been updated to v{version}.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(6_000);
    }

    private void OnInstallFailed(string message)
    {
        _notifyIcon.BalloonTipTitle = "Update failed";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(6_000);
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
        UpdateManager.CheckCompleted -= OnCheckCompleted;
        UpdateManager.InstallCompleted -= OnInstallCompleted;
        UpdateManager.InstallFailed -= OnInstallFailed;

        _menuWindow.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
