using System;
using System.Diagnostics;
using System.Threading;
using ETab.WinAPI;

namespace ETab.Helpers;

/// <summary>
/// Returns unused runtime pages to Windows after both the user and E-Tab have
/// been idle for long enough. The timer only asks Windows to trim the process
/// working set; it does not force a garbage collection.
/// </summary>
internal sealed class IdleMemoryTrimmer : IDisposable
{
    internal static readonly TimeSpan RequiredIdleTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumTimeBetweenTrims = TimeSpan.FromMinutes(5);

    private readonly Func<long> _lastActivityTimestamp;
    private readonly Timer _timer;
    private int _trimRunning;
    private int _disposed;
    private long _lastTrimTimestamp;
    private bool _hasTrimmed;

    public IdleMemoryTrimmer(Func<long> lastActivityTimestamp)
    {
        _lastActivityTimestamp = lastActivityTimestamp;
        _timer = new Timer(
            OnTimer,
            null,
            CheckInterval,
            CheckInterval);
    }

    internal static bool ShouldTrim(
        TimeSpan systemIdleTime,
        TimeSpan appIdleTime)
    {
        return systemIdleTime >= RequiredIdleTime &&
               appIdleTime >= RequiredIdleTime;
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _trimRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            if (_hasTrimmed &&
                Stopwatch.GetElapsedTime(_lastTrimTimestamp, now) <
                MinimumTimeBetweenTrims)
            {
                return;
            }

            var appIdleTime = Stopwatch.GetElapsedTime(
                _lastActivityTimestamp(),
                now);
            if (!ShouldTrim(WinApi.GetUserIdleTime(), appIdleTime))
                return;

            _lastTrimTimestamp = now;
            _hasTrimmed = true;
            WinApi.TrimWorkingSet();
        }
        catch
        {
            // Memory trimming is opportunistic and must never affect the app.
        }
        finally
        {
            Volatile.Write(ref _trimRunning, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();
    }
}
