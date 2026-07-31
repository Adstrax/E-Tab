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
    public static readonly ConcurrentDictionary<nint, RECT?> HiddenWindows = new();

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

    public static IEnumerable<nint> GetAllExplorerWindows()
    {
        return WinApi.FindAllWindowsEx("CabinetWClass");
    }

    public static IEnumerable<nint> GetAllExplorerTabs(nint window)
    {
        return WinApi.FindAllWindowsEx("ShellTabWindowClass", window);
    }

    public static Task<nint> ListenForNewExplorerTabAsync(
        nint window,
        IReadOnlyCollection<nint> currentTabs,
        int searchTimeMs = 1_000)
    {
        return DoUntilNotDefaultAsync(
            () => GetAllExplorerTabs(window).Except(currentTabs).FirstOrDefault(),
            searchTimeMs);
    }

    public static void HideWindow(nint hWnd)
    {
        if (HiddenWindows.TryGetValue(hWnd, out var existing) && existing != null)
            return;

        var originalPos = WinApi.GetWindowRect(hWnd, out var rect) ? (RECT?)rect : null;
        if (originalPos != null)
        {
            const uint flags = WinApi.SWP_HIDEWINDOW |
                               WinApi.SWP_NOSIZE |
                               WinApi.SWP_NOZORDER |
                               WinApi.SWP_NOACTIVATE |
                               WinApi.SWP_FRAMECHANGED;
            WinApi.SetWindowPos(hWnd, 0, -32_000, -32_000, 0, 0, flags);
        }
        else
        {
            // The window may be too new for GetWindowRect, so hide it directly.
            WinApi.ShowWindow(hWnd, WinApi.SW_HIDE);
        }

        HiddenWindows[hWnd] = originalPos;
    }

    public static bool ShowWindow(nint hWnd, bool removeCache)
    {
        if (!HiddenWindows.TryGetValue(hWnd, out var originalPos))
            return WinApi.ShowWindow(hWnd, WinApi.SW_SHOWNOACTIVATE);

        if (removeCache)
            HiddenWindows.TryRemove(hWnd, out _);

        if (originalPos is not { } position)
            return WinApi.ShowWindow(hWnd, WinApi.SW_SHOWNOACTIVATE);

        const uint flags = WinApi.SWP_SHOWWINDOW |
                           WinApi.SWP_NOSIZE |
                           WinApi.SWP_NOZORDER |
                           WinApi.SWP_NOACTIVATE |
                           WinApi.SWP_FRAMECHANGED;
        return WinApi.SetWindowPos(hWnd, 0, position.Left, position.Top, 0, 0, flags);
    }

    public static void BypassWinForegroundRestrictions()
    {
        // Simulate a key press to bypass the foreground restriction.
        KeyboardSimulator.SendKeyPress(VirtualKey.F23);
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
        var id = Registry.CurrentUser
            .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")
            ?.GetValue("LaunchTo") as int? ?? 1;

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
                    best = proc;
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
