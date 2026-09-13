using FCon.Abstractions.Model;
using FCon.Core.Config;
using FCon.Core.Engine;

namespace FCon.Core.Health;

public enum LinkState
{
    Idle,
    Connecting,
    /// <summary>Core is up; proving traffic actually flows before claiming success.</summary>
    Verifying,
    Healthy,
    /// <summary>Connected, but probes are failing. Recovery is in progress.</summary>
    Degraded,
    Recovering,
    Failed,
}

public sealed record LinkSnapshot(
    LinkState State,
    ProxyNode? Node,
    int? LatencyMs,
    string? Message,
    int ConsecutiveFailures)
{
    /// <summary>Where traffic came out, once confirmed. Null until a connection is verified.</summary>
    public ExitInfo? Exit { get; init; }

    public bool IsUsable => State is LinkState.Healthy or LinkState.Degraded;
}

/// <summary>
/// Turns "the core started" into "the tunnel works, and keeps working".
///
/// A proxy core binds its listener whether or not the server behind it is alive, so a
/// connection is not trusted until traffic has been carried end to end. After that the
/// tunnel is probed on a timer: a failing link is restarted, and a server that cannot be
/// recovered is set aside in favour of the next best one.
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly EngineController _engine;
    private readonly QualityStore _quality;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<ProxyNode>> _candidates;
    private readonly Action<string, bool> _log;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _monitor;
    private Task? _monitorTask;

    /// <summary>
    /// The running connect-and-retry loop. It lives in the background rather than inside
    /// the caller's await: retrying is unbounded, so a UI command that waited for it to
    /// finish would stay busy forever and leave the user unable to press Stop.
    /// </summary>
    private CancellationTokenSource? _attempt;
    private Task? _attemptTask;

    /// <summary>Set while the supervisor itself is reconnecting, so a core exit is not double-handled.</summary>
    private bool _recovering;

    /// <summary>True once the user asks for a connection; cleared on an explicit disconnect.</summary>
    private bool _wanted;

    /// <summary>Kept across health updates so the country does not flicker away between probes.</summary>
    private ExitInfo? _exit;

    public ConnectionSupervisor(
        EngineController engine,
        QualityStore quality,
        Func<AppSettings> settings,
        Func<IReadOnlyList<ProxyNode>> candidates,
        Action<string, bool> log)
    {
        _engine = engine;
        _quality = quality;
        _settings = settings;
        _candidates = candidates;
        _log = log;

        _engine.StatusChanged += OnEngineStatusChanged;
    }

    public LinkSnapshot Current { get; private set; } = new(LinkState.Idle, null, null, null, 0);

    public event Action<LinkSnapshot>? Changed;

    // ------------------------------------------------------------- connect

    /// <summary>
    /// Connect to a specific server, verify it, and start supervising. Falls over to the
    /// next ranked server when verification fails and failover is enabled.
    /// </summary>
    /// <summary>
    /// Start connecting, and keep trying until it works or the user stops it. Returns as
    /// soon as the attempt is under way, not when it succeeds — progress arrives through
    /// <see cref="Changed"/>.
    /// </summary>
    public async Task<bool> ConnectAsync(ProxyNode node, CancellationToken ct = default)
    {
        _wanted = true;
        await StopAttemptAsync().ConfigureAwait(false);
        await StopMonitorAsync().ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _attempt = cts;
            _attemptTask = Task.Run(() => AttemptLoopAsync(node, cts.Token), CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// Try every server, best first, and start over after a pause. Gives up only when the
    /// user disconnects: a link that is down is worth retrying indefinitely, and stopping
    /// after N tries just means the tunnel stays dead until someone notices.
    /// </summary>
    private async Task AttemptLoopAsync(ProxyNode node, CancellationToken ct)
    {
        var tried = new HashSet<Guid>();
        var current = node;
        var round = 0;

        while (_wanted && !ct.IsCancellationRequested)
        {
            // Re-read each pass so changing a setting takes effect without reconnecting.
            var settings = _settings();
            tried.Add(current.Id);

            if (await TryBringUpAsync(current, settings, ct).ConfigureAwait(false))
            {
                StartMonitor();
                return;
            }

            if (!_wanted || ct.IsCancellationRequested) break;

            var next = settings.AutoFailover ? PickNext(tried) : null;
            if (next is not null)
            {
                _log($"Failing over to {next.DisplayName}.", false);
                Publish(LinkState.Recovering, current, null, $"Trying {next.DisplayName}...");
                current = next;
                continue;
            }

            // Every candidate has been tried this round. Pause, then start again from the
            // best-ranked server; quarantines may have lapsed by then.
            round++;
            var wait = RetryDelay(round);
            Publish(
                LinkState.Recovering,
                current,
                null,
                $"No server responded. Retrying in {wait.TotalSeconds:0}s (round {round}).");
            _log($"All servers failed; retrying in {wait.TotalSeconds:0}s.", true);

            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            tried.Clear();
            current = PickNext(tried) ?? current;
        }

        // Only reached by an explicit stop; a give-up path no longer exists.
        if (!_wanted) Publish(LinkState.Idle, null, null, null);
    }

    /// <summary>Backoff between full rounds: quick at first, then easing off to a minute.</summary>
    private static TimeSpan RetryDelay(int round) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(round, 6))));

    private async Task StopAttemptAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _attempt;
            task = _attemptTask;
            _attempt = null;
            _attemptTask = null;
        }

        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Cooperative cancellation; a slow probe must not block stopping.
            }
        }
        cts.Dispose();
    }

    /// <summary>Connect to the best-ranked server we know of.</summary>
    public async Task<bool> ConnectBestAsync(CancellationToken ct = default)
    {
        var best = _quality.Rank(_candidates()).FirstOrDefault();
        if (best is null)
        {
            Publish(LinkState.Failed, null, null, "There are no servers to connect to.");
            return false;
        }
        return await ConnectAsync(best, ct).ConfigureAwait(false);
    }

    /// <summary>The only thing that ends a retry loop.</summary>
    public async Task DisconnectAsync()
    {
        _wanted = false;
        _exit = null;
        await StopAttemptAsync().ConfigureAwait(false);
        await StopMonitorAsync().ConfigureAwait(false);
        await _engine.DisconnectAsync().ConfigureAwait(false);
        Publish(LinkState.Idle, null, null, null);
    }

    /// <summary>Bring one server up and confirm it carries traffic.</summary>
    private async Task<bool> TryBringUpAsync(ProxyNode node, AppSettings settings, CancellationToken ct)
    {
        Publish(LinkState.Connecting, node, null, $"Connecting to {node.DisplayName}...");

        var status = await _engine.ConnectAsync(node, ct).ConfigureAwait(false);
        if (status.State != ConnectionState.Connected)
        {
            _quality.RecordFailure(node.Id, status.Message);
            Publish(LinkState.Failed, node, null, status.Message);
            return false;
        }

        if (!settings.VerifyOnConnect)
        {
            Publish(LinkState.Healthy, node, null, "Connected (not verified).");
            return true;
        }

        Publish(LinkState.Verifying, node, null, "Checking that traffic flows...");

        var health = await HealthProbe.CheckAsync(
            settings.HttpPort, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct).ConfigureAwait(false);

        if (!health.Ok)
        {
            _quality.RecordFailure(node.Id, health.Describe());
            _log($"{node.DisplayName} connected but carried no traffic: {health.Describe()}", true);

            // Leave nothing half-configured behind before trying the next server.
            await _engine.DisconnectAsync().ConfigureAwait(false);
            Publish(LinkState.Failed, node, null, $"No traffic through {node.DisplayName}: {health.Describe()}");
            return false;
        }

        _quality.RecordSuccess(node.Id);
        _quality.RecordLatency(node.Id, health.LatencyMs);
        Publish(LinkState.Healthy, node, health.LatencyMs, $"Connected via {node.DisplayName}");

        // Ask the far side where the traffic surfaced. This is both the country readout
        // and the strongest cheap proof the tunnel is genuinely carrying data.
        _exit = await ExitInfoProbe.LookupAsync(settings.HttpPort, ct: ct).ConfigureAwait(false);
        if (_exit is not null)
        {
            _log($"Exit: {_exit.Describe()}", false);
            Publish(LinkState.Healthy, node, health.LatencyMs, $"Connected via {node.DisplayName}");
        }

        return true;
    }

    private ProxyNode? PickNext(HashSet<Guid> exclude) =>
        _quality.Rank(_candidates().Where(n => !exclude.Contains(n.Id))).FirstOrDefault();

    // ------------------------------------------------------------ monitor

    private void StartMonitor()
    {
        var settings = _settings();
        if (!settings.ContinuousHealthCheck) return;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _monitor = cts;
            _monitorTask = Task.Run(() => MonitorLoopAsync(cts.Token), CancellationToken.None);
        }
    }

    private async Task StopMonitorAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _monitor;
            task = _monitorTask;
            _monitor = null;
            _monitorTask = null;
        }

        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The loop is cooperative; a stuck probe must not block disconnecting.
            }
        }
        cts.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            var settings = _settings();
            var interval = TimeSpan.FromSeconds(Math.Max(5, settings.HealthCheckIntervalSeconds));

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || _engine.ActiveNode is not { } node) return;

            var health = await HealthProbe.CheckAsync(
                settings.HttpPort, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            if (health.Ok)
            {
                if (failures > 0) _log($"{node.DisplayName} recovered.", false);
                failures = 0;
                _quality.RecordLatency(node.Id, health.LatencyMs);
                Publish(LinkState.Healthy, node, health.LatencyMs, $"Connected via {node.DisplayName}");
                continue;
            }

            failures++;
            _log($"Health check failed for {node.DisplayName}: {health.Describe()} ({failures})", true);
            Publish(LinkState.Degraded, node, null, $"Link unhealthy: {health.Describe()}", failures);

            if (failures < Math.Max(1, settings.UnhealthyThreshold)) continue;

            _quality.RecordFailure(node.Id, health.Describe());
            if (!settings.AutoReconnect) return;

            // Hand off to recovery and let this loop end; the new connection starts its own.
            ScheduleRecovery(node);
            return;
        }
    }

    /// <summary>
    /// Hand the connection back to a fresh attempt loop, starting from this server.
    /// Scheduled rather than awaited: the caller is usually the monitor loop, and
    /// restarting stops that loop — waiting for itself to finish would stall.
    /// </summary>
    private void ScheduleRecovery(ProxyNode node)
    {
        lock (_gate)
        {
            if (_recovering || IsAttempting) return;
            _recovering = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!_wanted) return;

                _log($"Restarting {node.DisplayName}...", false);
                Publish(LinkState.Recovering, node, null, $"Restarting {node.DisplayName}...");
                await ConnectAsync(node).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A recovery that throws must not take the process down with it.
                _log($"Recovery failed to start: {ex.Message}", true);
            }
            finally
            {
                lock (_gate) _recovering = false;
            }
        });
    }

    /// <summary>True while a connect-and-retry loop is already running.</summary>
    private bool IsAttempting
    {
        get
        {
            lock (_gate) return _attemptTask is { IsCompleted: false };
        }
    }

    /// <summary>A core that dies on its own is a failure of the current server, not a user action.</summary>
    private void OnEngineStatusChanged(ConnectionStatus status)
    {
        if (status.State != ConnectionState.Faulted) return;
        if (!_wanted) return;

        // A retry loop already owns the outcome; its own failures arrive here too, and
        // reacting to them would cancel and restart the very loop handling them.
        if (IsAttempting) return;

        lock (_gate)
        {
            if (_recovering) return;
        }

        if (status.Node is not { } node) return;

        _quality.RecordFailure(node.Id, status.Message);
        _log($"Connection to {node.DisplayName} dropped: {status.Message}", true);

        if (_settings().AutoReconnect) ScheduleRecovery(node);
        else Publish(LinkState.Failed, node, null, status.Message);
    }

    // ------------------------------------------------------------- helpers

    private void Publish(
        LinkState state,
        ProxyNode? node,
        int? latency,
        string? message,
        int failures = 0)
    {
        Current = new LinkSnapshot(state, node, latency, message, failures)
        {
            Exit = state is LinkState.Healthy or LinkState.Degraded ? _exit : null,
        };
        Changed?.Invoke(Current);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.StatusChanged -= OnEngineStatusChanged;
        await StopMonitorAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
