using System.Diagnostics;

namespace NativeNCA.App.Services;

/// <summary>
/// Named kernel objects shared by NativeNCA processes of this user session. The mutex is held by the one
/// instance that runs the server; the events ask it to show its window or to exit, and tell a process that
/// handed over (update, rollback, install) that its successor has taken over.
/// </summary>
internal static class InstanceSignals
{
    public const string MutexName = @"Local\NativeNCA.SingleInstance";
    public const string ShowEventName = @"Local\NativeNCA.ShowMainWindow";
    public const string ExitEventName = @"Local\NativeNCA.Exit";
    public const string HandOffReadyEventName = @"Local\NativeNCA.HandOffReady";

    /// <summary>Some NativeNCA holds (or is about to release) the single-instance mutex.</summary>
    public static bool IsInstanceRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static void ShowRunningInstance()
    {
        try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* it is shutting down */ }
    }

    /// <summary>Tells the successor of a hand-over that this process is up and serving.</summary>
    public static void SignalHandOffReady()
    {
        if (EventWaitHandle.TryOpenExisting(HandOffReadyEventName, out var ready))
        {
            using (ready) ready.Set();
        }
    }

    /// <summary>
    /// Closes the running NativeNCA, if any, and waits until it has released the mutex (and so the port).
    /// Versions before 0.2.0 do not listen for the exit event; those processes are terminated.
    /// </summary>
    public static Task<bool> CloseRunningInstanceAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (!IsInstanceRunning()) return true;

        bool asked = false;
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
        {
            using (exit) exit.Set();
            asked = true;
        }

        if (asked && WaitForMutexRelease(timeout)) return true;

        foreach (var process in Process.GetProcessesByName("NativeNCA").Where(p => p.Id != Environment.ProcessId))
        {
            try
            {
                if (process.SessionId == Process.GetCurrentProcess().SessionId)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Already gone or not ours.
            }
            finally
            {
                process.Dispose();
            }
        }
        return WaitForMutexRelease(TimeSpan.FromSeconds(5));
    });

    /// <summary>Acquires and immediately releases the mutex on this (worker) thread.</summary>
    private static bool WaitForMutexRelease(TimeSpan timeout)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return true;
        using (mutex)
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // the owner exited without releasing it
            }
            if (acquired) mutex.ReleaseMutex();
            return acquired;
        }
    }
}
