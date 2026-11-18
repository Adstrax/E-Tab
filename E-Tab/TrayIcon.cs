using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ETab.Helpers;
using ETab.Hooks;
using ETab.Managers;
using ETab.UI;
using ETab.WinAPI;

namespace ETab;

/// <summary>
/// Hosts the notification icon and its WPF-only tray flyout while retaining the
/// lightweight WinForms application loop. The tray icon itself is drawn at the
/// exact physical size the notification area uses at the current DPI so it
/// stays crisp (no downscaling) and fills the cell.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan FirstAutomaticUpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly HotkeyWindow? _hotkey;
    private readonly ExplorerWatcher _watcher;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _automaticUpdateCancellation;
    private Task? _automaticUpdateTask;
    private UpdateInfo? _downloadedUpdate;
    private string? _downloadedUpdatePath;
    private bool _updateOperationInProgress;
    private bool _disposed;

    public TrayIcon(ExplorerWatcher watcher)
    {
        _watcher = watcher;
        _icon = LoadTrayIcon();
        var version = UpdateManager.DisplayVersion;
        var isPreviewBuild = UpdateManager.IsPreviewVersion;
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = isPreviewBuild
                ? $"E-Tab {version} - Preview build"
                : $"E-Tab {version} - Open folders in new tabs",
            Visible = true,
        };

        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right))
                return;

            if (TrayMenuWindow.IsOpen)
            {
                TrayMenuWindow.CloseCurrent();
                return;
            }

            TrayMenuWindow.ShowMenu(
                new System.Windows.Point(Cursor.Position.X, Cursor.Position.Y),
                BuildMenuEntries(),
                version,
                isPreviewBuild);
        };

        _hotkey = new HotkeyWindow(_watcher, RestoreTrayIcon);
        StartAutomaticUpdates();
    }

    private IReadOnlyList<TrayMenuEntry> BuildMenuEntries()
    {
        var entries = new List<TrayMenuEntry>
        {
            new()
            {
                Header = "Start with Windows",
                Icon = FluentGlyph.Power,
                IsToggle = true,
                IsChecked = AutoStartManager.IsEnabled(),
                Command = ToggleStartWithWindows,
            },
            new()
            {
                Header = "Auto-merge new windows",
                Icon = FluentGlyph.Switch,
                IsToggle = true,
                IsChecked = _watcher.IsHookActive,
                Command = ToggleAutoMerge,
            },
            TrayMenuEntry.Separator(),
            new()
            {
                Header = "Merge all windows",
                Icon = FluentGlyph.Folder,
                Shortcut = "Ctrl+Shift+E",
                Command = _watcher.MergeAllWindowsNow,
            },
            TrayMenuEntry.Separator(),
            new()
            {
                Header = "Automatic updates",
                Icon = FluentGlyph.Sync,
                IsToggle = true,
                IsChecked = SettingsManager.AutoUpdate,
                Command = ToggleAutomaticUpdates,
            },
        };

        if (_downloadedUpdate is not null && _downloadedUpdatePath is not null)
        {
            entries.Add(new TrayMenuEntry
            {
                Header = $"Restart to install v{_downloadedUpdate.Version.ToString(3)}",
                Icon = FluentGlyph.UpdateRestore,
                IsAccent = true,
                Command = InstallDownloadedUpdate,
            });
        }

        entries.Add(new TrayMenuEntry
        {
            Header = _updateOperationInProgress ? "Update check in progress..." : "Check for updates",
            Icon = FluentGlyph.Download,
            IsEnabled = !_updateOperationInProgress,
            Command = () => _ = CheckForUpdatesAsync(),
        });
        entries.Add(TrayMenuEntry.Separator());
        entries.Add(new TrayMenuEntry
        {
            Header = "Exit",
            Icon = FluentGlyph.SignOut,
            IsDanger = true,
            Command = ExitApplication,
        });
        return entries;
    }

    private void ToggleStartWithWindows()
    {
        var enabled = !AutoStartManager.IsEnabled();
        AutoStartManager.SetEnabled(enabled);
        Log.Info("Start with Windows changed to " + enabled);
    }

    private void ToggleAutoMerge()
    {
        var enabled = !_watcher.IsHookActive;
        if (enabled)
            _watcher.StartHook();
        else
            _watcher.StopHook();
        Log.Info("Auto-merge setting changed to " + enabled);
    }

    private void ToggleAutomaticUpdates()
    {
        var enabled = !SettingsManager.AutoUpdate;
        SettingsManager.AutoUpdate = enabled;
        Log.Info("Automatic update setting changed to " + enabled);
        if (enabled)
            StartAutomaticUpdates();
        else
            StopAutomaticUpdates();
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
            return Math.Max(32, SystemInformation.SmallIconSize.Width);
        // Draw at least 32px so the shell downscales (crisp) instead of
        // upscaling a tiny bitmap (blurry) for the tray/flyout cell.
        return Math.Max(32, (int)Math.Round(16.0 * dpi / 96.0));
    }


    private static Bitmap DrawTrayIcon(int size)
    {
        var scale = size / 256.0d;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        // C2: a deck of two stacked tabs; mid-blue back peeks up-right, white
        // front sits on top. Anti-aliased and auto-centred so it fills the
        // square cell at any DPI and never looks small.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        const double tab = 220, dx = 36, dy = -36, radius = 24; // ~92% fill
        var minX = Math.Min(0, dx);
        var minY = Math.Min(0, dy);
        var maxX = Math.Max(tab, dx + tab);
        var maxY = Math.Max(tab, dy + tab);
        var tx = size / 2.0 - (minX + maxX) / 2.0 * scale;
        var ty = size / 2.0 - (minY + maxY) / 2.0 * scale;

        using (var back = new SolidBrush(Color.FromArgb(255, 0, 120, 212)))
            FillRoundedRect(g, tx + dx * scale, ty + dy * scale, tab * scale, tab * scale, radius * scale, back);

        using (var front = new SolidBrush(Color.White))
            FillRoundedRect(g, tx, ty, tab * scale, tab * scale, radius * scale, front);

        return bmp;
    }

    private static void FillRoundedRect(Graphics g, double x, double y, double w, double h, double r, Brush brush)
    {
        using var path = RoundedRectPath((float)x, (float)y, (float)w, (float)h, (float)r);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
    {
        var maxR = Math.Min(w / 2f, h / 2f);
        r = Math.Clamp(r, 1f, maxR);
        var path = new GraphicsPath();
        var d = r * 2f;
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
        Helper.RestoreAllHiddenWindows();

        Log.Info("Exit requested from tray menu.");
        // Graceful exit so the watcher, hooks, COM objects and the single-instance
        // mutex are released (ApplicationContext.ExitThreadCore runs).
        Application.ExitThread();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_disposed || _updateOperationInProgress) return;
        if (_downloadedUpdate is not null)
        {
            PromptToInstallDownloadedUpdate();
            return;
        }

        _updateOperationInProgress = true;
        try
        {
            var update = await UpdateManager.CheckAsync(_lifetimeCancellation.Token);
            if (update is null)
            {
                MessageBox.Show(
                    $"E-Tab {UpdateManager.DisplayVersion} is up to date.",
                    "E-Tab update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(
                $"{update.DisplayName} is available.\n\n" +
                $"Installed: {UpdateManager.DisplayVersion}\n" +
                $"Latest: {update.Version.ToString(3)}\n\n" +
                "Download it now? E-Tab will restart after installing the update.",
                "E-Tab update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var downloadedExecutable = await UpdateManager.DownloadExecutableAsync(
                update,
                _lifetimeCancellation.Token);

            StoreDownloadedUpdate(update, downloadedExecutable, notifyUser: false);

            UpdateManager.StartUpdater(downloadedExecutable);
            Log.Info($"Update {update.Version} was downloaded; restarting to install it.");
            ExitApplication();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The app is exiting; there is nobody to show an error to.
        }
        catch (Exception ex)
        {
            Log.Error("Checking for or downloading an update failed.", ex);
            MessageBox.Show(
                "E-Tab could not check for or download the update.\n\n" + ex.Message,
                "E-Tab update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _updateOperationInProgress = false;
        }
    }

    private void StartAutomaticUpdates()
    {
        if (_disposed || !SettingsManager.AutoUpdate)
            return;
        if (_automaticUpdateTask is { IsCompleted: false })
            return;

        _automaticUpdateCancellation?.Cancel();
        _automaticUpdateCancellation?.Dispose();
        _automaticUpdateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _automaticUpdateTask = RunAutomaticUpdateLoopAsync(
            _automaticUpdateCancellation.Token);
    }

    private void StopAutomaticUpdates()
    {
        var cancellation = _automaticUpdateCancellation;
        _automaticUpdateCancellation = null;
        _automaticUpdateTask = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task RunAutomaticUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstAutomaticUpdateCheckDelay, cancellationToken);

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await CheckAndDownloadAutomaticallyAsync(cancellationToken);
                await Task.Delay(AutomaticUpdateCheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling automatic updates or exiting stops the loop promptly.
        }
        catch (Exception ex)
        {
            Log.Warn("The automatic update loop stopped unexpectedly: " + ex.Message);
        }
    }

    private async Task CheckAndDownloadAutomaticallyAsync(CancellationToken cancellationToken)
    {
        if (!SettingsManager.AutoUpdate ||
            _downloadedUpdate is not null ||
            _updateOperationInProgress)
        {
            return;
        }

        _updateOperationInProgress = true;
        try
        {
            var update = await UpdateManager.CheckAsync(cancellationToken);
            if (update is null)
            {
                Log.Info("Automatic update check found no newer stable release.");
                return;
            }

            Log.Info($"Automatic update check found {update.Version}; downloading it.");
            var downloadedExecutable = await UpdateManager.DownloadExecutableAsync(
                update,
                cancellationToken);

            if (!SettingsManager.AutoUpdate || cancellationToken.IsCancellationRequested)
                return;

            StoreDownloadedUpdate(update, downloadedExecutable, notifyUser: true);
            Log.Info($"Update {update.Version} was downloaded and is ready to install.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the user turns automatic updates off or exits.
        }
        catch (Exception ex)
        {
            // Background failures are expected to include being offline. Keep
            // them out of the user's way and retry on the next scheduled check.
            Log.Warn("Automatic update check or download failed; will retry later: " + ex.Message);
        }
        finally
        {
            _updateOperationInProgress = false;
        }
    }

    private void StoreDownloadedUpdate(
        UpdateInfo update,
        string downloadedExecutable,
        bool notifyUser)
    {
        _downloadedUpdate = update;
        _downloadedUpdatePath = downloadedExecutable;

        if (!notifyUser || _disposed)
            return;

        _notifyIcon.BalloonTipTitle = "E-Tab update ready";
        _notifyIcon.BalloonTipText =
            $"Version {update.Version.ToString(3)} was downloaded and verified. " +
            "Open the tray menu and choose Restart to install.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(10_000);
    }

    private void PromptToInstallDownloadedUpdate()
    {
        var version = _downloadedUpdate?.Version.ToString(3) ?? "new";
        var answer = MessageBox.Show(
            $"E-Tab {version} has been downloaded and verified.\n\n" +
            "Restart E-Tab now to install it?",
            "E-Tab update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            InstallDownloadedUpdate();
    }

    private void InstallDownloadedUpdate()
    {
        var downloadedExecutable = _downloadedUpdatePath;
        if (!string.IsNullOrWhiteSpace(downloadedExecutable) &&
            System.IO.File.Exists(downloadedExecutable))
        {
            try
            {
                UpdateManager.StartUpdater(downloadedExecutable);
                Log.Info("Restarting to install the downloaded update.");
                ExitApplication();
            }
            catch (Exception ex)
            {
                Log.Error("Starting the downloaded update failed.", ex);
                MessageBox.Show(
                    "E-Tab could not start the update installer.\n\n" + ex.Message,
                    "E-Tab update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return;
        }

        _downloadedUpdate = null;
        _downloadedUpdatePath = null;
        MessageBox.Show(
            "The downloaded update is no longer available. " +
            "E-Tab will check for it again later.",
            "E-Tab update",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lifetimeCancellation.Cancel();
        StopAutomaticUpdates();
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        _hotkey?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    /// <summary>
    /// Puts the tray icon back after Explorer has restarted.
    ///
    /// The notification area belongs to Explorer: when it restarts - and on this
    /// machine it does, several times a day - every icon in it disappears, and
    /// an application only learns about it from this message. Without this the
    /// icon never came back at all: the app kept running, but from the user's
    /// side it looked like it had not started.
    /// </summary>
    private void RestoreTrayIcon()
    {
        if (_disposed) return;

        try
        {
            // Toggling it makes the icon be added to the new taskbar even when
            // this object still believes it is showing one.
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
            Log.Info("The taskbar was recreated; the tray icon was put back.");
        }
        catch (Exception ex)
        {
            Log.Warn($"The tray icon could not be put back: {ex.Message}");
        }
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int HotkeyId = 0xE01;
        private readonly ExplorerWatcher _watcher;
        private readonly Action _onTaskbarCreated;
        private readonly int _taskbarCreatedMessage = WinApi.RegisterWindowMessage("TaskbarCreated");

        public HotkeyWindow(ExplorerWatcher watcher, Action onTaskbarCreated)
        {
            _watcher = watcher;
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams());
            if (!WinApi.RegisterHotKey(Handle, HotkeyId, WinApi.MOD_CONTROL | WinApi.MOD_SHIFT, WinApi.VK_E))
                Log.Warn("Failed to register Ctrl+Shift+E hotkey.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WinApi.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                _watcher.MergeAllWindowsNow();
                return;
            }

            if (_taskbarCreatedMessage != 0 && m.Msg == _taskbarCreatedMessage)
            {
                _onTaskbarCreated();
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != 0)
                WinApi.UnregisterHotKey(Handle, HotkeyId);
            DestroyHandle();
        }
    }
}
