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
    private bool _promptOpen;

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

        // Create the menu once up front so the very first right-click is fast
        // too; the window is hidden instead of recreated on every open.
        _menuWindow = new TrayMenuWindow();
        _menuWindow.ExitRequested += ExitApplication;
        _menuWindow.UpdateCheckRequested += () => _ = UpdateManager.CheckForUpdatesAsync();
        _menuWindow.UpdateInstallRequested += () => StartUpdateInstall(_pendingUpdate);

        UpdateManager.CheckCompleted += OnCheckCompleted;
        UpdateManager.InstallCompleted += OnInstallCompleted;
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
                {
                    // Pick the ICO frame closest to the actual tray size so the
                    // icon is not scaled down (which makes it look blurry).
                    var traySize = SystemInformation.SmallIconSize;
                    return new Icon(stream, traySize.Width, traySize.Height);
                }
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
                ShowUpdatePrompt(result.Release);
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

    private void ShowUpdatePrompt(ReleaseInfo? update)
    {
        if (update == null || _promptOpen) return;

        _promptOpen = true;
        try
        {
            var prompt = new UpdatePromptWindow(update);
            if (prompt.ShowDialog() == true)
                StartUpdateInstall(update);
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private async void StartUpdateInstall(ReleaseInfo? update)
    {
        if (update == null) return;

        _pendingUpdate = null;
        _menuWindow.SetUpdateAvailable(null);

        var progressWindow = new UpdateProgressWindow();
        progressWindow.Show();

        try
        {
            var ok = await UpdateManager.InstallUpdateAsync(
                update,
                new Progress<(long Received, long Total)>(p => progressWindow.SetProgress(p.Received, p.Total)),
                status => progressWindow.Dispatcher.BeginInvoke(() => progressWindow.SetStatus(status)),
                progressWindow.CancellationToken);

            progressWindow.Close();
            if (!ok && !progressWindow.CancellationToken.IsCancellationRequested)
            {
                ShowUpdateError(
                    "The update could not be installed. Details are in " +
                    "%LOCALAPPDATA%\\E-Tab\\logs\\E-Tab.log.");
            }
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            ShowUpdateError($"The update could not be installed: {ex.Message}");
        }
    }

    private void OnInstallCompleted(string version)
    {
        _notifyIcon.BalloonTipTitle = "E-Tab updated";
        _notifyIcon.BalloonTipText = $"E-Tab has been updated to v{version}.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(6_000);
    }

    private void ShowUpdateError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "E-Tab update failed",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
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

        _menuWindow.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
