using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Diagnostics;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ETab.Interop;
using ETab.Managers;
using ETab.Models;
using ETab.WinAPI;

namespace ETab.Helpers;

public static class Helper
{
    private static int _lastCtrlShiftCheckAt;
    private static bool _lastCtrlShiftCheckValue;
    public static readonly ConcurrentDictionary<nint, RECT?> HiddenWindows = new();

    public static Task DoDelayedBackgroundAsync(Action action, int delayMs = 2_000, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            await Task.Delay(delayMs, cancellationToken);
            action();
        }, cancellationToken);
    }
    public static Task DoDelayedBackgroundAsync(Func<Task> action, int delayMs = 2_000, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            await Task.Delay(delayMs, cancellationToken);
            await action();
        }, cancellationToken);
    }
    public static Task<T> DoDelayedBackgroundAsync<T>(Func<Task<T>> action, int delayMs = 2_000, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            await Task.Delay(delayMs, cancellationToken);
            return await action();
        }, cancellationToken);
    }

    public static T DoUntilNotDefault<T>(Func<T> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilCondition(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }
    public static void DoUntilTimeEnd(Action action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        DoUntilCondition(action, static () => false, timeMs, sleepMs, cancellationToken);
    }
    public static void DoUntilCondition(Action action, Func<bool> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            action();
            if (predicate())
                return;

            Thread.Sleep(sleepMs);
        }
    }
    public static T DoUntilCondition<T>(Func<T> action, Predicate<T> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result))
                return result;

            Thread.Sleep(sleepMs);
        }

        return action();
    }
    public static void DoIfCondition(Action action, Func<bool> predicate, bool justOnce = false, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            if (predicate())
            {
                action();

                if (justOnce) return;
            }
            Thread.Sleep(sleepMs);
        }
    }
    public static Task<T> DoUntilNotDefaultAsync<T>(Func<Task<T>> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilConditionAsync(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }
    public static Task<T> DoUntilNotDefaultAsync<T>(Func<T> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilConditionAsync(
            action,
            result => !EqualityComparer<T?>.Default.Equals(result, default),
            timeMs,
            sleepMs,
            cancellationToken);
    }
    public static Task DoUntilTimeEndAsync(Func<Task> action, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        return DoUntilConditionAsync(action, static () => false, timeMs, sleepMs, cancellationToken);
    }
    public static async Task DoUntilConditionAsync(Func<Task> action, Func<bool> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            await action();
            if (predicate())
                return;

            await Task.Delay(sleepMs);
        }
    }
    public static async Task<T> DoUntilConditionAsync<T>(Func<T> action, Predicate<T> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = action();
            if (predicate(result))
                return result;

            await Task.Delay(sleepMs);
        }

        return action();
    }
    public static async Task<T> DoUntilConditionAsync<T>(Func<Task<T>> action, Predicate<T> predicate, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            var result = await action();
            if (predicate(result))
                return result;

            await Task.Delay(sleepMs);
        }

        return await action();
    }
    public static async Task DoIfConditionAsync(Func<Task> action, Func<bool> predicate, bool justOnce = false, int timeMs = 500, int sleepMs = 20, CancellationToken cancellationToken = default)
    {
        var startTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !IsTimeUp(startTicks, timeMs))
        {
            if (predicate())
            {
                await action();

                if (justOnce) return;
            }
            await Task.Delay(sleepMs);
        }
    }

    public static bool IsTimeUp(long startTicks, int timeMs)
    {

#if NET7_0_OR_GREATER
        var elapsedTime = Stopwatch.GetElapsedTime(startTicks);
#else
        var elapsedTime = GetElapsedTime(startTicks);
#endif

        return elapsedTime.TotalMilliseconds >= timeMs;
    }
    public static TimeSpan GetElapsedTime(long startTicks)
    {
        var tickFrequency = (double)10_000_000 / Stopwatch.Frequency;
        return new TimeSpan((long)((Stopwatch.GetTimestamp() - startTicks) * tickFrequency));
    }
    public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
    {
        if (val.CompareTo(min) < 0) return min;
        if (val.CompareTo(max) > 0) return max;
        return val;
    }

    public static Icon? GetIcon() => Icon.ExtractAssociatedIcon(GetExecutablePath());
    public static string GetEnumDescription(Enum value)
    {
        var fieldInfo = value.GetType().GetField(value.ToString());
        return fieldInfo?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString();
    }
    public static bool IsExplorerEmptySpace(Point point)
    {
        var hr = WinApi.AccessibleObjectFromPoint(point, out var accObj, out var childId);
        if (hr != 0 || childId is not 0) return false;

        var role = accObj.get_accRole(0);
        return role is 0x21; //IAccessible.Role:list (ROLE_SYSTEM_LIST 0x21)
    }
    public static bool IsFileExplorerTab(nint tab)
    {
        return tab != 0 && WinApi.IsWindowHasClassName(tab, "ShellTabWindowClass");
    }
    public static bool IsFileExplorerWindow(nint window)
    {
        return window != 0 && WinApi.IsWindowHasClassName(window, "CabinetWClass");
    }
    public static bool IsFileExplorerForeground(out nint foregroundWindow)
    {
        foregroundWindow = WinApi.GetForegroundWindow();
        return IsFileExplorerWindow(foregroundWindow);
    }
    public static nint GetAnotherExplorerWindow(nint currentWindow)
    {
        return currentWindow == 0
            ? WinApi.FindWindow("CabinetWClass", null)
            : GetAllExplorerWindows()
                .FirstOrDefault(window => window != currentWindow);
    }
    public static Task<nint> ListenForNewExplorerWindowAsync(IReadOnlyCollection<nint> currentWindows, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerWindows()
                    .Except(currentWindows)
                    .FirstOrDefault(),
            searchTimeMs);
    }

    public static nint ListenForNewExplorerTab(IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefault(() =>
                GetAllExplorerTabs()
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static Task<nint> ListenForNewExplorerTabAsync(IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerTabs()
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static Task<nint> ListenForNewExplorerTabAsync(nint window, IReadOnlyCollection<nint> currentTabs, int searchTimeMs = 1000)
    {
        return DoUntilNotDefaultAsync(() =>
                GetAllExplorerTabs(window)
                    .Except(currentTabs)
                    .FirstOrDefault(),
            searchTimeMs);
    }
    public static List<nint> GetAllExplorerTabs()
    {
        var tabs = new List<nint>();

        foreach (var window in GetAllExplorerWindows())
            tabs.AddRange(GetAllExplorerTabs(window));

        return tabs;
    }
    public static IEnumerable<nint> GetAllExplorerTabs(nint window)
    {
        return WinApi.FindAllWindowsEx("ShellTabWindowClass", window);
    }
    public static IEnumerable<nint> GetAllExplorerWindows()
    {
        return WinApi.FindAllWindowsEx("CabinetWClass");
    }
    public static Process? GetMainExplorerProcess()
    {
        Process? best = null;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var expectedPath = System.IO.Path.Combine(windowsFolder, "explorer.exe");
        var bestStart = DateTime.MaxValue;

        foreach (var hWnd in WinApi.FindAllWindowsEx("Shell_TrayWnd")) // Taskbar
        {
            if (WinApi.GetWindowThreadProcessId(hWnd, out var pid) <= 0) continue;
        
            var processPath = WinApi.GetProcessPath((int)pid);
            if (!string.Equals(processPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                // Pick the earliest start
                var proc = Process.GetProcessById((int)pid);
                if (proc.StartTime < bestStart)
                {
                    bestStart = proc.StartTime;
                    best = proc;
                }
            }
            catch { /* The Process might have terminated */ }
        }
        return best;
    }
    
    public static void UpdateWindowLayered(nint hWnd, bool remove)
    {
        var exStyle = WinApi.GetWindowLong(hWnd, WinApi.GWL_EXSTYLE);
        var isLayered = (exStyle & WinApi.WS_EX_LAYERED) != 0;
        
        if (remove && isLayered) // Remove
            WinApi.SetWindowLong(hWnd, WinApi.GWL_EXSTYLE, exStyle & ~WinApi.WS_EX_LAYERED);
        
        if (!remove && !isLayered) // Add
            WinApi.SetWindowLong(hWnd, WinApi.GWL_EXSTYLE, exStyle | WinApi.WS_EX_LAYERED);
    }
    public static void HideWindow(nint hWnd, bool keepTheme = false)
    {
        HiddenWindows.GetOrAdd(hWnd, static (hWnd, keepTheme) =>
        {
            if (keepTheme)
            {
                WinApi.GetWindowRect(hWnd, out var originalPos);
                HiddenWindows[hWnd] = originalPos;

                // Move it off-screen
                const uint flags = WinApi.SWP_HIDEWINDOW | WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_FRAMECHANGED;
                WinApi.SetWindowPos(hWnd, 0, -32_000, -32_000, 0, 0, flags);
                return originalPos;
            }

            // Set the transparency (alpha value) of the window (0 = transparent, 255 = opaque)
            UpdateWindowLayered(hWnd, remove: false);
            WinApi.SetLayeredWindowAttributes(hWnd, 0, 0, WinApi.LWA_ALPHA);
            return null;
        }, keepTheme);
    }
    public static bool ShowWindow(nint hWnd, bool removeCache)
    {
        if (!HiddenWindows.TryGetValue(hWnd, out var originalPos))
            return false;

        if (removeCache)
            HiddenWindows.TryRemove(hWnd, out _);

        if (originalPos != null) // keep theme
        {
            const uint flags = WinApi.SWP_SHOWWINDOW | WinApi.SWP_NOSIZE | WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_FRAMECHANGED;
            WinApi.SetWindowPos(hWnd, 0, originalPos.Value.Left, originalPos.Value.Top, 0, 0, flags);
            return true;
        }

        WinApi.SetLayeredWindowAttributes(hWnd, 0, 255, WinApi.LWA_ALPHA);
        return true;
    }

    public static bool IsCtrlShiftDown()
    {
        if (_lastCtrlShiftCheckValue && Environment.TickCount - _lastCtrlShiftCheckAt < 1_000)
            return true;
        
        _lastCtrlShiftCheckValue =
            (KeyboardSimulator.IsKeyPressed((int)VirtualKey.LeftControl) || KeyboardSimulator.IsKeyPressed((int)VirtualKey.RightControl)) &&
               (KeyboardSimulator.IsKeyPressed((int)VirtualKey.LeftShift) || KeyboardSimulator.IsKeyPressed((int)VirtualKey.RightShift));
        
        _lastCtrlShiftCheckAt = Environment.TickCount;
        return _lastCtrlShiftCheckValue;
    }
    public static void BypassWinForegroundRestrictions()
    {
        // Simulate a key press to bypass the Foreground restriction
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow#remarks
        KeyboardSimulator.SendKeyPress(VirtualKey.F23);
    }

    public static string NormalizeLocation(string location)
    {
        if (location.IndexOf('%') > -1)
            location = Environment.ExpandEnvironmentVariables(location);

        if (location.StartsWith("::", StringComparison.Ordinal))
            location = $"shell:{location}";

        else if (location.StartsWith("{", StringComparison.Ordinal))
            location = $"shell:::{location}";

        location = location.Trim(' ', '/', '\\', '\n', '\'', '"');

        return location.Replace('/', '\\');
    }
    public static string GetDefaultExplorerLocation(ShellPathComparer? shellPathComparer = null)
    {
        var id = GetDefaultExplorerLaunchId();
        var location = id switch
        {
            2 => "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}",// Home, Quick Access
            3 => "shell:::{088E3905-0323-4B02-9826-5D99428E115F}",// Downloads
            4 => "shell:::{018D5C66-4533-4307-9B53-224DE2ED1FE6}",// OneDrive
            _ => "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" // This PC
        };

        if (shellPathComparer == null)
            return location;

        var pidl = shellPathComparer.GetPidlFromPath(location);
        var path = ShellPathComparer.GetPathFromPidl(pidl); //SIGDN_URL: Downloads -> file:///C:/Users/Username/Downloads
        Marshal.FreeCoTaskMem(pidl);

        return NormalizeLocation(path ?? location);
    }

    public static string GetExecutablePath()
    {
        var processName = Process.GetCurrentProcess().MainModule?.FileName;
        return processName is { Length: > 0 } ? processName : $"{AppDomain.CurrentDomain.FriendlyName}.exe";
    }

    /// <summary>
    /// What the "open File Explorer to" setting says: 1 = This PC, 2 = Home.
    /// Read straight from the registry, because it is the only place it lives.
    /// </summary>
    private static int GetDefaultExplorerLaunchId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("LaunchTo") as int? ?? 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Puts every window this app took off screen back on screen. Used when the
    /// app exits, so a window it was holding can never outlive it.
    /// </summary>
    public static void RestoreAllHiddenWindows()
    {
        foreach (var hWnd in HiddenWindows.Keys.ToList())
            ShowWindow(hWnd, removeCache: true);
    }

    public static async Task<List<SupporterInfo>> GetSupporters()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            var svgContent = await client.GetStringAsync("https://cdn.jsdelivr.net/gh/w4po/sponsors/sponsors.svg");

            var supporters = new List<SupporterInfo>();
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(svgContent);

            // Find all <a> elements (supporters)
            var linkNodes = xmlDoc.GetElementsByTagName("a");

            foreach (System.Xml.XmlNode linkNode in linkNodes)
            {
                if (linkNode is not System.Xml.XmlElement linkElement)
                    continue;

                var href = linkElement.GetAttribute("href");
                var id = linkElement.GetAttribute("id");

                // Find the image element inside the link
                var imageElements = linkElement.GetElementsByTagName("image");
                if (imageElements.Count <= 0 || imageElements[0] is not System.Xml.XmlElement imageElement)
                    continue;

                var imageUrl = imageElement.GetAttribute("href");

                supporters.Add(new SupporterInfo
                {
                    Name = string.IsNullOrWhiteSpace(id) ? "Unknown" : id,
                    ProfileUrl = string.IsNullOrEmpty(href) ? string.Empty : href,
                    ImageUrl = string.IsNullOrEmpty(imageUrl) ? string.Empty : imageUrl
                });
            }

            return supporters;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing SVG: {ex.Message}");
            return [];
        }
    }
}
