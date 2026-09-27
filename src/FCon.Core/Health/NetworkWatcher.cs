using System.Net.NetworkInformation;

namespace FCon.Core.Health;

/// <summary>
/// Turns Windows network events into a single, debounced "look now" for the
/// supervisor. Address changes arrive in bursts of several per second while an adapter
/// settles; the tunnel only needs one probe once the burst is over.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    private readonly Action<string> _nudge;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public NetworkWatcher(Action<string> nudge, TimeSpan? debounce = null)
    {
        _nudge = nudge;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Schedule("Network address changed");

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Schedule(e.IsAvailable ? "Network is back" : "Network went away");

    /// <summary>Coalesce a burst of events into one nudge after the last of them.</summary>
    private void Schedule(string reason)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _pending?.Cancel();
            _pending?.Dispose();
            cts = _pending = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || cts.IsCancellationRequested) return;
            }
            _nudge(reason);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }

        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }
}
