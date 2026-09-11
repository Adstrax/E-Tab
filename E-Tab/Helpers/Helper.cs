using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ETab.Interop;
using ETab.WinAPI;
using Microsoft.Win32;

namespace ETab.Helpers;

public static class Helper
{
    /// <summary>
    /// A window this app has taken off the screen, plus everything needed to
    /// put it back exactly where Explorer meant to open it.
    /// </summary>
    /// <param name="HiddenAtTicks">When the window was last touched by this app.</param>
    /// <param name="WasMinimized">Whether the window was minimized before it was hidden.</param>
    /// <param name="OriginalRect">Where the window was before it was moved out of the way.</param>
    /// <param name="Parked">Whether the window was moved off screen.</param>
    /// <param name="Hidden">Whether the window was hidden with SW_HIDE.</param>
    public readonly record struct HiddenWindow(
        long HiddenAtTicks,
        bool WasMinimized,
        WinApi.RECT OriginalRect,
        bool Parked,
        bool Hidden);

    public static readonly ConcurrentDictionary<nint, HiddenWindow> HiddenWindows = new();

    // Far enough outside any real desktop that a window parked here cannot be
    // seen on any monitor, but still a normal window position that Explorer
    // can be moved back from without recalculating the frame.
    private const int OffScreenX = -32_000;
    private const int OffScreenY = -32_000;

