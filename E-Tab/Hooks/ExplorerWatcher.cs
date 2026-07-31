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
    private readonly Dictionary<object, WindowInfo> _shellItems = new();
    private readonly Dictionary<nint, object> _tabToItem = new();
    private readonly HashSet<nint> _knownTopLevelWindows = new();
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
        _pollTimer = new Timer(PollShell, null, 0, 75);
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

            var current = new HashSet<object>(currentItems.Select(x => x.Item));
            foreach (var item in _shellItems.Keys.Where(k => !current.Contains(k)).ToList())
                RemoveItemCore(item);

            foreach (var (item, hwnd) in currentItems)
            {
                if (_shellItems.TryGetValue(item, out var info))
                {
                    info.WindowHandle = hwnd;
                    UpdateTabMapping(item, info);
                    continue;
                }

                var newInfo = new WindowInfo
                {
                    WindowHandle = hwnd,
                    Location = TryGetLocation(item)
                };
                _shellItems[item] = newInfo;
                UpdateTabMapping(item, newInfo);
            }

            if (_knownTopLevelWindows.Count == 0)
                _mainWindowHandle = 0;
            else if (!_knownTopLevelWindows.Contains(_mainWindowHandle))
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
            if (_knownTopLevelWindows.Count == 0)
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
            if (_knownTopLevelWindows.Count < 1) return;
        }

        Helper.HideWindow(hWnd);
        ScheduleShowFallback(hWnd);
        _syncContext.Post(_ => PollShell(null), null);
    }

    private static void ScheduleShowFallback(nint hWnd)
    {
        _ = Task.Delay(3_000).ContinueWith(_ =>
        {
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
                    await SelectTabByHandle(windowHandle, existingTab);
                    WinApi.RestoreWindowToForeground(windowHandle);
                    converted = true;
                    return;
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

                var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
                if (newTabHandle == 0)
                    return;

                var tabItem = await Helper.DoUntilNotDefaultAsync(
                    () => GetItemByTabHandle(newTabHandle),
                    2_000,
                    50);
                if (tabItem == null)
                    return;

                await Navigate(tabItem, target);
                SelectItems(tabItem, TryGetSelectedItems(item));
                WinApi.RestoreWindowToForeground(mainWindowHWnd);

                converted = true;
            }
            finally
            {
                _toOpenWindowsLock.Release();
            }
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
        }
    }

    private nint SearchForTab(string targetPath)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer!.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            lock (_itemsLock)
            {
                foreach (var (item, info) in _shellItems.ToList())
                {
                    if (!Helper.IsTimeUp(info.CreatedAt, 2_000)) continue;
                    if (info.TabHandle == 0) continue;

                    var comparePath = info.Location ?? TryGetLocation(item);
                    if (comparePath == null) continue;
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
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse()
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count())
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
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

    private void UpdateTabMapping(object item, WindowInfo info)
    {
        var tabHandle = GetTabHandle(item);
        if (tabHandle == 0) return;
        if (tabHandle == info.TabHandle) return;

        if (info.TabHandle != 0)
            _tabToItem.Remove(info.TabHandle);

        info.TabHandle = tabHandle;
        _tabToItem[tabHandle] = item;
    }

    private void RemoveItem(object item)
    {
        lock (_itemsLock)
            RemoveItemCore(item);
    }

    private void RemoveItemCore(object item)
    {
        if (!_shellItems.Remove(item, out var info)) return;

        if (info.TabHandle != 0)
            _tabToItem.Remove(info.TabHandle);
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
            _shellItems.Clear();
            _tabToItem.Clear();
            _knownTopLevelWindows.Clear();
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
