using FCon.Abstractions.Model;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Net;

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

    /// <summary>Set on <see cref="LinkState.Failed"/> when the cause is this machine, not the server.</summary>
    public FaultKind Fault { get; init; }

    public bool IsUsable => State is LinkState.Healthy or LinkState.Degraded;
}

/// <summary>
/// Knobs that only tests need to turn. Production uses the defaults.
/// </summary>
public sealed record SupervisorTiming
{
    /// <summary>Backoff between full rounds: quick at first, then easing off to a minute.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } =
        round => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(round, 6))));

    /// <summary>Overrides the settings-driven health interval when set.</summary>
    public TimeSpan? MonitorInterval { get; init; }

    /// <summary>Once a probe has failed, re-probe this quickly instead of waiting a full interval.</summary>
    public TimeSpan DegradedInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to give the core after re-pointing its selector before probing again.</summary>
    public TimeSpan SwitchSettle { get; init; } = TimeSpan.FromSeconds(3);

    public static SupervisorTiming Default { get; } = new();
}

/// <summary>
/// Turns "the core started" into "the tunnel works, and keeps working".
///
/// A proxy core binds its listener whether or not the server behind it is alive, so a
/// connection is not trusted until traffic has been carried end to end. After that the
/// tunnel is watched: real traffic counts as proof on its own, and an active probe runs
/// only when nothing has flowed. A failing link is first handed to the core's own
/// automatic group, which swaps the server without a restart; only when that is not
/// possible is the core restarted, and a server that cannot be recovered is set aside in
/// favour of the next best one.
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly IEngineController _engine;
    private readonly QualityStore _quality;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<ProxyNode>> _candidates;
    private readonly Action<string, bool> _log;
    private readonly IHealthProbe _probe;
    private readonly Func<TrafficSample?>? _traffic;
    private readonly SupervisorTiming _timing;

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

    /// <summary>
    /// The server traffic is actually using. Equals the primary unless the core's
    /// automatic group has taken over, in which case it is whatever the group picked.
    /// </summary>
    private ProxyNode? _liveNode;

    private long _passiveMark;
    private bool _passiveMarkValid;

    /// <summary>Released by <see cref="Nudge"/> to make the monitor probe now rather than on its timer.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    public ConnectionSupervisor(
        IEngineController engine,
        QualityStore quality,
        Func<AppSettings> settings,
        Func<IReadOnlyList<ProxyNode>> candidates,
        Action<string, bool> log,
        IHealthProbe? probe = null,
        Func<TrafficSample?>? traffic = null,
        SupervisorTiming? timing = null)
    {
        _engine = engine;
        _quality = quality;
        _settings = settings;
        _candidates = candidates;
        _log = log;
        _probe = probe ?? new DefaultHealthProbe();
        _traffic = traffic;
        _timing = timing ?? SupervisorTiming.Default;

        _engine.StatusChanged += OnEngineStatusChanged;
    }

    public LinkSnapshot Current { get; private set; } = new(LinkState.Idle, null, null, null, 0);

    public event Action<LinkSnapshot>? Changed;

    // ------------------------------------------------------------- connect

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

            // After the first failure, let the core choose from its group from the
            // start rather than betting on one server again.
            var autoSelect = settings.PreferBestServer || tried.Count > 1 || round > 0;

            var outcome = await TryBringUpAsync(current, settings, autoSelect, ct).ConfigureAwait(false);
            if (outcome == BringUp.Up)
            {
                StartMonitor();
                return;
            }

            // No core, a core too old, a port taken: no other server and no amount of
            // retrying will change that. Stop, keep the message on screen, and hand
            // the decision back to the user.
            if (outcome == BringUp.Blocked)
            {
                _wanted = false;
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
            var wait = _timing.RetryDelay(round);
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

            // With failover off the user chose this server; a new round retries it,
            // not whichever server happens to rank best.
            if (settings.AutoFailover) current = PickNext(tried) ?? current;
        }

        // Only reached by an explicit stop; a give-up path no longer exists.
        if (!_wanted) Publish(LinkState.Idle, null, null, null);
    }

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
        _liveNode = null;
        await StopAttemptAsync().ConfigureAwait(false);
        await StopMonitorAsync().ConfigureAwait(false);
        await _engine.DisconnectAsync().ConfigureAwait(false);
        Publish(LinkState.Idle, null, null, null);
    }

    private enum BringUp { Up, ServerFailed, Blocked }

    /// <summary>Bring one server up and confirm it carries traffic.</summary>
    private async Task<BringUp> TryBringUpAsync(ProxyNode node, AppSettings settings, bool autoSelect, CancellationToken ct)
    {
        Publish(LinkState.Connecting, node, null, $"Connecting to {node.DisplayName}...");
        _liveNode = null;
        // A previous connection's exit must not be shown against this one.
        _exit = null;
        ResetPassive();

        var status = await _engine.ConnectAsync(node, autoSelect, ct).ConfigureAwait(false);
        if (status.State != ConnectionState.Connected)
        {
            if (status.IsEnvironmentFault)
            {
                // Not the server's fault; its record must not suffer for a missing core.
                _log(status.Message ?? "Cannot connect on this machine.", true);
                Publish(LinkState.Failed, node, null, status.Message, fault: status.Fault);
                return BringUp.Blocked;
            }

            _quality.RecordFailure(node.Id, status.Message);
            Publish(LinkState.Failed, node, null, status.Message);
            return BringUp.ServerFailed;
        }

        await PinSelectorAsync(ct).ConfigureAwait(false);

        if (!settings.VerifyOnConnect)
        {
            _liveNode = await ResolveLiveNodeAsync(node, ct).ConfigureAwait(false);
            Publish(LinkState.Healthy, _liveNode, null, "Connected (not verified).");
            return BringUp.Up;
        }

        Publish(LinkState.Verifying, node, null, "Checking that traffic flows...");

        var health = await _probe.CheckAsync(
            settings.HttpPort, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct).ConfigureAwait(false);

        ProxyNode live;
        if (health.Ok)
        {
            live = await ResolveLiveNodeAsync(node, ct).ConfigureAwait(false);
        }
        else
        {
            _quality.RecordFailure(node.Id, health.Describe());
            _log($"{node.DisplayName} connected but carried no traffic: {health.Describe()}", true);

            // The core may have other servers on board: let it swap before we tear down.
            var switched = await TrySwitchToAutoAsync(node, settings, ct).ConfigureAwait(false);
            if (switched is null)
            {
                // Leave nothing half-configured behind before trying the next server.
                await _engine.DisconnectAsync().ConfigureAwait(false);
                Publish(LinkState.Failed, node, null, $"No traffic through {node.DisplayName}: {health.Describe()}");
                return BringUp.ServerFailed;
            }

            (live, health) = switched.Value;
        }

        _quality.RecordSuccess(live.Id);
        _quality.RecordLatency(live.Id, health.LatencyMs);
        _liveNode = live;
        Publish(LinkState.Healthy, live, health.LatencyMs, $"Connected via {live.DisplayName}");

        // Ask the far side where the traffic surfaced. This is both the country readout
        // and the strongest cheap proof the tunnel is genuinely carrying data.
        await RefreshExitAsync(live, health.LatencyMs, settings, ct).ConfigureAwait(false);
        return BringUp.Up;
    }

    /// <summary>
    /// Force the selector onto the server this connection was built for. sing-box
    /// persists a selector's last choice in its cache file and restores it on start,
    /// overriding the config's <c>default</c>; without this a session that ended on
    /// the automatic group starts on it again, and verification tests the wrong thing.
    /// The API can lag the listener by a moment, so the call is retried briefly.
    /// </summary>
    private async Task PinSelectorAsync(CancellationToken ct)
    {
        var config = _engine.ActiveConfig;
        var api = _engine.Api;
        if (config?.SelectorTag is null || config.AutoTag is null || api is null) return;

        var wanted = config.StartsOnAuto ? config.AutoTag : config.PrimaryTag;
        for (var attempt = 0; attempt < 8 && !ct.IsCancellationRequested; attempt++)
        {
            var selector = await api.GetProxyAsync(config.SelectorTag, ct).ConfigureAwait(false);
            if (selector is not null)
            {
                if (string.Equals(selector.Now, wanted, StringComparison.Ordinal)) return;
                if (await api.SelectAsync(config.SelectorTag, wanted, ct).ConfigureAwait(false))
                {
                    _log($"Selector was on \"{selector.Now}\"; set to \"{wanted}\".", false);
                    return;
                }
            }

            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _log("Could not pin the core's selector; verification may test the group instead of the chosen server.", true);
    }

    /// <summary>
    /// Ask the monitor to probe now instead of waiting for its timer: the network
    /// changed, the machine woke up, or something else made the last verdict stale.
    /// Harmless when nothing is being monitored.
    /// </summary>
    public void Nudge(string reason)
    {
        if (!IsMonitoring) return;

        _log($"{reason}; checking the tunnel now.", false);
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // A nudge is already pending; one is enough.
            }
        }
    }

    private async Task RefreshExitAsync(ProxyNode node, int? latency, AppSettings settings, CancellationToken ct)
    {
        var exit = await _probe.LookupExitAsync(settings.HttpPort, ct).ConfigureAwait(false);
        if (exit is null || ct.IsCancellationRequested) return;

        _exit = exit;
        _log($"Exit: {exit.Describe()}", false);
        if (Current.State == LinkState.Healthy)
            Publish(LinkState.Healthy, node, latency, $"Connected via {node.DisplayName}");
    }

    /// <summary>
    /// The exit changed with the server: drop the old answer, let the switch settle,
    /// then ask again. Until it answers the UI says "exit unknown" rather than
    /// showing the previous server's country.
    /// </summary>
    private async Task RefreshExitAfterMoveAsync(ProxyNode node, int? latency, AppSettings settings, CancellationToken ct)
    {
        _exit = null;
        try
        {
            await Task.Delay(_timing.SwitchSettle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RefreshExitAsync(node, latency, settings, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-point the core's selector at its automatic group and see whether traffic
    /// flows again. Null when the config has no group, the core has no API, the group is
    /// already in charge, or the switch did not help. This is the path that avoids a
    /// restart: nothing is torn down, and connections from other apps keep working.
    /// </summary>
    private async Task<(ProxyNode Node, HealthResult Health)?> TrySwitchToAutoAsync(
        ProxyNode failing,
        AppSettings settings,
        CancellationToken ct)
    {
        var config = _engine.ActiveConfig;
        var api = _engine.Api;
        if (config?.SelectorTag is null || config.AutoTag is null || api is null) return null;

        var selector = await api.GetProxyAsync(config.SelectorTag, ct).ConfigureAwait(false);
        if (selector is null) return null;
        if (string.Equals(selector.Now, config.AutoTag, StringComparison.Ordinal)) return null;

        if (!await api.SelectAsync(config.SelectorTag, config.AutoTag, ct).ConfigureAwait(false)) return null;

        _log("Handed the connection to the core's automatic group.", false);
        Publish(LinkState.Recovering, failing, null, "Switching server inside the core...");

        try
        {
            await Task.Delay(_timing.SwitchSettle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var health = await _probe.CheckAsync(
            settings.HttpPort, settings.LatencyTestUrl, settings.LatencyTimeoutMs, ct).ConfigureAwait(false);
        if (!health.Ok) return null;

        var live = await ResolveLiveNodeAsync(failing, ct).ConfigureAwait(false);
        _exit = null;
        _log($"Traffic now flows through {live.DisplayName}.", false);
        return (live, health);
    }

    /// <summary>
    /// Which server the core is really using. With a selector it may be any member of
    /// the automatic group; without one it is the primary.
    /// </summary>
    private async Task<ProxyNode> ResolveLiveNodeAsync(ProxyNode fallback, CancellationToken ct)
    {
        var config = _engine.ActiveConfig;
        var api = _engine.Api;
        if (config?.SelectorTag is null || config.AutoTag is null || api is null) return fallback;

        var selector = await api.GetProxyAsync(config.SelectorTag, ct).ConfigureAwait(false);
        var now = selector?.Now;
        if (now is not null && string.Equals(now, config.AutoTag, StringComparison.Ordinal))
        {
            var group = await api.GetProxyAsync(config.AutoTag, ct).ConfigureAwait(false);
            now = group?.Now;
        }

        return config.NodeFor(now) ?? fallback;
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

            // Once something has failed, look again soon: a minute of dead tunnel is
            // what the interval-times-threshold arithmetic used to cost.
            var interval = failures > 0
                ? _timing.DegradedInterval
                : _timing.MonitorInterval ?? TimeSpan.FromSeconds(Math.Max(5, settings.HealthCheckIntervalSeconds));

            try
            {
                // Sleeps for the interval, or until a nudge says "look now".
                await _wake.WaitAsync(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || _engine.ActiveNode is not { } primary) return;

            // The automatic group may have moved traffic on its own; follow it.
            var node = await ResolveLiveNodeAsync(_liveNode ?? primary, ct).ConfigureAwait(false);
            if (_liveNode is null || node.Id != _liveNode.Id)
            {
                var moved = _liveNode is not null;
                _liveNode = node;
                if (moved)
                {
                    _log($"The core moved traffic to {node.DisplayName}.", false);
                    _exit = null;
                    _ = RefreshExitAfterMoveAsync(node, Current.LatencyMs, settings, ct);
                }
            }

            if (ct.IsCancellationRequested) return;

            // Real traffic is the best evidence there is. Skip the synthetic probe when
            // bytes have been arriving; it cannot false-alarm on a slow probe target.
            if (PassiveHealthy(settings))
            {
                if (failures > 0) _log($"{node.DisplayName} recovered.", false);
                failures = 0;
                Publish(LinkState.Healthy, node, Current.LatencyMs, $"Connected via {node.DisplayName}");
                continue;
            }

            var health = await _probe.CheckAsync(
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

            // First choice: let the running core swap servers. No restart, no proxy
            // flip, no dropped connections for the apps that still work.
            var switched = await TrySwitchToAutoAsync(node, settings, ct).ConfigureAwait(false);
            if (switched is { } s)
            {
                _liveNode = s.Node;
                _quality.RecordSuccess(s.Node.Id);
                _quality.RecordLatency(s.Node.Id, s.Health.LatencyMs);
                Publish(LinkState.Healthy, s.Node, s.Health.LatencyMs, $"Connected via {s.Node.DisplayName}");
                _ = RefreshExitAsync(s.Node, s.Health.LatencyMs, settings, ct);
                failures = 0;
                ResetPassive();
                continue;
            }

            // Hand off to recovery and let this loop end; the new connection starts its own.
            ScheduleRecovery(node);
            return;
        }
    }

    /// <summary>True when enough bytes arrived since the previous tick to count as proof of life.</summary>
    private bool PassiveHealthy(AppSettings settings)
    {
        if (_traffic?.Invoke() is not { } sample) return false;

        if (!_passiveMarkValid)
        {
            _passiveMark = sample.DownloadTotal;
            _passiveMarkValid = true;
            return false;
        }

        // Counters restart at zero with the core; a negative delta is a restart, not proof.
        var delta = sample.DownloadTotal - _passiveMark;
        _passiveMark = sample.DownloadTotal;
        return settings.PassiveHealthMinBytes > 0 && delta >= settings.PassiveHealthMinBytes;
    }

    private void ResetPassive()
    {
        _passiveMarkValid = false;
        _passiveMark = 0;
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

    /// <summary>True while the health monitor is watching a connection.</summary>
    public bool IsMonitoring
    {
        get
        {
            lock (_gate) return _monitorTask is { IsCompleted: false };
        }
    }

    /// <summary>True while a connect-and-retry loop is already running.</summary>
    public bool IsAttempting
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
        int failures = 0,
        FaultKind fault = FaultKind.None)
    {
        Current = new LinkSnapshot(state, node, latency, message, failures)
        {
            Exit = state is LinkState.Healthy or LinkState.Degraded ? _exit : null,
            Fault = fault,
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
