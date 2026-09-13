namespace FCon.App.Services;

/// <summary>
/// One running copy at a time. A second launch signals a named event and exits; the
/// running instance wakes on that event and shows its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\FCon.SingleInstance";
    private const string SignalName = @"Global\FCon.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly CancellationTokenSource? _cts;

    private SingleInstance(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsFirstInstance = isOwner;

        if (!isOwner) return;

        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another launch asks to show the window.</summary>
    public event Action? ActivationRequested;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isOwner);
        return new SingleInstance(mutex, isOwner);
    }

    /// <summary>Ask the already-running instance to come to the foreground.</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down; nothing to activate.
        }
    }

    private void ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_signal!.WaitOne(500)) ActivationRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _signal?.Dispose();
        if (IsFirstInstance) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
