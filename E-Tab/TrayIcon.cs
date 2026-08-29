using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
                    // Extract the exact ICO frame matching the physical tray
                    // size and build the HICON from it directly. Relying on
                    // Icon(stream, w, h) can pick the wrong PNG frame and
                    // scale it down, which is what made the tray icon blurry.
                    var bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);

                    var frame = PickIconFrame(bytes, GetTrayIconSize());
                    if (frame != null)
                    {
                        using var png = new MemoryStream(frame);
                        using var bmp = new Bitmap(png);
                        var handle = bmp.GetHicon();
                        try
                        {
                            using var temp = Icon.FromHandle(handle);
                            return (Icon)temp.Clone();
                        }
                        finally
                        {
                            DestroyIcon(handle);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to the default application icon.
        }

        return (Icon)SystemIcons.Application.Clone();
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

    /// <summary>
    /// Picks the ICO frame whose size is closest to the target and returns its
    /// raw PNG payload (this project's ICO stores PNG-compressed frames).
    /// </summary>
    private static byte[]? PickIconFrame(byte[] ico, int target)
    {
        var count = BitConverter.ToUInt16(ico, 4);
        var best = -1;
        var bestScore = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + i * 16;
            var width = ico[offset] == 0 ? 256 : ico[offset];
            var height = ico[offset + 1] == 0 ? 256 : ico[offset + 1];
            var score = Math.Abs(Math.Max(width, height) - target);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0) return null;

        var entry = 6 + best * 16;
        var length = BitConverter.ToInt32(ico, entry + 8);
        var start = BitConverter.ToInt32(ico, entry + 12);
        var data = new byte[length];
        Buffer.BlockCopy(ico, start, data, 0, length);
        return data;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);

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
