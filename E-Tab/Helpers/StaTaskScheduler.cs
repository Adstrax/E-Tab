using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ETab.WinAPI;

namespace ETab.Helpers;

public sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _disposed;

    public StaTaskScheduler(string threadName = "STA Thread")
    {
        _staThread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>
    /// Runs the tasks queued for this thread and keeps a Windows message loop
    /// alive while waiting for the next one.
    ///
    /// The message loop is not optional. A COM object lives in the apartment
    /// that created it and can only be called from another thread through that
    /// apartment's message queue, so a thread that never pumps leaves those
    /// calls waiting forever. The shell items this app reads, navigates and
    /// closes belong to these threads, and a call that never came back is what
    /// stopped a merge halfway - with the folder opened neither as a tab nor as
    /// a window. The old loop here only drained a queue and never pumped.
    /// </summary>
    private void Run()
    {
        while (!_disposed)
        {
            if (_tasks.TryTake(out var task))
            {
                try
                {
                    TryExecuteTask(task);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Exception in STA thread: {ex}");
                }

                continue;
            }

            PumpMessages();

            // Wait for a queued task, for a message, or for the tick. Waiting on
            // the task handle alone would leave messages unprocessed whenever the
            // queue is empty, which is almost all of the time.
            try
            {
                var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
                WinApi.MsgWaitForMultipleObjectsEx(1, handles, 20, WinApi.QS_ALLINPUT, 0);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static void PumpMessages()
    {
        while (WinApi.PeekMessage(out var msg, 0, 0, 0, WinApi.PM_REMOVE))
        {
            WinApi.TranslateMessage(ref msg);
            WinApi.DispatchMessage(ref msg);
        }
    }

    // Called by the TPL to queue a task
    protected override void QueueTask(Task task)
    {
        try
        {
            _tasks.Add(task);
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding has already been called.
        }
    }

    // (Optional) TPL calls this to see if we can run inline
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // Since this scheduler only wants tasks on its dedicated thread, we *generally*
        // disallow inlining unless this is the STA thread itself.
        if (Thread.CurrentThread == _staThread)
        {
            return TryExecuteTask(task);
        }
        return false;
    }

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        return _tasks.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        _tasks.CompleteAdding();
        _wake.Set();
        // The STA thread can be blocked in a COM call; it is a background
        // thread, so the process can still exit without waiting for it.
        _staThread.Join(TimeSpan.FromSeconds(2));
        _tasks.Dispose();
        _wake.Dispose();
    }
}