    public static async Task<T> DoUntilNotDefaultAsync<T>(
        Func<T> action,
        int timeMs = 500,
        int sleepMs = 20,
        CancellationToken cancellationToken = default)
    {
        return await DoUntilConditionAsync(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }

    public static async Task<T> DoUntilNotDefaultAsync<T>(
        Func<T> action,
        Predicate<T> predicate,
        int timeMs = 500,
        int sleepMs = 20,
        CancellationToken cancellationToken = default)
    {
        return await DoUntilConditionAsync(action, predicate, timeMs, sleepMs, cancellationToken);
    }

    private static async Task<T> DoUntilConditionAsync<T>(
        Func<T> action,
        Predicate<T> predicate,
        int timeMs = 500,
        int sleepMs = 20,
        CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result)) return result;
            await Task.Delay(sleepMs, cancellationToken);
        }

        return action();
    }

    public static bool IsTimeUp(long startTicks, int timeMs)
    {
        return Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds >= timeMs;
    }

    public static bool IsFileExplorerWindow(nint window)
    {
        return window != 0 && WinApi.IsWindowHasClassName(window, "CabinetWClass");
    }

    public static IEnumerable<nint> GetAllExplorerTabs(nint window)
    {
        return WinApi.FindAllWindowsEx("ShellTabWindowClass", window);
    }

    public static Task<nint> ListenForNewExplorerTabAsync(
        nint window,
        IReadOnlyCollection<nint> currentTabs,
        int searchTimeMs = 1_000,
        int sleepMs = 20)
    {
        return DoUntilNotDefaultAsync(
            () => GetAllExplorerTabs(window).Except(currentTabs).FirstOrDefault(),
            searchTimeMs,
            sleepMs);
    }

    /// <summary>
    /// Moves a File Explorer window that has just been created - but not
    /// revealed yet - out of the way.
    ///
    /// Explorer creates the frame window and only shows it a moment later
    /// (measured: 130-250 ms), and once it is shown the window stays on screen
    /// for as long as Explorer keeps its own thread busy: a hide request sent
    /// at that point waits behind that work, which measured 30-110 ms of a
    /// window the user can see, i.e. a flash. Moving the window while it is
    /// still invisible costs nothing, is already applied by the time Explorer
    /// shows it, and the window is then never painted on screen at all.
    ///
    /// Only the position is touched: size, frame and Z-order are left alone,
    /// which is what kept the older SWP_FRAMECHANGED version from garbling the
    /// layout of the window it was applied to.
    /// </summary>
    /// <returns>True when the window was moved out of the way.</returns>
    public static bool ParkWindow(nint hWnd)
    {
        if (hWnd == 0) return false;
        if (!WinApi.IsWindow(hWnd)) return false;
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return false;

        // Never touch a window the user can already see, and never take over a
        // window this app is already tracking.
        if (WinApi.IsWindowVisible(hWnd)) return false;
        if (!WinApi.GetWindowRect(hWnd, out var original)) return false;
        if (original.Right <= original.Left || original.Bottom <= original.Top) return false;

        if (!HiddenWindows.TryAdd(
                hWnd,
                new HiddenWindow(Stopwatch.GetTimestamp(), false, original, Parked: true, Hidden: false)))
            return false;

        MoveOffScreen(hWnd);
        return true;
    }

    /// <summary>
    /// Hides an Explorer window without touching its geometry or its frame.
    ///
    /// The window used to be moved to (-32000, -32000) with SWP_FRAMECHANGED,
    /// which forced Explorer to recalculate its non-client area while the
    /// window was still initializing. That could leave the tab strip and
    /// address bar laid out for the wrong size after the window was restored,
    /// and it left fully off-screen windows behind whenever a merge was
    /// abandoned. SW_HIDE keeps size, position, Z-order and the
    /// maximized/minimized state untouched, so restoring it is exact.
    /// </summary>
    /// <returns>True when this call hid the window.</returns>
    public static bool HideWindow(nint hWnd) => HideWindowCore(hWnd, hideInBackground: false);

    /// <summary>
    /// Same as HideWindow, except that the window call itself is made on a
    /// background thread.
    ///
    /// Explorer is normally still finishing the window when it is hidden here,
    /// and SW_HIDE then waits for Explorer's thread to service the request
    /// (measured 30-110 ms). Nothing may sit on that wait: the merge that turns
    /// the window into a tab starts from the caller, and every millisecond spent
    /// waiting is added to how long the user waits for their folder. The
    /// bookkeeping is still done by the caller, so the window counts as hidden
    /// immediately either way.
    /// </summary>
    public static bool HideWindowInBackground(nint hWnd) => HideWindowCore(hWnd, hideInBackground: true);

    private static bool HideWindowCore(nint hWnd, bool hideInBackground)
    {
        if (hWnd == 0) return false;
        if (!WinApi.IsWindow(hWnd)) return false;
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return false;

        if (HiddenWindows.TryGetValue(hWnd, out var tracked))
        {
            if (tracked.Hidden)
            {
                // Explorer does not always reveal a window in one step: when it
                // shows a window this app is already hiding, it has to be hidden
                // again, otherwise it sits on screen for the rest of the merge.
                Hide(hWnd, hideInBackground);
                return false;
            }

            // Parked before Explorer revealed it: this is the first moment the
            // window could have been seen, and therefore the moment the merge
            // may take it over.
            Hide(hWnd, hideInBackground);
            HiddenWindows[hWnd] = tracked with { Hidden = true, HiddenAtTicks = Stopwatch.GetTimestamp() };
            return true;
        }

        // Only ever hide a real, visible Explorer window: a window the user has
        // not seen yet must never end up in the restore list.
        if (!WinApi.IsWindowVisible(hWnd)) return false;

        var state = new HiddenWindow(
            Stopwatch.GetTimestamp(),
            WinApi.IsIconic(hWnd),
            default,
            Parked: false,
            Hidden: true);
        if (!HiddenWindows.TryAdd(hWnd, state)) return false;

        Hide(hWnd, hideInBackground);
        return true;
    }

    private static void Hide(nint hWnd, bool inBackground)
    {
        if (!inBackground)
        {
            if (WinApi.IsWindowVisible(hWnd))
                WinApi.ShowWindow(hWnd, WinApi.SW_HIDE);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Re-validated on this thread: the window may have been
                // destroyed, or its handle recycled, while the request was
                // queued, and this app only ever hides windows it still tracks.
                if (HiddenWindows.ContainsKey(hWnd) &&
                    WinApi.IsWindow(hWnd) &&
                    WinApi.IsWindowHasClassName(hWnd, "CabinetWClass") &&
                    WinApi.IsWindowVisible(hWnd))
                {
                    WinApi.ShowWindow(hWnd, WinApi.SW_HIDE);
                }
            }
            catch
            {
                // Hiding must never throw.
            }
        });
    }

    private static void MoveOffScreen(nint hWnd)
    {
        WinApi.SetWindowPos(
            hWnd,
            0,
            OffScreenX,
            OffScreenY,
            0,
            0,
            WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Puts a window hidden by this app back on screen.
    ///
    /// Windows the app never hid are left alone, and the window is never
    /// activated: SW_SHOWNOACTIVATE re-shows it with the exact size, position
    /// and state it had before it was hidden. The handle is validated first so
    /// a recycled handle can never make an unrelated window appear.
    /// </summary>
    public static bool ShowWindow(nint hWnd, bool removeCache)
    {
        if (!HiddenWindows.TryGetValue(hWnd, out var state)) return false;

        if (removeCache)
            HiddenWindows.TryRemove(hWnd, out _);

        if (!WinApi.IsWindow(hWnd)) return false;
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return false;

        // A window that was parked has to be put back where Explorer meant to
        // open it, otherwise the user gets it somewhere else entirely. The move
        // happens while the window is still hidden, so nothing jumps on screen.
        if (state.Parked)
        {
            WinApi.SetWindowPos(
                hWnd,
                0,
                state.OriginalRect.Left,
                state.OriginalRect.Top,
                0,
                0,
                WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
        }

        return WinApi.ShowWindow(
            hWnd,
            state.WasMinimized ? WinApi.SW_SHOWMINNOACTIVE : WinApi.SW_SHOWNOACTIVATE);
    }

    /// <summary>
    /// Restores every window this app is still hiding. Used when the app exits
    /// (and as a last-resort safety net); a watcher re-initialization restores
    /// windows one by one instead, so a running merge is not flashed back on
    /// screen.
    /// </summary>
    public static void RestoreAllHiddenWindows()
    {
        foreach (var hWnd in HiddenWindows.Keys.ToList())
            ShowWindow(hWnd, removeCache: true);
    }

    /// <summary>
    /// True for a window that was moved out of the way but not hidden: it still
    /// reports itself as visible, so it must never be picked as the window a
    /// merge targets or as the window a merge should be hosted in.
    /// </summary>
    public static bool IsParkedOffScreen(nint hWnd)
        => HiddenWindows.TryGetValue(hWnd, out var state) && state.Parked && !state.Hidden;

    /// <summary>
    /// True when the window is being tracked at all (parked, hidden, or both).
    /// </summary>
    public static bool IsTracked(nint hWnd) => HiddenWindows.ContainsKey(hWnd);

    /// <summary>
    /// True when the window has actually been hidden, as opposed to only moved
    /// out of the way before Explorer revealed it.
    /// </summary>
    public static bool IsHidden(nint hWnd)
        => HiddenWindows.TryGetValue(hWnd, out var state) && state.Hidden;

    /// <summary>
    /// Safety net for a window that was parked but that Explorer never went on
    /// to reveal: if nothing else has claimed it, put it back where Explorer
    /// meant to open it, so the user is never left without their window.
    ///
    /// The window is not forced on screen - whatever Explorer was going to do
    /// with it still happens, only at the position it was meant to appear at.
    /// </summary>
    public static void UnparkIfUntouched(nint hWnd)
    {
        if (!HiddenWindows.TryGetValue(hWnd, out var state)) return;
        if (!state.Parked || state.Hidden) return;

        if (!WinApi.IsWindow(hWnd))
        {
            HiddenWindows.TryRemove(hWnd, out _);
            return;
        }

        // Explorer revealed it after all, so the normal hide path owns it now.
        if (WinApi.IsWindowVisible(hWnd)) return;

        WinApi.SetWindowPos(
            hWnd,
            0,
            state.OriginalRect.Left,
            state.OriginalRect.Top,
            0,
            0,
            WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
        HiddenWindows[hWnd] = state with { Parked = false };
        Log.Info($"Window 0x{hWnd:X} was never revealed; put back at {state.OriginalRect.Left},{state.OriginalRect.Top}.");
    }

    public static string NormalizeLocation(string location)
    {
        if (location.IndexOf('%') > -1)
            location = Environment.ExpandEnvironmentVariables(location);

        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                location = new Uri(location).LocalPath;
            }
            catch
            {
                location = location.Substring("file:".Length);
            }
        }

        if (location.StartsWith("::", StringComparison.Ordinal))
            location = $"shell:{location}";
        else if (location.StartsWith("{", StringComparison.Ordinal))
            location = $"shell:::{location}";

        location = location.Trim(' ', '/', '\\', '\n', '\'', '"');
        return location.Replace('/', '\\');
    }

    public static string GetDefaultExplorerLocation(ShellPathComparer? shellPathComparer = null)
    {
        using var advancedKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var id = advancedKey?.GetValue("LaunchTo") as int? ?? 1;

        var location = id switch
        {
            2 => "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", // Home / Quick Access
            3 => "shell:::{088E3905-0323-4B02-9826-5D99428E115F}", // Downloads
            4 => "shell:::{018D5C66-4533-4307-9B53-224DE2ED1FE6}", // OneDrive
            _ => "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"  // This PC
        };

        if (shellPathComparer == null)
            return location;

        var pidl = shellPathComparer.GetPidlFromPath(location);
        if (pidl == 0) return location;

        try
        {
            var path = ShellPathComparer.GetPathFromPidl(pidl);
            return NormalizeLocation(path ?? location);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pidl);
        }
    }

    public static Process? GetMainExplorerProcess()
    {
        Process? best = null;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expectedPath = System.IO.Path.Combine(windowsFolder, "explorer.exe");
        var bestStart = DateTime.MaxValue;

        foreach (var hWnd in WinApi.FindAllWindowsEx("Shell_TrayWnd"))
        {
            if (WinApi.GetWindowThreadProcessId(hWnd, out var pid) <= 0) continue;

            var processPath = WinApi.GetProcessPath((int)pid);
            if (!string.Equals(processPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var proc = Process.GetProcessById((int)pid);
                if (proc.StartTime < bestStart)
                {
                    bestStart = proc.StartTime;
                    best?.Dispose();
                    best = proc;
                }
                else
                {
                    proc.Dispose();
                }
            }
            catch
            {
                // The process may have terminated.
            }
        }

        return best;
    }
}
