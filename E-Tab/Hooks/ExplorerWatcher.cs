using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ETab.Helpers;
using ETab.Interop;
using ETab.Managers;
using ETab.Models;
using ETab.WinAPI;

namespace ETab.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

public class ExplorerWatcher : IHook
{
    private static bool _instanceRunning;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, byte> _processedHWnds = new();
    private readonly ConcurrentDictionary<nint, byte> _trackedExplorerHandles = new();
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new(), _processLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);
    private readonly ProcessWatcher _processWatcher;
    private readonly SynchronizationContext _uiContext;
    private int _mainExplorerProcessId;
    private int _explorerCheckQueued;
    private bool _shellObjectsInitialized;
    private Timer? _explorerCheckTimer;

    private nint _eventObjectVisibilityHookId;
    private WinEventDelegate? _eventObjectVisibilityHookCallback;
    private nint _eventSystemForegroundHookId;
    private WinEventDelegate? _eventSystemForegroundHookCallback;
    private nint _eventSystemCaptureHookId;
    private WinEventDelegate? _eventSystemCaptureHookCallback;
    private readonly ManualResetEventSlim _winEventThreadReady = new(false);
    private Thread? _winEventThread;
    private uint _winEventThreadId;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;
    private int _desktopFolderSwitchRunning;
    private string? _desktopFolderFromInput;
    private long _desktopFolderFromInputAt;

    private string _defaultLocation = null!;
    private bool _reuseTabs = true;
    private bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;

    /// <summary>
    /// When a folder was last taken on. Read by the app to tell an idle minute
    /// from a busy one, which is the only moment the memory it is holding can be
    /// handed back.
    /// </summary>
    public static long LastActivityTicks { get; private set; } = Stopwatch.GetTimestamp();

    public event Action? OnShellInitialized;

    public ExplorerWatcher()
    {
        if (_instanceRunning)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _instanceRunning = true;

        // ShellWindows and every InternetExplorer object it returns belong to
        // one COM apartment. Keep that apartment the WinForms UI thread: event
        // handlers, the explorer-restart path and the tray menu must all touch
        // these objects from here. Mixing in a timer or a worker thread makes
        // IConnectionPoint fail with RPC_E_WRONG_THREAD.
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ExplorerWatcher must be created on the UI thread.");

        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        StartExplorerProcessCheck();
    }

    public void StartHook()
    {
        if (_isForcingTabs) return;
        _isForcingTabs = true;
        SettingsManager.AutoMerge = true;
        Log.Info("Auto-merge hook is active.");
    }

    public void StopHook()
    {
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
        SettingsManager.AutoMerge = false;
    }

    /// <summary>
    /// Turns every File Explorer window that is not the one folders gather in
    /// into a tab of that window - the same move the hook makes for a window
    /// that is opened, applied to the windows that are already there. Useful
    /// with the hook switched off, and from the tray menu.
    /// </summary>
    public void MergeAllWindowsNow()
    {
        if (SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => MergeAllWindowsNow(), null);
            return;
        }

        _ = MergeAllWindowsOnUiThreadAsync();
    }

    private async Task MergeAllWindowsOnUiThreadAsync()
    {
        try
        {
            var main = _mainWindowHandle != 0 ? _mainWindowHandle : Helper.GetAnotherExplorerWindow(0);
            if (main == 0) return;

            List<WindowEntry> entries;
            lock (_windowEntryDictLock)
                entries = new List<WindowEntry>(_windowEntryDict);

            foreach (var (window, info) in entries)
            {
                try
                {
                    var hwnd = new IntPtr(window.HWND);
                    if (hwnd == main) continue;
                    if (!WinApi.IsWindow(hwnd) || !WinApi.IsWindowVisible(hwnd)) continue;

                    var location = info.Location ?? GetLocation(window);
                    if (string.IsNullOrWhiteSpace(location)) continue;

                    await SwitchTo(location, 0, GetSelectedItems(window), asTab: true);
                    window.Quit();
                }
                catch
                {
                    // One window that will not move must not stop the rest.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Merging the open windows failed: " + ex.Message);
        }
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
            _closedWindows.Clear();
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        var result = new List<WindowRecord>();
        
        // Add open windows
        lock (_windowEntryDictLock)
            result.AddRange(
                _windowEntryDict.Keys.Select(ie => new WindowRecord(GetLocation(ie), new IntPtr(ie.HWND), GetSelectedItems(ie), ie.LocationName)));
        
        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
            result.AddRange(_closedWindows.AsEnumerable().Reverse());
        
        return result.GroupBy(w => w.Location).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }
    
    public async Task<nint> SearchForTab(string targetPath)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (!Helper.IsTimeUp(windowInfo.CreatedAt, 2_000))
                    continue;

                var comparePath = windowInfo.Location ?? GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                {
                    // The first tab's HWND can change after Explorer creates
                    // more tabs. Ask the shell object for its current handle
                    // instead of trusting a handle cached minutes ago.
                    var currentHandle = await GetTabHandle(window, refresh: true);
                    if (currentHandle != 0)
                    {
                        if (tabHandle is { } cachedHandle &&
                            cachedHandle != 0 &&
                            cachedHandle != currentHandle)
                        {
                            Log.Info(
                                $"Tab handle for '{comparePath}' moved from " +
                                $"0x{cachedHandle:X} to 0x{currentHandle:X}.");
                        }
                        return currentHandle;
                    }

                    if (tabHandle is { } fallbackHandle && fallbackHandle != 0)
                        return fallbackHandle;
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
    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
    }
    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();
        SelectTabByIndex(windowHandle, count - 1);
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Send 0xA221 magic command (CTRL + 1...n)
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }
    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, bool asTab, nint windowHandle, int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay);

        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);
        
        if (normalizedPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                //
            }
        }
        
        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) && !_reuseTabs)
        {
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        if (_windowEntryDict.Count > 0)
        {
            OpenNewTab(windowHandle, normalizedPath);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle);
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            closedWindow = _closedWindows.LastOrDefault(w => w.Location != _defaultLocation);
            if (closedWindow == null) return;
            _closedWindows.Remove(closedWindow);
        }

        if (!asTab)
        {
            closedWindow.CreatedAt = Environment.TickCount;
            await OpenNewWindowWithSelection(closedWindow);
            return;
        }

        await OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }
    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (Helper.GetAllExplorerTabs(windowHandle).Take(2).Count() < 2)
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = _windowEntryDict[window].Value.Location ?? GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        // Send 0xA021 magic command (CTRL + W)
        WinApi.SendMessage(activeTabHandle, WinApi.WM_COMMAND, 0xA021, 1);

        await OpenNewWindowWithSelection(windowRecord);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            if (isForward) window?.GoForward();
            else window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (_processedHWnds.TryAdd(hWnd, 0))
        {
            // Schedule removal after a short delay
            _ = Task.Delay(7_000).ContinueWith(t => _processedHWnds.TryRemove(hWnd, out _), TaskScheduler.Default);
        }
    }

    private void StartWinEventMonitor()
    {
        if (_winEventThread != null) return;

        _winEventThreadReady.Reset();
        _winEventThread = new Thread(WinEventMonitorLoop)
        {
            IsBackground = true,
            Name = "E-Tab Explorer WinEvent monitor"
        };
        _winEventThread.SetApartmentState(ApartmentState.STA);
        _winEventThread.Start();

        if (!_winEventThreadReady.Wait(2_000))
            Log.Warn("The Explorer WinEvent monitor did not start within 2 seconds.");
    }

    private void WinEventMonitorLoop()
    {
        var comInitialized = false;
        try
        {
            comInitialized = WinApi.CoInitializeEx(0, WinApi.COINIT_APARTMENTTHREADED) >= 0;

            // Create this thread's message queue before publishing its ID.
            WinApi.PeekMessage(out _, 0, 0, 0, WinApi.PM_NOREMOVE);
            _winEventThreadId = WinApi.GetCurrentThreadId();

            var eventFlags = WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS;

            _eventObjectVisibilityHookCallback = OnWindowVisibilityChanged;
            _eventObjectVisibilityHookId = WinApi.SetWinEventHook(
                WinApi.EVENT_OBJECT_SHOW,
                WinApi.EVENT_OBJECT_SHOW,
                0,
                _eventObjectVisibilityHookCallback,
                0,
                0,
                eventFlags);

            _eventSystemForegroundHookCallback = OnSystemForeground;
            _eventSystemForegroundHookId = WinApi.SetWinEventHook(
                WinApi.EVENT_SYSTEM_FOREGROUND,
                WinApi.EVENT_SYSTEM_FOREGROUND,
                0,
                _eventSystemForegroundHookCallback,
                0,
                0,
                eventFlags);

            _eventSystemCaptureHookCallback = OnSystemCaptureStart;
            _eventSystemCaptureHookId = WinApi.SetWinEventHook(
                WinApi.EVENT_SYSTEM_CAPTURESTART,
                WinApi.EVENT_SYSTEM_CAPTURESTART,
                0,
                _eventSystemCaptureHookCallback,
                0,
                0,
                eventFlags);

            if (_eventObjectVisibilityHookId == 0)
                Log.Warn("The Explorer window visibility hook could not be installed.");
            if (_eventSystemForegroundHookId == 0)
                Log.Warn("The Explorer foreground hook could not be installed.");
            if (_eventSystemCaptureHookId == 0)
                Log.Warn("The desktop input hook could not be installed.");

            Log.Info($"Explorer WinEvent monitor is active on thread {_winEventThreadId}.");
            _winEventThreadReady.Set();

            while (WinApi.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WinApi.WM_QUIT) break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The Explorer WinEvent monitor stopped unexpectedly: " + ex.Message);
        }
        finally
        {
            _winEventThreadReady.Set();

            if (_eventObjectVisibilityHookId != 0)
                WinApi.UnhookWinEvent(_eventObjectVisibilityHookId);
            if (_eventSystemForegroundHookId != 0)
                WinApi.UnhookWinEvent(_eventSystemForegroundHookId);
            if (_eventSystemCaptureHookId != 0)
                WinApi.UnhookWinEvent(_eventSystemCaptureHookId);

            _eventObjectVisibilityHookId = 0;
            _eventObjectVisibilityHookCallback = null;
            _eventSystemForegroundHookId = 0;
            _eventSystemForegroundHookCallback = null;
            _eventSystemCaptureHookId = 0;
            _eventSystemCaptureHookCallback = null;
            _winEventThreadId = 0;

            if (comInitialized)
                WinApi.CoUninitialize();
        }
    }

    private void StopWinEventMonitor()
    {
        var thread = _winEventThread;
        if (thread == null) return;

        var threadId = Volatile.Read(ref _winEventThreadId);
        if (threadId != 0)
            WinApi.PostThreadMessage(threadId, WinApi.WM_QUIT, 0, 0);

        if (!thread.Join(2_000))
            Log.Warn("The Explorer WinEvent monitor did not stop within 2 seconds.");

        _winEventThread = null;
    }

    private void OnWindowVisibilityChanged(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        try
        {
            if (!_isForcingTabs || idObject != 0 || idChild != 0) return;
            if (eventType != WinApi.EVENT_OBJECT_SHOW) return;

            HideIncomingWindow(hWnd, "its first show");
        }
        catch (Exception ex)
        {
            Log.Warn("Handling an Explorer visibility event failed: " + ex.Message);
        }
    }

    private void HideIncomingWindow(nint hWnd, string moment)
    {
        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_processedHWnds.ContainsKey(hWnd)) return;

        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_trackedExplorerHandles.Count < 2 || Helper.IsCtrlShiftDown()) return;
        if (IsTrackedExplorerWindow(hWnd)) return;
        if (Helper.HiddenWindows.ContainsKey(hWnd)) return;

        // Alpha transparency alone can still expose Explorer's first painted
        // frame through DWM. Hide the native window as well, then restore the
        // theme-friendly layered state when it is shown again.
        Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue, hideCompletely: true);
        Log.Info($"Window 0x{hWnd:X} was hidden at {moment} so it cannot paint before becoming a tab.");

        // If the shell never registers this window, keep an invisible window
        // from becoming stranded. A late appearance is recoverable; a window
        // the user cannot see or reach is not.
        _ = Task.Delay(4_000).ContinueWith(_ => _uiContext.Post(_ =>
        {
            if (!Helper.HiddenWindows.ContainsKey(hWnd) || !WinApi.IsWindow(hWnd)) return;

            Helper.ShowWindow(hWnd, removeCache: true);
            if (!SettingsManager.HaveThemeIssue)
                Helper.UpdateWindowLayered(hWnd, remove: true);
            Log.Warn($"Window 0x{hWnd:X} was shown again because the shell never registered it.");
        }, null), TaskScheduler.Default);
    }

    private void OnSystemForeground(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (!_isForcingTabs || !Helper.IsFileExplorerWindow(hWnd)) return;

            if (!IsTrackedExplorerWindow(hWnd))
            {
                // Explorer raises the window and only 30-50 ms later raises
                // SHOW. Hide it at the earlier foreground signal so its first
                // frame is never painted while the folder is moved into a tab.
                HideIncomingWindow(hWnd, "foreground");
                return;
            }

            if (Interlocked.CompareExchange(ref _desktopFolderSwitchRunning, 1, 0) != 0) return;

            try
            {
                // Shell COM objects remain owned by the UI apartment. Only the
                // time-sensitive WinEvent pump runs on the dedicated thread.
                _uiContext.Post(_ => _ = SwitchToDesktopFolderAsync(hWnd), null);
            }
            catch
            {
                Volatile.Write(ref _desktopFolderSwitchRunning, 0);
                throw;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _desktopFolderSwitchRunning, 0);
            Log.Warn("Handling an Explorer foreground event failed: " + ex.Message);
        }
    }

    private void OnSystemCaptureStart(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (!_isForcingTabs ||
                !WinApi.IsWindowHasClassName(hWnd, "SysListView32"))
                return;

            var root = WinApi.GetAncestor(hWnd, 2); // GA_ROOT
            var rootClass = WinApi.GetWindowClassName(root);
            if (!string.Equals(rootClass, "Progman", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rootClass, "WorkerW", StringComparison.OrdinalIgnoreCase))
                return;

            // Capture the icon while the desktop is still the topmost window.
            // Once Explorer is foreground it may cover the icon, so its position
            // can no longer be resolved with a point hit test.
            _desktopFolderFromInput = Helper.GetDesktopFolderUnderCursor();
            _desktopFolderFromInputAt = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            Log.Warn("Capturing a desktop input event failed: " + ex.Message);
        }
    }

    private bool IsTrackedExplorerWindow(nint hWnd)
    {
        return hWnd == _mainWindowHandle || _trackedExplorerHandles.ContainsKey(hWnd);
    }

    private async Task SwitchToDesktopFolderAsync(nint foregroundWindow)
    {
        try
        {
            var folder = _desktopFolderFromInput;
            if (folder == null ||
                Helper.IsTimeUp(_desktopFolderFromInputAt, 2_000))
                return;

            var existingTab = await SearchForTab(folder);
            if (existingTab == 0) return;

            var targetWindow = WinApi.GetParent(existingTab);
            if (targetWindow == 0) return;

            var activeTab = GetActiveTabHandle(foregroundWindow);
            if (activeTab == existingTab &&
                foregroundWindow == targetWindow)
                return;

            await SelectTabByHandle(targetWindow, existingTab);
            WinApi.RestoreWindowToForeground(targetWindow);
            _desktopFolderFromInput = null;
            LastActivityTicks = Stopwatch.GetTimestamp();
            Log.Info(
                $"Desktop folder '{folder}' was already open; switched " +
                $"0x{foregroundWindow:X} from tab 0x{activeTab:X} to 0x{existingTab:X}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Switching to a folder opened from the desktop failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _desktopFolderSwitchRunning, 0);
        }
    }
    private InternetExplorer? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;
                if (window.GetProperty("seenBefore") is not null) continue;
                window.PutProperty("seenBefore", true);

                windowInfo = new WindowInfo();
                _windowEntryDict.Add(window, windowInfo);

                if (_windowEntryDict.Count == 1)
                {
                    _mainWindowHandle = new IntPtr(window.HWND);

                    if (SettingsManager.RestorePreviousWindows && _closedWindows.Any(w => w.Restore))
                        _ = RestorePreviousWindows();
                }
                
                return window;
            }
        }

        windowInfo = null;
        return null;
    }
    private async void OnShellWindowRegistered(int __)
    {
        LastActivityTicks = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var showAgain = true;
        nint hWnd = 0;
        try
        {
            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            WindowInfo windowInfo = null!;
            var window = await Helper.DoUntilNotDefaultAsync(() => GetRecentlyCreatedWindow(out windowInfo!), 2_500, 70);
            if (window == null) return;

            _ = GetTabHandle(window);

            hWnd = new IntPtr(window.HWND);
            _trackedExplorerHandles.TryAdd(hWnd, 0);
            
            if (shouldOpenAsWindow)
            {
                PreventWindowHiding(hWnd);
                HookWindowEvents(window, windowInfo);
                return;
            }
            
            var location = GetLocation(window);

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                PreventWindowHiding(hWnd);
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
                Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
            else
                PreventWindowHiding(hWnd);

            Log.Info(
                $"Window 0x{hWnd:X} ('{location}') appeared; " +
                (shouldReopenAsTab ? "moving its folder into a tab." : "leaving it as a window."));

            // Check if it is a detached tab
            var isRecentlyClosed = TryGetRecentlyClosedWindow(location, out var closedWindow);
            if (isRecentlyClosed)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isRecentlyClosed;

            if (shouldReopenAsTab)
            {
                showAgain = false;

                var converted = OpenTabNavigateWithSelection(
                    new WindowRecord(location, hWnd, GetSelectedItems(window)), _mainWindowHandle);

                window.Quit();
                RemoveWindowAndUnhookEvents(window, windowInfo);
                await converted;
                Log.Info($"0x{hWnd:X} ('{location}') is a tab now after {sw.ElapsedMilliseconds} ms.");
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isRecentlyClosed)
            {
                isRecentlyClosed = await Helper.DoUntilNotDefaultAsync(() => TryGetRecentlyClosedWindow(location, out closedWindow), 700, 50);
                if (isRecentlyClosed)
                    SelectItems(window, closedWindow!.SelectedItems);
            }

            HookWindowEvents(window, windowInfo);
        }
        catch (Exception ex)
        {
            Log.Warn($"Converting window 0x{hWnd:X} failed: {ex.Message}");
        }
        finally
        {
            if (showAgain)
            {
                await Helper.DoUntilNotDefaultAsync(() => Helper.ShowWindow(hWnd, removeCache: false), 1_500, 200);

                if (!SettingsManager.HaveThemeIssue)
                    Helper.UpdateWindowLayered(hWnd, remove: true);

                // OnWindowShown might fire after ShellWindowRegistered and hide it again, keep the cache, wait a bit, then remove it.
                _ = Task.Delay(3000).ContinueWith(t => Helper.HiddenWindows.TryRemove(hWnd, out _), TaskScheduler.Default);
            }
        }
    }
    private void HookWindowEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        try
        {
            _trackedExplorerHandles.TryAdd(new IntPtr(window.HWND), 0);
        }
        catch
        {
            // The window may have gone away before its events were hooked.
        }

        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            var location = windowInfo.Location ?? GetLocation(window);
            var locationName = windowInfo.Name ?? window.LocationName;
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND), name: locationName);
            lock (_closedWindowsLock) _closedWindows.Add(windowRecord);

            // Home, This PC, etc
            if (location == _defaultLocation)
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            windowRecord.SelectedItems = GetSelectedItems(window);
            RemoveWindowAndUnhookEvents(window, windowInfo);
        };

        windowInfo.OnNavigateHandler = (object _, ref object _) =>
        {
            windowInfo.Location = GetLocation(window);
            windowInfo.Name = window.LocationName;
        };

        try
        {
            // Subscribe
            window.OnQuit += windowInfo.OnQuitHandler;
            windowInfo.Location = GetLocation(window);
            windowInfo.Name = window.LocationName;
            window.NavigateComplete2 += windowInfo.OnNavigateHandler;

            // Make sure the window is still alive (User might have closed it immediately after opening it)
            _ = window.HWND;
        }
        catch
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
    }
    private void RemoveWindowAndUnhookEvents(
        InternetExplorer window,
        WindowInfo windowInfo,
        bool useLock = true,
        bool skipEventUnhook = false)
    {
        if (!skipEventUnhook)
        {
            try
            {
                // Unsubscribe while the shell server is still alive.
                if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
                if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;
            }
            catch (Exception ex)
            {
                Log.Warn("Unhooking an Explorer window failed: " + ex.Message);
            }
        }

        // Remove from dictionary
        if (useLock)
        {
            lock (_windowEntryDictLock)
                _windowEntryDict.Remove(window);
        }
        else
            _windowEntryDict.Remove(window);

        try
        {
            _trackedExplorerHandles.TryRemove(new IntPtr(window.HWND), out _);
        }
        catch
        {
            // The shell object may already be disconnected.
        }

        // Finally, release the COM reference for this InternetExplorer instance
        try
        {
            Marshal.ReleaseComObject(window);
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing an Explorer window failed: " + ex.Message);
        }
    }

    private async Task RestorePreviousWindows()
    {
        foreach (var record in _closedWindows.Where(record => record.Restore))
        {
            record.Restore = false;
            _ = OpenTabNavigateWithSelection(record);
        }
    }
    private async Task OpenNewWindowWithSelection(WindowRecord windowToOpen, bool duplicate = true, bool lockToOpenWindows = true)
    {
        if (lockToOpenWindows)
            await _toOpenWindowsLock.WaitAsync();

        try
        {
            lock (_closedWindowsLock)
                _closedWindows.Add(windowToOpen);

            var hasSelection = windowToOpen.SelectedItems?.Length > 0;

            nint[]? currentWindows = null;
            if (hasSelection)
                currentWindows = Helper.GetAllExplorerWindows().ToArray();

            Helper.BypassWinForegroundRestrictions();

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            await RunOnUiThread(() =>
            {
                Shell? shell = null;
                try
                {
                    shell = new Shell();
                    shell.ShellExecute(location, "", "", duplicate ? "opennewwindow" : "open");
                }
                finally
                {
                    if (shell != null)
                        Marshal.ReleaseComObject(shell);
                }
            });

            if (!hasSelection) return;

            var newWindowHandle = await Helper.ListenForNewExplorerWindowAsync(currentWindows ?? []);
            if (newWindowHandle == 0) return;

            var window = _windowEntryDict.Keys.FirstOrDefault(w => w.HWND == newWindowHandle);
            if (window == null) return;

            SelectItems(window, windowToOpen.SelectedItems);
        }
        finally
        {
            if (lockToOpenWindows)
                _toOpenWindowsLock.Release();
        }
    }
    private async Task OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0, bool isDuplicate = false, bool forceTabReuse = false)
    {
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = await SearchForTab(windowToOpen.Location);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    await SelectTabByHandle(windowHandle, existingTab);
                    WinApi.RestoreWindowToForeground(windowHandle);
                    return;
                }
            }

            // Get the main window
            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(windowToOpen.Handle);

            if (mainWindowHWnd == 0)
            {
                await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false);
                return;
            }

            // Store the current tabs
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request to open a new tab
            await RequestToOpenNewTab(mainWindowHWnd, lockToOpenWindows: false);

            // Wait for the new tab
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs, 2_000);
            if (newTabHandle == 0) return;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50);
            if (window == null) return;

            var tcs = new TaskCompletionSource<bool>();
            DWebBrowserEvents2_NavigateComplete2EventHandler navigateHandler = null!;
            navigateHandler = (object _, ref object _) =>
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(true);
                SelectItems(window, windowToOpen.SelectedItems);
            };

            window.NavigateComplete2 += navigateHandler;
            try
            {
                await Navigate(window, windowToOpen.Location);
            }
            catch
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(false);
            }

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            var timeoutTask = Task.Delay(5000);
            await Task.WhenAny(tcs.Task, timeoutTask);
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }
    private bool TryGetRecentlyClosedWindow(string location, out WindowRecord? closedWindow, int maxAge = 2_000)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(location);
            lock (_closedWindowsLock)
            {
                for (var i = _closedWindows.Count - 1; i >= 0; i--)
                {
                    var record = _closedWindows[i];
                    if (Environment.TickCount - record.CreatedAt > maxAge) break;
                    if (!_shellPathComparer.IsEquivalent(location, record.Location, targetPidl)) continue;
                    _closedWindows.RemoveAt(i);
                    closedWindow = record;
                    return true;
                }
            }
            closedWindow = null;
            return false;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }
    private Task<nint> GetTabHandle(InternetExplorer window, bool refresh = false)
    {
        if (!refresh &&
            _windowEntryDict.TryGetValue(window, out WindowEntry entry) &&
            entry.OptionalKey is { } handle and > 0)
            return Task.FromResult(handle);

        // QueryService and the returned shell browser must stay in the same
        // apartment as the InternetExplorer object and its event connection.
        return RunOnUiThread(() =>
        {
            try
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (window is not Interop.IServiceProvider sp) return 0;

                sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out var shellBrowser);
                if (shellBrowser == null) return 0;

                try
                {
                    shellBrowser.GetWindow(out var hWnd);

                    if (hWnd != 0)
                        _windowEntryDict.UpdateOptionalKey(window, hWnd, resolveConflict: true);

                    return hWnd;
                }
                finally
                {
                    Marshal.ReleaseComObject(shellBrowser);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Getting the Explorer tab handle failed: " + ex.Message);
                return 0;
            }
        });
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(InternetExplorer window)
    {
        var selectedItems = (window.Document as ShellFolderView)!.SelectedItems();
        var count = selectedItems.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = selectedItems.Item(i).Name;
        }

        return result;
    }
    private static void SelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        if (window.Document is not ShellFolderView document) return;

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            object item = document.Folder.ParseName(name);
            if (item == null) continue;
            document.SelectItem(ref item, 1);
        }
    }
    private static string GetLocation(InternetExplorer window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return Helper.NormalizeLocation(path);

        // Recycle Bin, This PC, etc
        path = ((window.Document as ShellFolderView)!.Folder as Folder2)!.Self.Path;
        return Helper.NormalizeLocation(path);
    }
    private async Task Navigate(InternetExplorer window, string path)
    {
        if (!path.Contains("#") && !path.Contains("%23"))
        {
            window.Navigate2(path);
            return;
        }

        var folder = await RunOnUiThread(() =>
        {
            Shell? shell = null;
            Folder? folder;
            try
            {
                shell = new Shell();
                folder = shell.NameSpace(path);
            }
            finally
            {
                if (shell != null)
                    Marshal.ReleaseComObject(shell);
            }
            return folder;
        });

        try
        {
            window.Navigate2(folder);
        }
        finally
        {
            if (folder != null)
                Marshal.ReleaseComObject(folder);
        }
    }
    private Task RunOnUiThread(Action action)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private Task<T?> RunOnUiThread<T>(Func<T?> action)
    {
        if (SynchronizationContext.Current == _uiContext)
            return Task.FromResult(action());

        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private void StartExplorerProcessCheck()
    {
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = new Timer(_ => QueueExplorerProcessCheck(), null, 0, 1000);
    }

    private void QueueExplorerProcessCheck()
    {
        if (Interlocked.Exchange(ref _explorerCheckQueued, 1) != 0) return;

        _uiContext.Post(_ =>
        {
            Volatile.Write(ref _explorerCheckQueued, 0);
            CheckForMainExplorer();
        }, null);
    }

    private void CheckForMainExplorer()
    {
        var process = Helper.GetMainExplorerProcess();
        if (process == null) return;
        
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        
        lock (_processLock)
        {
            if (_mainExplorerProcessId != 0) return;

            try
            {
                _mainExplorerProcessId = process.Id;
                InitializeShellObjects();
                Log.Info($"Shell COM objects initialized on the UI thread ({Environment.CurrentManagedThreadId}).");
                OnShellInitialized?.Invoke();
            }
            catch (Exception ex)
            {
                _mainExplorerProcessId = 0;
                Log.Error("Shell COM initialization failed.", ex);
                try
                {
                    DisposeShellObjects();
                }
                catch (Exception cleanupException)
                {
                    Log.Error("Shell COM cleanup after initialization failure also failed.", cleanupException);
                }
                StartExplorerProcessCheck();
            }
        }
    }
    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        // Main explorer.exe process (_shellWindows must be restarted)
        lock (_processLock)
        {
            if (e.ProcessId == _mainExplorerProcessId)
            {
                _mainExplorerProcessId = 0;
                // The shell server is already gone. Asking its dead event
                // connection to unsubscribe can raise RPC_E_WRONG_THREAD even
                // though this code runs on the apartment that created it.
                // The connection died with Explorer, so only local references
                // need to be released.
                DisposeShellObjects(skipEventUnhook: true);
                StartExplorerProcessCheck();
                return;
            }
        }
        
        // Other explorer.exe processes
        lock (_windowEntryDictLock)
        {
            if (_windowEntryDict.Count == 0) return;
            var crashCount = 0;
            for (var i = _windowEntryDict.Count - 1; i >= 0; i--)
            {
                var (window, info) = _windowEntryDict.ElementAt<WindowEntry>(i);
                try
                {
                    _ = window.HWND;
                }
                catch
                {
                    if (SettingsManager.RestorePreviousWindows && info.OnNavigateHandler != null)
                    {
                        crashCount++;
                        lock (_closedWindowsLock)
                            _closedWindows.Add(new WindowRecord(info.Location!, name: info.Name!));
                    }
                    
                    RemoveWindowAndUnhookEvents(window, info, useLock: false, skipEventUnhook: true);
                }
            }
            if (!SettingsManager.RestorePreviousWindows || _windowEntryDict.Count > 0) return;
            lock (_closedWindowsLock)
            {
                for (var i = 1; i <= crashCount; i++)
                    _closedWindows[_closedWindows.Count - i].Restore = true;
            }
        }
    }

    private void InitializeShellObjects()
    {
        _shellPathComparer = new ShellPathComparer();
        _shellWindows = new ShellWindows();
        _shellObjectsInitialized = true;

        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);
        
        if (SettingsManager.ClosedWindows != null)
            lock (_closedWindowsLock) _closedWindows.AddRange(SettingsManager.ClosedWindows);

        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // WinEvent callbacks must never queue behind tab conversion on the
        // WinForms thread. A dedicated STA message pump keeps the first-show
        // signal prompt even while Shell COM is busy merging another window.
        StartWinEventMonitor();

        // Hook the event handlers for already-open windows
        var hasOpen = false;
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window)
                continue;
            hasOpen = true;

            var windowInfo = new WindowInfo();
            _windowEntryDict.Add(window, windowInfo);
            window.PutProperty("seenBefore", true);

            _ = GetTabHandle(window);
            HookWindowEvents(window, windowInfo);
        }

        if (!hasOpen) return;
        lock (_closedWindowsLock)
            foreach (var window in _closedWindows) window.Restore = false;
    }
    private void DisposeShellObjects(bool skipEventUnhook = false)
    {
        if (!_shellObjectsInitialized) return;
        _shellObjectsInitialized = false;

        PersistWindows();

        // Unhook global event
        if (_windowRegisteredHandler != null && !skipEventUnhook)
        {
            try
            {
                _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            }
            catch (Exception ex)
            {
                Log.Warn("Unhooking ShellWindows.WindowRegistered failed: " + ex.Message);
            }
            _windowRegisteredHandler = null;
        }
        StopWinEventMonitor();

        // Unsubscribe from each InternetExplorer instance's events
        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            try
            {
                if (!skipEventUnhook)
                {
                    // Unsubscribe while the shell server is still alive.
                    if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
                    if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Unhooking an Explorer window failed: " + ex.Message);
            }
            finally
            {
                try
                {
                    Marshal.ReleaseComObject(window);
                }
                catch
                {
                    // The shell may already have released this window.
                }
            }
        }
        _windowEntryDict.Clear();

        // Release the ShellWindows COM object
        try
        {
            Marshal.ReleaseComObject(_shellWindows);
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing ShellWindows failed: " + ex.Message);
        }

        try
        {
            _shellPathComparer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing the shell path comparer failed: " + ex.Message);
        }
    }

    private void PersistWindows()
    {
        var store = new List<WindowRecord>();
        lock (_closedWindowsLock)
        {
            if (SettingsManager.SaveClosedHistory) store.AddRange(_closedWindows);
            _closedWindows.Clear();
        }

        // Save currently open windows (explorer crash / system restart, logoff / AppExit)
        if (SettingsManager.RestorePreviousWindows)
            lock (_windowEntryDictLock)
            {
                store.AddRange(_windowEntryDict.Values
                    .Where(w => w.OnNavigateHandler != null)
                    .Select(w => new WindowRecord(w.Location!, name: w.Name!, restore: true)));
            }
        
        // DistinctBy location
        var distinctItems = store
            .GroupBy(w => w.Location)
            .Select(g => g.Last())
            .ToArray();
        
        // TakeLast 100
        SettingsManager.ClosedWindows = distinctItems.Skip(Math.Max(0, distinctItems.Length - 100)).ToArray();
    }

    public void Dispose()
    {
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        DisposeShellObjects();
        _instanceRunning = false;
        _processWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
