namespace AiLimitMonitor.Tray;

/// <summary>
/// Keeps at most one tray icon per Windows session. The first process owns a named mutex; a
/// later launch finds it taken, pokes a named event so the running instance can point at
/// itself, and quits. The names are session-local (<c>Local\</c>) rather than machine-global:
/// two users signed into the same machine should each get their own icon.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\AiLimitMonitor.Tray.Instance";
    private const string ActivateEventName = @"Local\AiLimitMonitor.Tray.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// The ownership token when this process is the only instance, or null when another one is
    /// already running — in which case it has been asked to surface itself and this process
    /// should exit without showing anything.
    /// </summary>
    public static SingleInstance? Acquire()
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // The name exists but is locked down (e.g. the running instance is elevated),
            // which is itself proof that someone else owns it.
            ReportAlreadyRunning();
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance was killed without releasing it — ownership passes to us.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            ReportAlreadyRunning();
            return null;
        }

        return new SingleInstance(mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName));
    }

    /// <summary>
    /// True once per extra launch attempt: another process just tried to start and gave up.
    /// Polled from the UI thread, so the caller can react without any cross-thread marshalling.
    /// </summary>
    public bool WasLaunchAttempted() => _activate.WaitOne(TimeSpan.Zero);

    /// <summary>Nudges the running instance; when it cannot be reached (different elevation,
    /// for instance) tells the user directly instead of exiting silently.</summary>
    private static void ReportAlreadyRunning()
    {
        if (TrySignalRunningInstance())
            return;

        MessageBox.Show(
            "AI Limit Monitor 已經在執行中，圖示就在系統匣（右下角時鐘旁）。",
            "AI Limit Monitor",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static bool TrySignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
                return false;
            using (activate)
                activate.Set();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>Must run on the thread that acquired the mutex — i.e. after Application.Run returns.</summary>
    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activate.Dispose();
    }
}
