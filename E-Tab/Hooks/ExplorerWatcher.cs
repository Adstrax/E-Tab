using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ETab.Helpers;
using ETab.Interop;
using ETab.Models;
using ETab.WinAPI;

namespace ETab.Hooks;

public sealed class ExplorerWatcher : IDisposable
{
    private const string ControlPanelLocation = "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}";
    private static Guid ShellBrowserGuid = typeof(IShellBrowser).GUID;

    private readonly SynchronizationContext _syncContext;
    private readonly object _itemsLock = new();
    private readonly object _processLock = new();
    private readonly Dictionary<nint, WindowInfo> _tabInfos = new();
    private readonly Dictionary<nint, object> _tabToItem = new();
    private readonly HashSet<nint> _knownTopLevelWindows = new();
    private readonly HashSet<nint> _pendingConversions = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1, 1);
    private readonly ProcessWatcher _processWatcher;
    private readonly StaTaskScheduler _staTaskScheduler;

    private object? _shellApp;
    private ShellPathComparer? _shellPathComparer;
    private string _defaultLocation = string.Empty;
    private nint _mainWindowHandle;
    private int _mainExplorerProcessId;
    private Timer? _explorerCheckTimer;
    private Timer? _pollTimer;
    private nint _eventObjectCreateHookId;
    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectCreateHookCallback;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private bool _polling;
    private bool _disposed;

    public ExplorerWatcher()
    {
        _syncContext = SynchronizationContext.Current
                       ?? throw new InvalidOperationException("ExplorerWatcher must be created on a UI thread.");
        _staTaskScheduler = new StaTaskScheduler();
        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        StartExplorerProcessCheck();
    }

    private void CheckForMainExplorer(object? state)
    {
        var process = Helper.GetMainExplorerProcess();
        if (process == null) return;

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;

        lock (_processLock)
        {
            if (_mainExplorerProcessId != 0) return;

            _mainExplorerProcessId = process.Id;
            RunInStaThread(InitializeShellObjects).GetAwaiter().GetResult();
        }
    }

    private void InitializeShellObjects()
    {
        _shellPathComparer = new ShellPathComparer();
        _shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);

        _eventObjectCreateHookCallback = OnWindowShown;
        _eventObjectCreateHookId = WinApi.SetWinEventHook(
            WinApi.EVENT_OBJECT_CREATE,
            WinApi.EVENT_OBJECT_CREATE,
            0,
            _eventObjectCreateHookCallback,
            0,
            0,
            0);

        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(
            WinApi.EVENT_OBJECT_SHOW,
            WinApi.EVENT_OBJECT_SHOW,
            0,
            _eventObjectShowHookCallback,
            0,
            0,
            0);

        PollShellCore();
        // Keep the background poll at 250ms: new windows are already caught
        // immediately by the WinEvent hooks, and a faster timer measurably
        // increases idle CPU usage without a visible latency benefit.
        _pollTimer = new Timer(PollShell, null, 0, 250);
    }

    private void PollShell(object? state)
    {
        if (_disposed || _shellApp == null || _polling) return;

        _polling = true;
        try
        {
            RunInStaThread(PollShellCore).GetAwaiter().GetResult();
        }
        catch
        {
            // Explorer can be in a transient state during restart.
        }
        finally
        {
            _polling = false;
        }
    }

    private void PollShellCore()
    {
        if (_shellApp == null) return;

        var currentTopLevel = WinApi.FindAllWindowsEx("CabinetWClass").ToHashSet();
        var currentItems = new List<(object Item, nint Hwnd)>();

        try
        {
            dynamic windows = ((dynamic)_shellApp).Windows();
            var count = (int)windows.Count;
            for (var i = 0; i < count; i++)
            {
                object item;
                try
                {
                    item = (object)windows.Item(i);
                }
                catch
                {
                    continue;
                }

                var hwnd = GetWindowHandle(item);
                if (hwnd != 0)
                    currentItems.Add((item, hwnd));
            }
        }
        catch
        {
            // ShellWindows can be temporarily unavailable during Explorer restart.
        }

        var recognizedWindows = new HashSet<nint>();
        foreach (var hwnd in currentTopLevel)
        {
            if (_knownTopLevelWindows.Contains(hwnd)) continue;
            if (HandleNewTopLevelWindow(hwnd, currentItems))
                recognizedWindows.Add(hwnd);
        }

        lock (_itemsLock)
        {
            _knownTopLevelWindows.RemoveWhere(h => !currentTopLevel.Contains(h));
            _knownTopLevelWindows.UnionWith(recognizedWindows);

            var currentTabHandles = new HashSet<nint>();
            foreach (var (item, hwnd) in currentItems)
            {
                var tabHandle = GetTabHandle(item);
                if (tabHandle == 0) continue;
                currentTabHandles.Add(tabHandle);
                _tabToItem[tabHandle] = item;

                if (_tabInfos.TryGetValue(tabHandle, out var info))
                {
                    info.WindowHandle = hwnd;
                    info.TabHandle = tabHandle;
                    continue;
                }

                var newInfo = new WindowInfo
                {
                    WindowHandle = hwnd,
                    TabHandle = tabHandle,
                    Location = _tabInfos.Count == 0 && WinApi.IsWindowVisible(hwnd) &&
                               WinApi.IsWindowHasClassName(hwnd, "CabinetWClass")
                        ? TryGetLocation(item)
                        : null
                };
                _tabInfos[tabHandle] = newInfo;
            }

            foreach (var staleTab in _tabInfos.Keys.Where(k => !currentTabHandles.Contains(k)).ToList())
            {
                _tabToItem.Remove(staleTab);
                _tabInfos.Remove(staleTab);
            }

            if (_knownTopLevelWindows.Count == 0)
                _mainWindowHandle = 0;
            else if (!_knownTopLevelWindows.Contains(_mainWindowHandle) || !WinApi.IsWindowVisible(_mainWindowHandle))
                _mainWindowHandle = GetMainWindowHWnd(0);
        }
    }

    private bool HandleNewTopLevelWindow(nint hwnd, List<(object Item, nint Hwnd)> items)
    {
        object? item = null;
        foreach (var (candidate, candidateHwnd) in items)
        {
            if (candidateHwnd == hwnd)
            {
                item = candidate;
                break;
            }
        }

        if (item == null) return false;

        lock (_itemsLock)
        {
            if (!HasVisibleExplorerWindow(hwnd))
            {
                Helper.ShowWindow(hwnd, removeCache: true);
                return true;
            }
        }

        var location = TryGetLocation(item);
        if (location != null && location.StartsWith(ControlPanelLocation, StringComparison.OrdinalIgnoreCase))
        {
            Helper.ShowWindow(hwnd, removeCache: true);
            return true;
        }

        if (GetTabHandle(item) == 0) return false;
        if (WinApi.FindAllWindowsEx("ShellTabWindowClass", hwnd).Take(2).Count() != 1)
        {
            Helper.ShowWindow(hwnd, removeCache: true);
            return true;
        }

        Helper.HideWindow(hwnd);
        lock (_itemsLock)
            _pendingConversions.Add(hwnd);
        ScheduleShowFallback(hwnd);
        _syncContext.Post(_ => _ = ConvertToTabAsync(item, hwnd, location), null);
        return true;
    }

    private void OnWindowShown(
        nint hWinEventHook,
        uint eventType,
        nint hWnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0) return;
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        lock (_itemsLock)
        {
            if (_knownTopLevelWindows.Contains(hWnd)) return;
            if (!HasVisibleExplorerWindow(hWnd)) return;
        }

        Helper.HideWindow(hWnd);
        ScheduleShowFallback(hWnd);
        _syncContext.Post(_ => PollShell(null), null);
        _ = Task.Delay(60).ContinueWith(
            _ => _syncContext.Post(_ => PollShell(null), null),
            TaskScheduler.Default);
    }

    private static bool HasVisibleExplorerWindow(nint except)
    {
        foreach (var hWnd in WinApi.FindAllWindowsEx("CabinetWClass"))
        {
            if (hWnd == except) continue;
            if (WinApi.IsWindowVisible(hWnd)) return true;
        }

        return false;
    }

    private void ScheduleShowFallback(nint hWnd)
    {
        _ = Task.Delay(3_000).ContinueWith(_ =>
        {
            // If the conversion is still in progress, leave the window hidden;
            // ConvertToTabAsync restores it when it completes or fails.
            lock (_itemsLock)
            {
                if (_pendingConversions.Contains(hWnd)) return;
            }

            if (Helper.HiddenWindows.ContainsKey(hWnd))
                Helper.ShowWindow(hWnd, removeCache: true);
        }, TaskScheduler.Default);
    }

    private async Task ConvertToTabAsync(object item, nint sourceHwnd, string? location)
    {
        var converted = false;
        try
        {
            await _toOpenWindowsLock.WaitAsync();
            try
            {
                var target = string.IsNullOrWhiteSpace(location) ? TryGetLocation(item) : location;
                if (string.IsNullOrWhiteSpace(target))
                    return;

                var existingTab = SearchForTab(target);
                if (existingTab != 0)
                {
                    var windowHandle = WinApi.GetParent(existingTab);
                    if (windowHandle != 0 && WinApi.IsWindow(windowHandle) && WinApi.IsWindow(existingTab))
                    {
                        await SelectTabByHandle(windowHandle, existingTab);

                        // Only treat the conversion as done when the tab is
                        // still alive and attached to the same window. If the
                        // window was closed while we were selecting the tab,
                        // fall through to the normal path so the source
                        // window is restored instead of silently disappearing.
                        if (WinApi.IsWindow(existingTab) && WinApi.GetParent(existingTab) == windowHandle)
                        {
                            WinApi.RestoreWindowToForeground(windowHandle);
                            converted = true;
                            return;
                        }
                    }
                }

                var mainWindowHWnd = GetMainWindowHWnd(0);
                if (mainWindowHWnd == 0)
                {
                    await OpenNewWindow(target);
                    converted = true;
                    return;
                }

                var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();
                await RequestToOpenNewTab(mainWindowHWnd);

                var newTabHandle = await Helper.ListenForNewExplorerTabAsync(
                    mainWindowHWnd,
                    currentTabs,
                    2_000,
                    sleepMs: 10);
                if (newTabHandle == 0)
                    return;

                // Give slow Explorer extra time to register the new tab's
                // Shell item before giving up, so a half-created tab is not
                // left at the default location.
                var tabItem = await WaitForTabItemAsync(newTabHandle, 4_000);
                if (tabItem == null)
                    return;

                try
                {
                    await Navigate(tabItem, target);
                    SelectItems(tabItem, TryGetSelectedItems(item));
                }
                catch
                {
                    // Navigation failed (Explorer may be closing or busy).
                    // Close the half-created tab so no stray default tab is
                    // left behind, then restore the source window.
                    if (Helper.GetAllExplorerTabs(mainWindowHWnd).Count() > 1)
                        TryQuitTabItem(tabItem);
                    return;
                }

                // If the target window disappeared mid-conversion, restore
                // the source window instead of pretending the tab exists.
                if (WinApi.IsWindow(newTabHandle) && WinApi.GetParent(newTabHandle) == mainWindowHWnd)
                {
                    WinApi.RestoreWindowToForeground(mainWindowHWnd);
                    converted = true;
                }
            }
            finally
            {
                _toOpenWindowsLock.Release();
            }
        }
        catch
        {
            // Any unexpected failure leaves converted = false, so the
            // finally block below restores the source window.
        }
        finally
        {
            if (converted)
            {
                try
                {
                    ((dynamic)item).Quit();
                }
                catch
                {
                    // The window may already be gone.
                }

                RemoveItem(item);
                Helper.HiddenWindows.TryRemove(sourceHwnd, out _);
            }
            else
            {
                Helper.ShowWindow(sourceHwnd, removeCache: true);
            }

            lock (_itemsLock)
                _pendingConversions.Remove(sourceHwnd);
        }
    }

    private static void TryQuitTabItem(object tabItem)
    {
        try
        {
            ((dynamic)tabItem).Quit();
        }
        catch
        {
            // The tab may already be gone.
        }
    }

    private nint SearchForTab(string targetPath)
    {
        lock (_itemsLock)
        {
            foreach (var (handle, info) in _tabInfos.ToList())
            {
                if (!Helper.IsTimeUp(info.CreatedAt, 2_000)) continue;
                if (info.TabHandle == 0) continue;

                var comparePath = info.Location;
                if (comparePath == null && _tabToItem.TryGetValue(info.TabHandle, out var tabItem))
                {
                    comparePath = TryGetLocation(tabItem);
                    if (comparePath != null)
                        info.Location = comparePath;
                }
                if (comparePath == null) continue;
                if (string.Equals(targetPath, comparePath, StringComparison.OrdinalIgnoreCase))
                    return info.TabHandle;
            }
        }

        if (IsFileSystemPath(targetPath))
            return 0;

        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer!.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            lock (_itemsLock)
            {
                foreach (var (handle, info) in _tabInfos.ToList())
                {
                    if (!Helper.IsTimeUp(info.CreatedAt, 2_000)) continue;
                    if (info.TabHandle == 0) continue;

                    var comparePath = info.Location;
                    if (comparePath == null) continue;
                    if (string.Equals(targetPath, comparePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                        return info.TabHandle;
                }
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }

    private static bool IsFileSystemPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }

    private async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            activeTab = await Helper.DoUntilNotDefaultAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
    }

    private static void SelectTabByIndex(nint windowHandle, int index)
    {
        // 0xA221 is the magic CTRL + 1...n command.
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }

    private async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false)
    {
        if (windowHandle == 0)
        {
            await OpenNewWindow(string.Empty);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // 0xA21B is the magic CTRL + T command.
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }

    private async Task OpenNewWindow(string location)
    {
        Helper.BypassWinForegroundRestrictions();

        var target = string.IsNullOrWhiteSpace(location) ? _defaultLocation : location;
        await RunInStaThread(() =>
        {
            dynamic shell = CreateShell();
            try
            {
                shell.ShellExecute(target, "", "", "open");
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        });
    }

    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle) && WinApi.IsWindowVisible(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Where(h => WinApi.IsWindowVisible(h))
            .Reverse()
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count())
            .FirstOrDefault();

        return _mainWindowHandle;
    }

    private nint GetTabHandle(object item)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (item is not ETab.Interop.IServiceProvider sp) return 0;

        sp.QueryService(ref ShellBrowserGuid, ref ShellBrowserGuid, out var shellBrowser);
        if (shellBrowser == null) return 0;

        try
        {
            shellBrowser.GetWindow(out nint hWnd);
            return hWnd;
        }
        finally
        {
            Marshal.ReleaseComObject(shellBrowser);
        }
    }

    private object? GetItemByTabHandle(nint tabHandle)
    {
        lock (_itemsLock)
            return _tabToItem.TryGetValue(tabHandle, out var item) ? item : null;
    }

    /// <summary>
    /// Waits for the Shell item of a newly created tab to be registered.
    /// Polls the shell synchronously on the STA thread so the enumeration is
    /// guaranteed to have completed before each lookup, instead of queuing an
    /// asynchronous poll that may lag behind the check.
    /// </summary>
    private async Task<object?> WaitForTabItemAsync(nint tabHandle, int timeMs)
    {
        var startTicks = Stopwatch.GetTimestamp();
        while (!Helper.IsTimeUp(startTicks, timeMs))
        {
            try
            {
                await RunInStaThread(PollShellCore);
            }
            catch
            {
                // Explorer can be in a transient state during tab creation.
            }

            var item = GetItemByTabHandle(tabHandle);
            if (item != null) return item;

            await Task.Delay(15);
        }

        return null;
    }

    private void RemoveItem(object item)
    {
        var tabHandle = GetTabHandle(item);
        lock (_itemsLock)
        {
            if (tabHandle == 0) return;
            _tabToItem.Remove(tabHandle);
            _tabInfos.Remove(tabHandle);
        }
    }

    private static nint GetWindowHandle(object item)
    {
        try
        {
            return new IntPtr(Convert.ToInt64(((dynamic)item).HWND));
        }
        catch
        {
            return 0;
        }
    }

    private static string? TryGetLocation(object item)
    {
        try
        {
            dynamic window = item;
            var path = (string?)window.LocationURL;
            if (!string.IsNullOrWhiteSpace(path))
                return Helper.NormalizeLocation(path);

            var document = window.Document;
            if (document == null) return null;

            var folder = document.Folder;
            if (folder == null) return null;

            var self = folder.Self;
            if (self == null) return null;

            return Helper.NormalizeLocation((string)self.Path);
        }
        catch
        {
            return null;
        }
    }

    private static string[]? TryGetSelectedItems(object item)
    {
        try
        {
            dynamic document = ((dynamic)item).Document;
            if (document == null) return null;

            dynamic selectedItems = document.SelectedItems();
            var count = (int)selectedItems.Count;
            if (count == 0) return null;

            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = (string)selectedItems.Item(i).Name;

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void SelectItems(object item, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        try
        {
            dynamic document = ((dynamic)item).Document;
            if (document == null) return;

            for (var i = 0; i < names.Length; i++)
            {
                dynamic selected = document.Folder.ParseName(names[i]);
                if (selected == null) continue;
                document.SelectItem(selected, 1);
            }
        }
        catch
        {
            // Selecting items is best effort.
        }
    }

    private async Task Navigate(object item, string path)
    {
        dynamic window = item;
        if (!path.Contains("#") && !path.Contains("%23"))
        {
            window.Navigate2(path);
            return;
        }

        object? folder = null;
        await RunInStaThread(() =>
        {
            dynamic shell = CreateShell();
            try
            {
                folder = (object?)shell.NameSpace(path);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        });

        try
        {
            window.Navigate2(folder);
        }
        finally
        {
            if (folder != null)
                Marshal.FinalReleaseComObject(folder);
        }
    }

    private static dynamic CreateShell()
    {
        return (dynamic)Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
    }

    private Task RunInStaThread(Action action)
    {
        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, _staTaskScheduler);
    }

    private void StartExplorerProcessCheck()
    {
        _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1_000);
    }

    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        lock (_processLock)
        {
            if (e.ProcessId != _mainExplorerProcessId) return;

            _mainExplorerProcessId = 0;
            DisposeShellObjects();
            StartExplorerProcessCheck();
        }
    }

    private void DisposeShellObjects()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Dispose();
            _pollTimer = null;
        }

        if (_eventObjectCreateHookCallback != null)
        {
            if (_eventObjectCreateHookId != 0)
                WinApi.UnhookWinEvent(_eventObjectCreateHookId);
            _eventObjectCreateHookCallback = null;
        }

        if (_eventObjectShowHookCallback != null)
        {
            if (_eventObjectShowHookId != 0)
                WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        // Never leave windows hidden when the watcher stops.
        foreach (var hWnd in Helper.HiddenWindows.Keys.ToList())
            Helper.ShowWindow(hWnd, removeCache: true);

        lock (_itemsLock)
        {
            _tabInfos.Clear();
            _tabToItem.Clear();
            _knownTopLevelWindows.Clear();
            _pendingConversions.Clear();
        }

        _shellPathComparer?.Dispose();
        _shellPathComparer = null;

        if (_shellApp != null)
        {
            Marshal.FinalReleaseComObject(_shellApp);
            _shellApp = null;
        }

        _mainWindowHandle = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;

        _processWatcher.Dispose();
        DisposeShellObjects();
        _staTaskScheduler.Dispose();

        GC.SuppressFinalize(this);
    }
}
