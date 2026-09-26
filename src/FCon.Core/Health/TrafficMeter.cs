using System.Diagnostics;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Net;

namespace FCon.Core.Health;

public sealed record TrafficSample(
    long UploadTotal,
    long DownloadTotal,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    int Connections)
{
    public static TrafficSample Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>
    /// Formatted with the invariant culture on purpose. The unit labels are English, so a
    /// locale-specific decimal separator would produce mixed output like "1,5 MB", and the
    /// value would change shape depending on who is running the app.
    /// </summary>
    public static string FormatBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var size = Math.Abs(value);
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return unit == 0
            ? string.Create(culture, $"{size:0} {units[unit]}")
            : string.Create(culture, $"{size:0.#} {units[unit]}");
    }

    public static string FormatRate(double bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";
}

/// <summary>
/// Reads live byte counters from the running core.
///
/// sing-box serves a Clash-compatible API, so totals come straight from the core rather
/// than being guessed. Xray only exposes its statistics over gRPC, which would mean
/// bundling generated protobuf clients for a status-bar readout, so it reports nothing
/// and the UI says so rather than showing an invented number.
///
/// Besides the status bar, the totals feed the supervisor's passive health check: bytes
/// arriving are proof the tunnel works without sending a probe.
/// </summary>
public sealed class TrafficMeter : IAsyncDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastUp;
    private long _lastDown;
    private long _lastTicks;

    public TrafficMeter(Func<AppSettings> settings) => _settings = settings;

    public TrafficSample Current { get; private set; } = TrafficSample.Empty;

    /// <summary>False when the active engine cannot report counters.</summary>
    public bool Available { get; private set; }

    public event Action<TrafficSample>? Sampled;

    public void Start()
    {
        Stop();

        var settings = _settings();
        Available = settings.Engine == EngineKind.SingBox && settings.ApiPort > 0;

        // Reset so the first delta is not computed against a previous session.
        lock (_gate)
        {
            _lastUp = 0;
            _lastDown = 0;
            _lastTicks = 0;
            Current = TrafficSample.Empty;
        }

        if (!Available)
        {
            Sampled?.Invoke(TrafficSample.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _loop = Task.Run(() => PollAsync(settings.ApiPort, settings.ApiSecret, cts.Token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        cts?.Cancel();
        cts?.Dispose();

        Current = TrafficSample.Empty;
        Sampled?.Invoke(Current);
    }

    private async Task PollAsync(int apiPort, string? secret, CancellationToken ct)
    {
        // Loopback only: this must never be routed through the proxy it is measuring.
        using var api = new ClashApiClient(apiPort, secret);

        while (!ct.IsCancellationRequested)
        {
            // The core may still be starting, or has gone away. Either way, keep the
            // last sample and try again rather than tearing the meter down.
            if (await api.GetConnectionsAsync(ct).ConfigureAwait(false) is { } snapshot)
                Publish(snapshot.UploadTotal, snapshot.DownloadTotal, snapshot.Connections);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(long up, long down, int connections)
    {
        TrafficSample sample;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            double upRate = 0, downRate = 0;

            if (_lastTicks != 0)
            {
                var seconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
                if (seconds > 0.05)
                {
                    // Counters reset when the core restarts; a negative delta means that
                    // happened, and reporting a huge negative rate would be nonsense.
                    upRate = Math.Max(0, up - _lastUp) / seconds;
                    downRate = Math.Max(0, down - _lastDown) / seconds;
                }
            }

            _lastUp = up;
            _lastDown = down;
            _lastTicks = now;

            sample = new TrafficSample(up, down, upRate, downRate, connections);
            Current = sample;
        }

        Sampled?.Invoke(sample);
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
