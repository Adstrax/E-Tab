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
    private const int FastPollMs = 200;
    private const int IdlePollMs = 1000;
    private const int FastPollDurationMs = 3000;
    private const int FullShellPollIntervalMs = 5000;
    private const double IdleTrimAfterSeconds = 90;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

    private readonly SynchronizationContext _syncContext;
    private readonly object _itemsLock = new();
    private readonly object _processLock = new();
    private readonly Dictionary<nint, WindowInfo> _tabInfos = new();
    private readonly Dictionary<nint, object> _tabToItem = new();
    private readonly HashSet<nint> _knownTopLevelWindows = new();
    private readonly HashSet<nint> _pendingConversions = new();
    private readonly Dictionary<nint, long> _firstSeenTicks = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1, 1);
    private readonly StaTaskScheduler _staTaskScheduler;
    private readonly StaTaskScheduler _conversionStaTaskScheduler;

    private object? _shellApp;
    private ShellPathComparer? _shellPathComparer;
    private string _defaultLocation = string.Empty;
    private nint _mainWindowHandle;
    private int _mainExplorerProcessId;
    private Process? _mainExplorerProcess;
    private Timer? _explorerCheckTimer;
    private Timer? _pollTimer;
    private nint _eventObjectCreateHookId;
    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectCreateHookCallback;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private int _polling;
    // Guards the periodic Explorer check against overlapping ticks.
    private int _explorerCheckBusy;
    private long _fastPollUntilTicks;
    private long _lastFullShellPollTicks;
    private long _lastActivityTicks;
    private Timer? _maintenanceTimer;
    private readonly object _tabWaitLock = new();
    private readonly Dictionary<nint, List<TabWaitRequest>> _tabWaiters = new();

    private volatile bool _autoMerge = Settings.AutoMerge;

    public bool AutoMerge
    {
        get => _autoMerge;
        set
        {
            _autoMerge = value;
            try { Settings.AutoMerge = value; }
            catch (Exception ex) { Log.Warn("Failed to persist AutoMerge: " + ex.Message); }
        }
    }

    private bool _disposed;

    public ExplorerWatcher()
    {
        _syncContext = SynchronizationContext.Current
                       ?? throw new InvalidOperationException("ExplorerWatcher must be created on a UI thread.");
        _staTaskScheduler = new StaTaskScheduler("Shell poll STA");
        // Conversions get their own STA thread so they never queue behind the
        // periodic shell polls, which can hold the shared STA thread for tens
        // of milliseconds per full enumeration.
        _conversionStaTaskScheduler = new StaTaskScheduler("Conversion STA");
        StartExplorerProcessCheck();
        _lastActivityTicks = Stopwatch.GetTimestamp();
        StartMemoryMaintenance();
    }

    private void CheckForMainExplorer(object? state)
    {
        if (_disposed) return;

        // Ticks are one second apart and the first shell snapshot can take
        // longer than that, so two ticks used to be able to overlap: the
        // second one saw "process id known, Process object not attached yet"
        // and read that as "Explorer died", which reset the watcher and put
        // every hidden window back on screen. Only one check runs at a time.
        if (Interlocked.CompareExchange(ref _explorerCheckBusy, 1, 0) != 0) return;
        try
        {
            CheckForMainExplorerCore();
        }
        finally
        {
            Interlocked.Exchange(ref _explorerCheckBusy, 0);
        }
    }

    private void CheckForMainExplorerCore()
    {
        // Once the main explorer process is known, the periodic check is just
        // a cheap liveness probe: Process.Exited normally notifies instantly,
        // but this catches the rare case where the event is missed (or its
        // subscription failed).
        lock (_processLock)
        {
            if (_mainExplorerProcessId != 0)
            {
                // The Process object is attached only after the first shell
                // snapshot has been taken. "No object yet" means this check is
                // still initializing, not that Explorer exited.
                if (_mainExplorerProcess is not { } current) return;

                bool exited;
                try
                {
                    exited = current.HasExited;
                }
                catch
                {
                    // The Process object can be in an inconsistent state after
                    // Explorer terminates.
                    exited = true;
                }

                if (!exited) return;
                HandleMainExplorerTerminated();
                return;
            }
        }

        var process = Helper.GetMainExplorerProcess();
        if (process == null) return;

        lock (_processLock)
        {
            if (_mainExplorerProcessId != 0)
            {
                process.Dispose();
                return;
            }

            _mainExplorerProcessId = process.Id;
        }

        try
        {
            RunInStaThread(InitializeShellObjects).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to initialize shell objects.", ex);
            lock (_processLock)
                _mainExplorerProcessId = 0;
            process.Dispose();
            return;
        }

        lock (_processLock)
        {
            _mainExplorerProcess?.Dispose();
            _mainExplorerProcess = process;
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += OnMainExplorerExited;
            }
            catch
            {
                // Exit-event subscription is best effort; the periodic liveness
                // check above still detects Explorer termination.
            }
        }

        // Install the WinEvent hooks on the UI thread. Out-of-context hook
        // callbacks are delivered on the thread that registered the hook, and
        // that thread must pump Windows messages. The WPF UI thread does, while
        // the dedicated STA scheduler thread does not (which previously left
        // these callbacks undelivered).
        _syncContext.Post(_ => InstallWinEventHooks(), null);
    }

    private void InitializeShellObjects()
    {
        _shellPathComparer = new ShellPathComparer();
        _shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);

        PollShellCore(convertNewWindows: false);
        _fastPollUntilTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FastPollDurationMs / 1000;
        _pollTimer = new Timer(PollShell, null, 0, FastPollMs);
        Log.Info($"Explorer watcher initialized (explorer PID {_mainExplorerProcessId}).");
    }

    private void InstallWinEventHooks()
    {
        if (_disposed) return;
        if (_eventObjectCreateHookId != 0 || _eventObjectShowHookId != 0) return;

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
    }

    private void PollShell(object? state)
    {
        if (_disposed || _shellApp == null) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        try
        {
            RunInStaThread(() => PollShellCore()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"Shell poll failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            AdjustPollInterval();
        }
    }

    private void RequestFastPoll()
    {
        _fastPollUntilTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FastPollDurationMs / 1000;
        try
        {
            _pollTimer?.Change(FastPollMs, FastPollMs);
        }
        catch
        {
            // The timer may already be disposed during shutdown.
        }
    }

    private void AdjustPollInterval()
    {
        if (_disposed || _pollTimer == null) return;

        var fast = Stopwatch.GetTimestamp() < _fastPollUntilTicks;
        var interval = fast ? FastPollMs : IdlePollMs;
        try
        {
            _pollTimer.Change(interval, interval);
        }
        catch
        {
            // The timer may already be disposed during shutdown.
        }
    }

    private void PollShellCore(bool convertNewWindows = true)
    {
        if (_shellApp == null) return;

        var currentTopLevel = WinApi.FindAllWindowsEx("CabinetWClass").ToHashSet();

        // The ShellWindows COM snapshot is the most expensive step: it costs
        // several COM round-trips per open tab. At idle it is only needed to
        // keep the tab cache fresh, so run a full pass when a new window
        // actually appeared (which also gives us its Shell item for
        // conversion) or every few seconds to prune closed tabs, instead of
        // on every poll.
        bool fullPassNeeded;
        lock (_itemsLock)
        {
            fullPassNeeded = !convertNewWindows ||
                             Helper.IsTimeUp(_lastFullShellPollTicks, FullShellPollIntervalMs) ||
                             currentTopLevel.Any(h => !_knownTopLevelWindows.Contains(h));
        }

        var currentItems = new List<(object Item, nint Hwnd)>();
        if (fullPassNeeded)
        {
            _lastFullShellPollTicks = Stopwatch.GetTimestamp();
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
        }

        var recognizedWindows = new HashSet<nint>();
        if (convertNewWindows)
        {
            foreach (var hwnd in currentTopLevel)
            {
                if (!fullPassNeeded) break;
                if (_knownTopLevelWindows.Contains(hwnd)) continue;
                if (HandleNewTopLevelWindow(hwnd, currentItems))
                    recognizedWindows.Add(hwnd);
            }
        }
        else
        {
            // Initial snapshot: record every already-open window as known so
            // the first poll does not collapse the user's existing windows
            // into tabs (which caused a flash on first launch and a garbled
            // window on first close).
            recognizedWindows.UnionWith(currentTopLevel);
        }

        lock (_itemsLock)
        {
            _knownTopLevelWindows.RemoveWhere(h => !currentTopLevel.Contains(h));
            _knownTopLevelWindows.UnionWith(recognizedWindows);

            foreach (var staleSeen in _firstSeenTicks.Keys.Where(h => !currentTopLevel.Contains(h)).ToList())
                _firstSeenTicks.Remove(staleSeen);

            if (fullPassNeeded)
            {
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
            }

            if (_knownTopLevelWindows.Count == 0)
                _mainWindowHandle = 0;
            else if (!_knownTopLevelWindows.Contains(_mainWindowHandle) || !WinApi.IsWindowVisible(_mainWindowHandle))
                _mainWindowHandle = GetMainWindowHWnd(0);
        }
    }

    private bool HandleNewTopLevelWindow(nint hwnd, List<(object Item, nint Hwnd)> items)
    {
        if (!AutoMerge)
        {
            lock (_itemsLock)
                _knownTopLevelWindows.Add(hwnd);
            return true;
        }

        object? item = null;
        foreach (var (candidate, candidateHwnd) in items)
        {
            if (candidateHwnd == hwnd)
            {
                item = candidate;
                break;
            }
        }

        if (item == null)
        {
            MarkUnconvertibleIfStale(hwnd);
            return false;
        }

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

        if (GetTabHandle(item) == 0)
        {
            MarkUnconvertibleIfStale(hwnd);
            return false;
        }
        if (WinApi.FindAllWindowsEx("ShellTabWindowClass", hwnd).Take(2).Count() != 1)
        {
            Helper.ShowWindow(hwnd, removeCache: true);
            return true;
        }

        Helper.HideWindowInBackground(hwnd);
        lock (_itemsLock)
            _pendingConversions.Add(hwnd);
        ScheduleShowFallback(hwnd);
        ScheduleHideRecheck(hwnd);
        RequestFastPoll();
        // Run the conversion off the UI thread: its awaits would otherwise
        // resume on the WPF dispatcher and hold the UI thread for the whole
        // conversion (COM lookups, navigation), delaying
        // WinEvent hook delivery for any new window that appears meanwhile.
        _ = Task.Run(() => ConvertToTabAsync(item, hwnd, location));
        return true;
    }

    /// <summary>
    /// A window that ShellWindows cannot map to a convertible item (for
    /// example an elevated Explorer window, which a non-elevated process
    /// cannot enumerate via COM) would otherwise be hidden on every SHOW
    /// event and restored by the fallback timer, flickering forever.
    /// After a short grace period, give up on such windows: show them and
    /// mark them as known so OnWindowShown stops hiding them.
    /// </summary>
    private void MarkUnconvertibleIfStale(nint hwnd)
    {
        lock (_itemsLock)
        {
            if (!_firstSeenTicks.TryGetValue(hwnd, out var firstSeen))
            {
                _firstSeenTicks[hwnd] = Stopwatch.GetTimestamp();
                return;
            }

            if (!Helper.IsTimeUp(firstSeen, 1_000))
                return;

            _firstSeenTicks.Remove(hwnd);
            _knownTopLevelWindows.Add(hwnd);
            Helper.ShowWindow(hwnd, removeCache: true);
            Log.Warn($"Window 0x{hwnd:X} cannot be merged into a tab; leaving it visible.");
        }
    }

    private List<(object Item, nint Hwnd)> EnumerateShellWindows()
    {
        var result = new List<(object Item, nint Hwnd)>();
        if (_shellApp == null) return result;
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
                    result.Add((item, hwnd));
            }
        }
        catch
        {
            // ShellWindows can be temporarily unavailable.
        }

        return result;
    }

    /// <summary>
    /// Collapses every currently-open Explorer window (except the main window)
    /// into tabs of the main window. Used by the manual merge command, which is
    /// useful when auto-merge is switched off so the user can still collapse
    /// several windows in one go.
    /// </summary>
    public void MergeAllWindowsNow()
    {
        if (_disposed || _shellApp == null) return;

        MarkActivity();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                RunInStaThread(() =>
                {
                    var items = EnumerateShellWindows();
                    var host = PickMergeTarget();
                    if (host == 0)
                    {
                        Log.Warn("Manual merge ignored: no visible Explorer window to be the target.");
                        return;
                    }

                    Log.Info($"Manual merge: target 0x{host:X}; {items.Count} window(s) enumerated.");
                    foreach (var (item, hwnd) in items)
                    {
                        if (hwnd == host) continue;
                        if (!WinApi.IsWindow(hwnd) || !WinApi.IsWindowVisible(hwnd))
                        {
                            Log.Info($"Manual merge: skip 0x{hwnd:X} (not a visible window).");
                            continue;
                        }

                        var location = TryGetLocation(item);
                        if (location != null && location.StartsWith(ControlPanelLocation, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Info($"Manual merge: skip 0x{hwnd:X} (control panel).");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(location))
                        {
                            Log.Info($"Manual merge: skip 0x{hwnd:X} (no location).");
                            continue;
                        }
                        if (GetTabHandle(item) == 0)
                        {
                            Log.Info($"Manual merge: skip 0x{hwnd:X} (no tab handle).");
                            continue;
                        }
                        if (!WinApi.FindAllWindowsEx("ShellTabWindowClass", hwnd).Any())
                        {
                            Log.Info($"Manual merge: skip 0x{hwnd:X} (no shell tab).");
                            continue;
                        }

                        Log.Info($"Manual merge: merging 0x{hwnd:X} into {location}.");
                        Helper.HideWindow(hwnd);
                        lock (_itemsLock)
                            _pendingConversions.Add(hwnd);
                        _ = Task.Run(() => ConvertToTabAsync(item, hwnd, location, forceNew: true, preferredWindow: host));
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn("Merge all windows failed: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Chooses the window that should become the target of a manual merge:
    /// the focused Explorer window if there is one, otherwise the recorded main
    /// window, otherwise the first visible Explorer window.
    /// </summary>
    private nint PickMergeTarget()
    {
        var foreground = WinApi.GetForegroundWindow();
        if (Helper.IsFileExplorerWindow(foreground) && WinApi.IsWindowVisible(foreground))
            return foreground;

        var main = GetMainWindowHWnd(0);
        if (main != 0) return main;

        return WinApi.FindAllWindowsEx("CabinetWClass")
            .FirstOrDefault(h => WinApi.IsWindowVisible(h) && !Helper.IsParkedOffScreen(h));
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
        if (WinApi.IsWindowHasClassName(hWnd, "ShellTabWindowClass"))
        {
            NotifyTabCreated(hWnd);
            return;
        }
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;
        if (!AutoMerge) return;

        // Explorer creates the frame window and only reveals it a moment later
        // (~150 ms), and once revealed the window stays on screen for as long as
        // Explorer keeps its own thread busy: a hide request sent at that point
        // waits behind that work, which measured 30-110 ms of a window the user
        // can see, i.e. a flash. Moving the window while it is still invisible
        // costs nothing and is already applied by the time Explorer shows it, so
        // the window is never painted on screen at all. See Helper.ParkWindow.
        if (eventType == WinApi.EVENT_OBJECT_CREATE)
        {
            TryParkNewWindow(hWnd);
            return;
        }

        // A window this app is already hiding must be hidden again the moment
        // Explorer shows it (see HideWindow): otherwise it stays visible until
        // the merge finishes and reads as a flash of the new window.
        if (Helper.IsHidden(hWnd))
        {
            if (WinApi.IsWindowVisible(hWnd))
            {
                Helper.HideWindow(hWnd);
                Log.Info($"Window 0x{hWnd:X} was shown again by Explorer; hidden again.");
            }
            return;
        }

        lock (_itemsLock)
        {
            if (_knownTopLevelWindows.Contains(hWnd)) return;
            // Nothing to merge into: leave the window alone instead of hiding
            // it and having to put it back a moment later.
            if (!HasVisibleExplorerWindow(hWnd)) return;
        }

        // A window that was moved out of the way is only registered as hidden
        // here, the first moment it could have been seen; from then on
        // HideWindow keeps it hidden and reports nothing new, so this detection
        // branch runs once per window. Windows that are not visible yet are left
        // for their SHOW event.
        var wasParked = Helper.IsTracked(hWnd);
        if (!Helper.HideWindowInBackground(hWnd)) return;

        MarkActivity();
        ScheduleShowFallback(hWnd);
        ScheduleHideRecheck(hWnd);
        RequestFastPoll();
        // dwmsEventTime is the moment Explorer made the window visible; the gap
        // to now is how long the user could actually see it before this app was
        // able to hide it, i.e. the length of the flash.
        var detectedMs = MsSinceEvent(dwmsEventTime);
        Log.Info(wasParked
            ? $"New Explorer window detected (0x{hWnd:X}); was already out of the way, hidden {detectedMs} ms after Explorer revealed it."
            : $"New Explorer window detected (0x{hWnd:X}); visible for {detectedMs} ms before hiding.");

        // Poll the shell off the UI thread: PollShell blocks on the STA
        // scheduler, and this callback now runs on the UI thread.
        ThreadPool.QueueUserWorkItem(_ => PollShell(null));
        _ = Task.Delay(60).ContinueWith(
            _ => ThreadPool.QueueUserWorkItem(_ => PollShell(null)),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Moves a freshly created Explorer window out of the way before Explorer
    /// shows it, which is the only moment at which this costs the user nothing.
    /// </summary>
    private void TryParkNewWindow(nint hWnd)
    {
        lock (_itemsLock)
        {
            if (_knownTopLevelWindows.Contains(hWnd)) return;
            // Nothing to merge into: leave the window alone instead of moving
            // it and having to put it back a moment later.
            if (!HasVisibleExplorerWindow(hWnd)) return;
        }

        if (!Helper.ParkWindow(hWnd)) return;

        // Explorer almost always reveals the window a moment later, which is
        // what starts the merge. If it never does, nothing else would put the
        // window back, so it is put back here.
        _ = Task.Delay(2_000).ContinueWith(_ => Helper.UnparkIfUntouched(hWnd), TaskScheduler.Default);
    }

    private static bool HasVisibleExplorerWindow(nint except)
    {
        foreach (var hWnd in WinApi.FindAllWindowsEx("CabinetWClass"))
        {
            if (hWnd == except) continue;
            // A window this app has already moved out of the way is not a place
            // the user can see, so it does not count as somewhere to merge into.
            if (Helper.IsParkedOffScreen(hWnd)) continue;
            if (WinApi.IsWindowVisible(hWnd)) return true;
        }

        return false;
    }

    /// <summary>
    /// Puts a window back on screen when no merge has taken it over.
    ///
    /// A window is hidden the moment it appears, but the Shell item needed to
    /// merge it only comes from a full ShellWindows pass, which takes a few
    /// hundred milliseconds. When that lookup never succeeds the window has to
    /// be given back to the user, so it stays hidden for at most this long. A
    /// conversion that is already running owns the window instead, and
    /// ConvertToTabAsync restores it when the merge fails.
    /// </summary>
    private void ScheduleShowFallback(nint hWnd)
    {
        _ = Task.Delay(1_500).ContinueWith(_ =>
        {
            lock (_itemsLock)
            {
                if (_pendingConversions.Contains(hWnd)) return;
                if (!Helper.HiddenWindows.ContainsKey(hWnd)) return;
            }

            if (Helper.ShowWindow(hWnd, removeCache: true))
                Log.Warn($"Window 0x{hWnd:X} could not be merged; restored after 1.5 s.");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// One extra visibility check while a merge is running.
    ///
    /// Explorer reveals a new window more than once as its shell view comes up,
    /// and the SHOW event normally hides it again straight away. This covers
    /// the case where a window is revealed without an event this app can see:
    /// without it the window would sit on screen for the whole merge.
    /// </summary>
    private void ScheduleHideRecheck(nint hWnd)
    {
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            lock (_itemsLock)
            {
                if (!_pendingConversions.Contains(hWnd)) return;
            }

            Helper.HideWindow(hWnd);
        }, TaskScheduler.Default);
    }

    private async Task ConvertToTabAsync(object item, nint sourceHwnd, string? location, bool forceNew = false, nint preferredWindow = 0)
    {
        var sw = Stopwatch.StartNew();
        MarkActivity();
        long searchMs = 0, createMs = 0, itemMs = 0, navMs = 0;
        var converted = false;
        try
        {
            var target = string.IsNullOrWhiteSpace(location) ? TryGetLocation(item) : location;
            if (string.IsNullOrWhiteSpace(target))
                return;

            Log.Info($"Merging 0x{sourceHwnd:X} into '{target}'.");

            // Fast path: the folder is already open as a tab, so just select
            // that tab instead of creating a new one.
            var existingTab = SearchForTab(target);
            searchMs = sw.ElapsedMilliseconds;
            if (!forceNew && existingTab != 0)
            {
                var windowHandle = WinApi.GetParent(existingTab);
                if (windowHandle != 0 && WinApi.IsWindow(windowHandle) && WinApi.IsWindow(existingTab))
                {
                    await SelectTabByHandle(windowHandle, existingTab);

                    // Only treat the conversion as done when the tab is
                    // still alive and attached to the same window. If the
                    // window was closed while we were selecting the tab, fall
                    // through to the normal path so the source window is
                    // restored instead of silently disappearing.
                    if (WinApi.IsWindow(existingTab) && WinApi.GetParent(existingTab) == windowHandle)
                    {
                        object? existingItem = null;
                        if (_tabToItem.TryGetValue(existingTab, out var cached))
                            existingItem = cached;
                        else
                            existingItem = await RunConversionInStaThread(() => FindShellItemForTab(existingTab));

                        if (existingItem != null)
                            SelectItems(existingItem, TryGetSelectedItems(item));

                        WinApi.TryActivate(windowHandle);
                        converted = true;
                        return;
                    }
                }
            }

            // Serialize only the tab-creation step; the item wait and the
            // navigation can overlap between conversions so opening several
            // folders in a row does not queue behind the first one.
            var (targetWindow, newTabHandle) = await CreateNewTabAsync(preferredWindow);
            createMs = sw.ElapsedMilliseconds;
            if (targetWindow == 0 || newTabHandle == 0)
                return;

            // Give slow Explorer extra time to register the new tab's Shell
            // item before giving up, so a half-created tab is not left at the
            // default location.
            var tabItem = await WaitForTabItemAsync(newTabHandle, 4_000);
            itemMs = sw.ElapsedMilliseconds;
            if (tabItem == null)
                return;

            // Navigate the new tab to the target via its Shell item (no simulated
            // address-bar keystrokes). Navigate2 runs when the fresh tab is not
            // already at the target, which is the normal case for a new tab.
            var currentLocation = TryGetLocation(tabItem);
            if (!string.Equals(currentLocation, target, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Navigate(tabItem, target);
                }
                catch (Exception ex)
                {
                    // Navigation failed (Explorer may be closing or busy).
                    // Close the half-created tab so no stray default tab is
                    // left behind, then restore the source window.
                    Log.Warn($"Navigation failed for '{target}': {ex.Message}");
                    if (Helper.GetAllExplorerTabs(targetWindow).Count() > 1)
                        TryQuitTabItem(tabItem);
                    return;
                }
            }
            SelectItems(tabItem, TryGetSelectedItems(item));
            navMs = sw.ElapsedMilliseconds;

            // If the target window disappeared mid-conversion, restore the
            // source window instead of pretending the tab exists.
            if (WinApi.IsWindow(newTabHandle) && WinApi.GetParent(newTabHandle) == targetWindow)
            {
                WinApi.TryActivate(targetWindow);
                converted = true;
            }
        }
        catch (Exception ex)
        {
            // Any unexpected failure leaves converted = false, so the
            // finally block below restores the source window.
            Log.Warn($"Conversion failed for 0x{sourceHwnd:X}: {ex}");
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
                // Hand the window back exactly as the user left it. A failed
                // merge must never lose the window the user opened: it would
                // otherwise stay hidden with no taskbar button.
                if (Helper.ShowWindow(sourceHwnd, removeCache: true))
                    Log.Warn($"Merge of 0x{sourceHwnd:X} failed; source window restored.");
            }

            lock (_itemsLock)
                _pendingConversions.Remove(sourceHwnd);
        }

        Log.Info(
            $"Conversion of 0x{sourceHwnd:X} finished in {sw.ElapsedMilliseconds} ms " +
            $"(search {searchMs}ms, create {createMs}ms, item {itemMs}ms, " +
            $"navigate {navMs}ms, converted={converted}).");
    }

    private async Task<(nint WindowHandle, nint TabHandle)> CreateNewTabAsync(nint preferredWindow = 0)
    {
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            var mainWindowHWnd = preferredWindow != 0 && WinApi.IsWindowVisible(preferredWindow) ? preferredWindow : GetMainWindowHWnd(0);
            if (mainWindowHWnd == 0)
            {
                // No visible Explorer window is available to merge into.
                // Restore the source window instead of opening a brand-new
                // one, so a window does not "reappear" right after the user
                // closes File Explorer.
                return (0, 0);
            }

            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();
            await RequestToOpenNewTab(mainWindowHWnd);
            var newTabHandle = await WaitForNewTabAsync(mainWindowHWnd, currentTabs, 2_000);
            return (mainWindowHWnd, newTabHandle);
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }

    /// <summary>
    /// Waits for a new Explorer tab in the given window. The WinEvent hook
    /// reports the tab the moment its window appears (event-driven, low CPU);
    /// a slow polling fallback keeps it robust if the hook does not fire for a
    /// particular tab window.
    /// </summary>
    private async Task<nint> WaitForNewTabAsync(nint window, IReadOnlyCollection<nint> currentTabs, int timeoutMs)
    {
        var eventTcs = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_tabWaitLock)
        {
            if (!_tabWaiters.TryGetValue(window, out var list))
            {
                list = new List<TabWaitRequest>();
                _tabWaiters[window] = list;
            }
            list.Add(new TabWaitRequest(currentTabs, eventTcs));
        }

        try
        {
            var eventTask = eventTcs.Task;
            var pollTask = Helper.ListenForNewExplorerTabAsync(window, currentTabs, timeoutMs, sleepMs: 50);
            var winner = await Task.WhenAny(eventTask, pollTask);
            var result = await winner;
            if (result != 0) return result;

            // Both timed out; take whichever is still non-zero.
            return await (winner == eventTask ? pollTask : eventTask);
        }
        finally
        {
            lock (_tabWaitLock)
            {
                if (_tabWaiters.TryGetValue(window, out var list))
                {
                    list.RemoveAll(r => r.Tcs == eventTcs);
                    if (list.Count == 0) _tabWaiters.Remove(window);
                }
            }
        }
    }

    /// <summary>
    /// Called on the UI thread when a new ShellTabWindowClass window appears;
    /// completes any waiter bound to its parent window.
    /// </summary>
    private void NotifyTabCreated(nint tabHandle)
    {
        var parent = WinApi.GetParent(tabHandle);
        if (parent == 0) return;

        lock (_tabWaitLock)
        {
            if (!_tabWaiters.TryGetValue(parent, out var list)) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var request = list[i];
                if (request.CurrentTabs.Contains(tabHandle)) continue;
                if (request.Tcs.TrySetResult(tabHandle))
                    list.RemoveAt(i);
            }
            if (list.Count == 0) _tabWaiters.Remove(parent);
        }
    }

    private sealed class TabWaitRequest
    {
        public TabWaitRequest(IReadOnlyCollection<nint> currentTabs, TaskCompletionSource<nint> tcs)
        {
            CurrentTabs = currentTabs;
            Tcs = tcs;
        }

        public IReadOnlyCollection<nint> CurrentTabs { get; }
        public TaskCompletionSource<nint> Tcs { get; }
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
            foreach (var (handle, info) in _tabInfos)
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
                foreach (var (handle, info) in _tabInfos)
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
            WinApi.TryActivate(windowHandle);
    }

    private async Task OpenNewWindow(string location)
    {
        var target = string.IsNullOrWhiteSpace(location) ? _defaultLocation : location;
        await RunConversionInStaThread(() =>
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
        if (Helper.IsFileExplorerWindow(_mainWindowHandle)
            && WinApi.IsWindowVisible(_mainWindowHandle)
            && !Helper.IsParkedOffScreen(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Where(h => WinApi.IsWindowVisible(h) && !Helper.IsParkedOffScreen(h))
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

    /// <summary>
    /// Looks up the Shell item for a tab handle without running the full shell
    /// snapshot. This is used while waiting for a newly created tab so the wait
    /// is both faster and cheaper than a complete PollShellCore pass.
    /// </summary>
    private object? FindShellItemForTab(nint tabHandle, int startIndex = 0)
    {
        if (_shellApp == null) return null;

        // Cheap pre-filter: only ShellWindows items hosted in the same
        // Explorer window can be this tab's item. Comparing the item HWND
        // (one COM property read) before the QueryService/GetWindow lookup
        // avoids several COM round-trips per unrelated window, which matters
        // because this runs repeatedly while waiting for the tab to register.
        var parentWindow = WinApi.GetParent(tabHandle);
        if (parentWindow == 0) return null;

        object? found = null;
        try
        {
            dynamic windows = ((dynamic)_shellApp).Windows();
            var count = (int)windows.Count;
            // Items are added in creation order, so a tab that has just been
            // created can only be one of the entries added since the last look.
            for (var i = Math.Max(startIndex, 0); i < count; i++)
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

                if (GetWindowHandle(item) != parentWindow) continue;
                if (GetTabHandle(item) != tabHandle)
                    continue;

                found = item;
                break;
            }
        }
        catch
        {
            // ShellWindows can be temporarily unavailable during tab creation.
        }

        if (found != null)
        {
            lock (_itemsLock)
                _tabToItem[tabHandle] = found;
        }

        return found;
    }

    /// <summary>
    /// Waits for the Shell item of a tab that has just been created.
    ///
    /// The tab window exists long before its browser registers with
    /// ShellWindows, and the lookup walks every open tab, which is several COM
    /// calls into the very Explorer that is still busy starting this tab. A tab
    /// that registers adds exactly one entry to ShellWindows, so the entry count
    /// is read first - one cheap call - and the expensive scan only runs when
    /// that count actually moved, plus once every 250 ms in case an entry
    /// appears without changing it.
    /// </summary>
    private async Task<object?> WaitForTabItemAsync(nint tabHandle, int timeMs)
    {
        var startTicks = Stopwatch.GetTimestamp();
        var lastCount = -1;
        var lastScanTicks = 0L;

        while (!Helper.IsTimeUp(startTicks, timeMs))
        {
            var count = await RunConversionInStaThread(GetShellWindowCount);
            if (count <= 0 || count != lastCount || Helper.IsTimeUp(lastScanTicks, 250))
            {
                // A grown list only has to be checked from where the last look
                // ended; anything else needs a full pass.
                var fromIndex = lastCount >= 0 && count > lastCount ? lastCount : 0;
                lastCount = count;
                lastScanTicks = Stopwatch.GetTimestamp();
                try
                {
                    var item = await RunConversionInStaThread(() => FindShellItemForTab(tabHandle, fromIndex));
                    if (item != null) return item;
                }
                catch
                {
                    // Explorer can be in a transient state during tab creation.
                }
            }

            await Task.Delay(10);
        }

        return null;
    }

    /// <summary>
    /// How many entries ShellWindows exposes right now: a single COM call, used
    /// as a cheap "did anything register since the last look" signal.
    /// </summary>
    private int GetShellWindowCount()
    {
        if (_shellApp == null) return -1;

        try
        {
            return (int)((dynamic)_shellApp).Windows().Count;
        }
        catch
        {
            return -1;
        }
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
        await RunConversionInStaThread(() =>
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

    private Task<T> RunInStaThread<T>(Func<T> func)
    {
        return Task.Factory.StartNew(func, CancellationToken.None, TaskCreationOptions.None, _staTaskScheduler);
    }

    private Task RunConversionInStaThread(Action action)
    {
        return Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, _conversionStaTaskScheduler);
    }

    private Task<T> RunConversionInStaThread<T>(Func<T> func)
    {
        return Task.Factory.StartNew(func, CancellationToken.None, TaskCreationOptions.None, _conversionStaTaskScheduler);
    }

    /// <summary>
    /// Milliseconds since a WinEvent timestamp. WinEvent times are 32-bit
    /// tick counts, so the difference is taken in 32-bit arithmetic and a
    /// nonsensical result (clock change, suspended machine) is reported as 0.
    /// </summary>
    private static long MsSinceEvent(uint eventTime)
    {
        var delta = unchecked((uint)Environment.TickCount64 - eventTime);
        return delta > 60_000 ? 0 : delta;
    }

    private void MarkActivity()
    {
        Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Periodically (every 30s) checks whether the app has been idle for a few
    /// minutes, then reclaims transient COM/WPF garbage and hands idle
    /// working-set pages back to the OS. Gated on idleness so it never pauses
    /// the UI during conversions or rapid tab switching.
    /// </summary>
    private void StartMemoryMaintenance()
    {
        _maintenanceTimer = new Timer(MemoryMaintenanceTick, null, MaintenanceInterval, MaintenanceInterval);
    }

    private void MemoryMaintenanceTick(object? state)
    {
        if (_disposed) return;

        var idleSeconds = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastActivityTicks)) / (double)Stopwatch.Frequency;
        if (idleSeconds < IdleTrimAfterSeconds) return;

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            WinApi.TrimWorkingSet();
        }
        catch
        {
            // Reclaiming memory must never affect the app.
        }
    }
    private void StartExplorerProcessCheck()
    {
        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        _explorerCheckTimer = new Timer(CheckForMainExplorer, null, 0, 1_000);
    }

    private void OnMainExplorerExited(object? sender, EventArgs e)
    {
        HandleMainExplorerTerminated();
    }

    private void HandleMainExplorerTerminated()
    {
        lock (_processLock)
        {
            if (_mainExplorerProcessId == 0) return;

            _mainExplorerProcessId = 0;
            try
            {
                _mainExplorerProcess?.Dispose();
            }
            catch
            {
                // The process object may already be in a terminating state.
            }
            _mainExplorerProcess = null;

            Log.Warn("Explorer process terminated; reinitializing watcher.");
            DisposeShellObjects();
            StartExplorerProcessCheck();
        }
    }

    private void DisposeShellObjects(bool restoreHiddenWindows = false)
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
            _eventObjectCreateHookId = 0;
            _eventObjectCreateHookCallback = null;
        }

        if (_eventObjectShowHookCallback != null)
        {
            if (_eventObjectShowHookId != 0)
                WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookId = 0;
            _eventObjectShowHookCallback = null;
        }

        // Windows hidden by this app are handed back only when the app itself
        // is going away. Re-initializing the watcher (after a real Explorer
        // restart, say) must never flash a half-merged window back on screen:
        // those windows are restored by ScheduleShowFallback, or by the
        // conversion that owns them, within a second or so.
        if (restoreHiddenWindows)
        {
            Helper.RestoreAllHiddenWindows();
        }
        else
        {
            // Handles that died with the old Explorer process must not linger:
            // a recycled handle could otherwise be mistaken for a window that
            // this app is still hiding.
            foreach (var hWnd in Helper.HiddenWindows.Keys.ToList())
            {
                if (!WinApi.IsWindow(hWnd))
                    Helper.HiddenWindows.TryRemove(hWnd, out _);
            }
        }

        lock (_itemsLock)
        {
            _tabInfos.Clear();
            _tabToItem.Clear();
            _knownTopLevelWindows.Clear();
            _pendingConversions.Clear();
            _firstSeenTicks.Clear();
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
        Log.Info("Explorer watcher disposed.");

        _explorerCheckTimer?.Dispose();
        _explorerCheckTimer = null;
        _maintenanceTimer?.Dispose();

        lock (_processLock)
        {
            try
            {
                _mainExplorerProcess?.Dispose();
            }
            catch
            {
                // Ignore disposal errors during shutdown.
            }
            _mainExplorerProcess = null;
        }

        DisposeShellObjects(restoreHiddenWindows: true);
        _staTaskScheduler.Dispose();
        _conversionStaTaskScheduler.Dispose();

        GC.SuppressFinalize(this);
    }
}
