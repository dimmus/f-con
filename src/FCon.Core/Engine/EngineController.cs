using System.Diagnostics;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;
using FCon.Core.Config;
using FCon.Core.Net;
using FCon.Core.Plugins;

namespace FCon.Core.Engine;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted,
}

public sealed record EngineLogLine(DateTimeOffset Timestamp, string Text, bool IsError);

/// <summary>
/// Why a connect attempt faulted. The supervisor retries and fails over on a server
/// fault; the others are about this machine and no other server will fix them.
/// </summary>
public enum FaultKind
{
    None,
    Server,
    CoreMissing,
    CoreTooOld,
    PortConflict,
}

public sealed record ConnectionStatus(
    ConnectionState State,
    ProxyNode? Node,
    string? Message,
    IReadOnlyList<string> Warnings)
{
    public FaultKind Fault { get; init; }

    /// <summary>True when trying another server cannot help.</summary>
    public bool IsEnvironmentFault => Fault is FaultKind.CoreMissing or FaultKind.CoreTooOld or FaultKind.PortConflict;
}

/// <summary>
/// What the supervisor needs from the thing that runs the core. Pulled behind an
/// interface so the connect/verify/recover state machine can be tested without
/// starting a process.
/// </summary>
public interface IEngineController
{
    ConnectionState State { get; }

    /// <summary>The server the config was built around.</summary>
    ProxyNode? ActiveNode { get; }

    /// <summary>The config the running core was started with, including its group tags.</summary>
    GeneratedConfig? ActiveConfig { get; }

    /// <summary>Control API of the running core, when it has one (sing-box with an API port).</summary>
    IClashApi? Api { get; }

    event Action<ConnectionStatus>? StatusChanged;

    /// <param name="autoSelect">
    /// Start on the automatic group rather than on <paramref name="node"/>: the core
    /// picks the fastest healthy server from the pool from the first packet.
    /// </param>
    Task<ConnectionStatus> ConnectAsync(ProxyNode node, bool autoSelect = false, CancellationToken ct = default);

    Task DisconnectAsync();
}

/// <summary>
/// Owns the engine child process and the system-wide plumbing that goes with it:
/// generate config, start the core, wait for it to be listening, flip the system proxy.
/// Stopping always undoes those in reverse, including when the core dies on its own.
/// </summary>
public sealed class EngineController : IEngineController, IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly Func<AppSettings> _settings;
    private readonly Func<RoutingProfile> _routing;
    private readonly Func<IReadOnlyList<ProxyNode>> _candidates;
    private readonly Lock _gate = new();

    private Process? _process;
    private ProxyNode? _node;
    private GeneratedConfig? _config;
    private ClashApiClient? _api;
    private bool _systemProxyApplied;
    private CancellationTokenSource? _lifetime;

    /// <summary>
    /// Tail of the core's own output. When the core dies, "exit code 1" on its own is
    /// useless — the reason it printed just before quitting is what the user needs.
    /// </summary>
    private readonly Queue<string> _recentOutput = new();

    /// <summary>
    /// Every core we start joins this job, so the kernel takes them down with us even
    /// when we are killed outright and no cleanup code of ours can run.
    /// </summary>
    private readonly ProcessJob _job = new();

    /// <summary>
    /// Describing an engine spawns "<c>core version</c>". The preview pane and the
    /// settings page ask often, so the answer is kept for a short while.
    /// </summary>
    private (EngineKind Kind, EngineInfo? Info, DateTime At)? _describeCache;

    /// <param name="candidates">
    /// Servers eligible for the failover group, best first. The connected server is
    /// always first in the pool; the rest come from here, up to the configured size.
    /// </param>
    public EngineController(
        PluginRegistry registry,
        Func<AppSettings> settings,
        Func<RoutingProfile> routing,
        Func<IReadOnlyList<ProxyNode>>? candidates = null)
    {
        _registry = registry;
        _settings = settings;
        _routing = routing;
        _candidates = candidates ?? (() => []);
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ProxyNode? ActiveNode => _node;
    public GeneratedConfig? ActiveConfig => _config;
    public IClashApi? Api => _api;

    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<EngineLogLine>? LogReceived;

    // ------------------------------------------------------------------ start

    public async Task<ConnectionStatus> ConnectAsync(ProxyNode node, bool autoSelect = false, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var settings = _settings();
        SetState(ConnectionState.Connecting, node, "Preparing configuration...", []);

        var engine = Describe(settings.Engine);
        if (engine is null)
        {
            var exe = EngineLocator.ExecutableName(settings.Engine);
            var folder = Path.Combine(AppPaths.EnginesDirectory, EngineLocator.DirectoryName(settings.Engine));

            // The status card is narrow, so keep the headline short and put the path in the log.
            Log($"{exe} was not found. Expected it in {folder} or on PATH.", isError: true);
            return Fault(node, $"{exe} is not installed. Open Settings to get it.", FaultKind.CoreMissing);
        }

        if (DescribeTooOld(engine) is { } tooOld) return Fault(node, tooOld, FaultKind.CoreTooOld);

        GeneratedConfig config;
        try
        {
            config = Generate(node, settings, autoSelect, engine.Version);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return Fault(node, ex.Message);
        }

        // Check the listeners up front. Otherwise the core dies on a bare Winsock bind
        // error that says nothing about which program already owns the port.
        if (DescribePortConflict(settings) is { } conflict) return Fault(node, conflict, FaultKind.PortConflict);

        AppPaths.EnsureCreated();
        var configPath = AppPaths.GeneratedConfigFile(EngineLocator.DirectoryName(settings.Engine));
        await File.WriteAllTextAsync(configPath, config.ToJson(), ct).ConfigureAwait(false);

        try
        {
            StartProcess(engine, configPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Fault(node, $"Could not start {engine.FileName}: {ex.Message}");
        }

        // Warnings travel on the Connected status only; carrying them here as well
        // made every one of them appear twice in the log per connect.
        SetState(ConnectionState.Connecting, node, "Waiting for the local listener...", []);

        var ready = await WaitForListenerAsync(settings.SocksPort, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);

        if (!ready)
        {
            var tail = CoreOutputTail();
            var reason = _process is { HasExited: true }
                ? $"{engine.FileName} exited immediately (code {_process.ExitCode})."
                : $"{engine.FileName} did not open port {settings.SocksPort} in time.";
            if (tail is not null) reason += Environment.NewLine + tail;

            await StopProcessAsync().ConfigureAwait(false);
            return Fault(node, reason);
        }

        if (settings.TrafficMode == TrafficMode.SystemProxy)
        {
            try
            {
                SystemProxy.Enable(settings.HttpPort);
                _systemProxyApplied = true;
            }
            catch (Exception ex)
            {
                // The tunnel is already up. Failing to point Windows at it is a degradation,
                // not a reason to tear down a working connection, so report and carry on.
                Log($"Could not set the system proxy: {ex.Message}", isError: true);
                Log($"Point applications at 127.0.0.1:{settings.HttpPort} manually, "
                    + "or switch Traffic capture to Manual in Settings.", isError: true);
            }
        }

        lock (_gate)
        {
            _node = node;
            _config = config;
            _api = settings is { Engine: EngineKind.SingBox, ApiPort: > 0 }
                ? new ClashApiClient(settings.ApiPort, settings.ApiSecret)
                : null;
        }

        SetState(ConnectionState.Connected, node, $"Connected via {engine.FileName}", config.Warnings);
        return CurrentStatus(config.Warnings);
    }

    /// <summary>Generate the config for a node without starting anything — used by the preview pane.</summary>
    public GeneratedConfig Generate(ProxyNode node, AppSettings? settings = null, bool autoSelect = false)
    {
        settings ??= _settings();
        return Generate(node, settings, autoSelect, Describe(settings.Engine)?.Version);
    }

    private GeneratedConfig Generate(ProxyNode node, AppSettings settings, bool autoSelect, string? engineVersion)
    {
        var routing = _routing();
        var pool = BuildPool(node, settings);

        return settings.Engine == EngineKind.Xray
            ? new XrayConfigBuilder(_registry).Build(node, pool, settings, routing, engineVersion, autoSelect)
            : new SingBoxConfigBuilder(_registry).Build(node, pool, settings, routing, engineVersion, autoSelect);
    }

    /// <summary>
    /// The chosen server plus the best of the rest, capped. Members the engine cannot run
    /// or that fail validation stay out; the group must never be the reason a start fails.
    /// </summary>
    private List<ProxyNode> BuildPool(ProxyNode node, AppSettings settings)
    {
        var pool = new List<ProxyNode> { node };
        if (!settings.InCoreFailover) return pool;

        var max = Math.Max(1, settings.FailoverPoolSize);
        foreach (var candidate in _candidates())
        {
            if (pool.Count >= max) break;
            if (candidate.Id == node.Id) continue;

            var plugin = _registry.ById(candidate.Protocol);
            if (plugin is null || !PoolEmitter.Supports(plugin.Descriptor.Engines, settings.Engine)) continue;
            if (plugin.Validate(candidate).Count > 0) continue;

            pool.Add(candidate);
        }
        return pool;
    }

    private EngineInfo? Describe(EngineKind kind)
    {
        lock (_gate)
        {
            if (_describeCache is { } cached && cached.Kind == kind
                && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30))
            {
                return cached.Info;
            }
        }

        var info = EngineLocator.Describe(kind);
        lock (_gate) _describeCache = (kind, info, DateTime.UtcNow);
        return info;
    }

    /// <summary>The emitted schema has a floor; an older core fails with an unhelpful parse error.</summary>
    private static string? DescribeTooOld(EngineInfo engine)
    {
        var version = EngineVersionKit.Parse(engine.Version);
        if (version is null) return null;

        if (engine.Kind == EngineKind.SingBox && version < SingBoxConfigBuilder.MinimumVersion)
        {
            return $"sing-box {version} is too old; {SingBoxConfigBuilder.MinimumVersion} or newer is required. "
                   + "Update it from Settings.";
        }
        return null;
    }

    private void StartProcess(EngineInfo engine, string configPath)
    {
        var arguments = engine.Kind == EngineKind.Xray
            ? $"run -c \"{configPath}\""
            : $"run -c \"{configPath}\" -D \"{AppPaths.RuntimeDirectory}\"";

        var startInfo = new ProcessStartInfo(engine.ExecutablePath, arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(engine.ExecutablePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // Both cores read geo assets relative to this variable rather than the working directory.
        startInfo.Environment["XRAY_LOCATION_ASSET"] = AppPaths.AssetsDirectory;

        // sing-box 1.14 deprecated the implicit HTTP client that downloads remote rule-sets
        // and made it fatal in 1.15. Only configs with geosite:/geoip: rules need it, and
        // those emit a warning of their own; the flag is removed in 1.16, where setting it
        // would be noise at best.
        if (engine.Kind == EngineKind.SingBox
            && !EngineVersionKit.AtLeast(EngineVersionKit.Parse(engine.Version), 1, 16))
        {
            startInfo.Environment["ENABLE_DEPRECATED_IMPLICIT_DEFAULT_HTTP_CLIENT"] = "true";
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var text = StripAnsi(e.Data);
            RecordCoreOutput(text);
            Log(text, false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var text = StripAnsi(e.Data);
            RecordCoreOutput(text);
            Log(text, isError: !IsRoutineCoreChatter(text));
        };
        process.Exited += OnProcessExited;

        lock (_gate) _recentOutput.Clear();
        process.Start();

        // Bind the core to our lifetime before anything else, so a crash between here
        // and the first health check cannot strand it.
        if (!_job.Assign(process))
        {
            Log("Could not bind the proxy core to this app's lifetime; if FCon is killed "
                + "rather than closed, the core may keep running.", isError: true);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            _process = process;
            _lifetime = new CancellationTokenSource();
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Only meaningful when we did not ask for it; DisconnectAsync detaches first.
        if (State is ConnectionState.Disconnecting or ConnectionState.Disconnected) return;

        var code = sender is Process p ? p.ExitCode : -1;
        RestoreSystemProxy();

        var reason = CoreOutputTail();
        var message = reason is null
            ? $"The proxy core stopped unexpectedly (exit code {code})."
            : $"The proxy core stopped (exit code {code}):{Environment.NewLine}{reason}";

        SetState(ConnectionState.Faulted, _node, message, []);
    }

    // ------------------------------------------------------------------- stop

    public async Task DisconnectAsync()
    {
        if (State == ConnectionState.Disconnected && _process is null) return;

        SetState(ConnectionState.Disconnecting, _node, "Stopping...", []);
        RestoreSystemProxy();
        await StopProcessAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _node = null;
            _config = null;
        }
        SetState(ConnectionState.Disconnected, null, null, []);
    }

    private async Task StopProcessAsync()
    {
        Process? process;
        CancellationTokenSource? lifetime;
        ClashApiClient? api;
        lock (_gate)
        {
            process = _process;
            lifetime = _lifetime;
            api = _api;
            _process = null;
            _lifetime = null;
            _api = null;
        }

        api?.Dispose();

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            lifetime.Dispose();
        }

        if (process is null) return;

        process.Exited -= OnProcessExited;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                       or System.ComponentModel.Win32Exception)
        {
            // The process is gone or unkillable; either way there is nothing left to wait for.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void RestoreSystemProxy()
    {
        if (!_systemProxyApplied) return;
        _systemProxyApplied = false;
        try
        {
            SystemProxy.Disable();
        }
        catch (Exception ex)
        {
            // Never let cleanup throw: this runs on the shutdown and fault paths.
            Log($"Could not restore the system proxy: {ex.Message}", isError: true);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Returns a message naming the program that already holds one of our listener ports,
    /// or null when both are free. Other proxy clients default to the same 10808/10809
    /// pair, so this collision is common rather than exotic.
    /// </summary>
    private static string? DescribePortConflict(AppSettings settings)
    {
        foreach (var (port, label) in new[]
                 {
                     (settings.SocksPort, "SOCKS"),
                     (settings.HttpPort, "HTTP"),
                 })
        {
            if (port <= 0 || !PortProbe.IsListening(port)) continue;

            var holder = PortProbe.DescribeListener(port);
            return holder is null
                ? $"{label} port {port} is already in use. Change it in Settings, or use Pick free ports."
                : $"{label} port {port} is already in use by {holder}. "
                  + "Close that program, or use Pick free ports in Settings.";
        }

        return null;
    }

    /// <summary>Poll the local port rather than parsing log output, which differs per core.</summary>
    private static async Task<bool> WaitForListenerAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (PortProbe.IsListening(port)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    private ConnectionStatus Fault(ProxyNode node, string message, FaultKind kind = FaultKind.Server)
    {
        SetState(ConnectionState.Faulted, node, message, []);
        return CurrentStatus([]) with { Fault = kind };
    }

    private ConnectionStatus CurrentStatus(IReadOnlyList<string> warnings) =>
        new(State, _node, _lastMessage, warnings);

    private string? _lastMessage;

    private void SetState(
        ConnectionState state,
        ProxyNode? node,
        string? message,
        IReadOnlyList<string> warnings)
    {
        State = state;
        _lastMessage = message;
        StatusChanged?.Invoke(new ConnectionStatus(state, node, message, warnings));
    }

    private void Log(string text, bool isError)
    {
        LogReceived?.Invoke(new EngineLogLine(DateTimeOffset.Now, text, isError));
    }

    /// <summary>
    /// The cores colour their output for a terminal. Left alone, the escape sequences
    /// show up verbatim in the log pane and in error dialogs.
    /// </summary>
    private const char EscapeChar = (char)27;

    private static readonly System.Text.RegularExpressions.Regex AnsiPattern =
        new(@"\x1b\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripAnsi(string text) =>
        text.Contains(EscapeChar) ? AnsiPattern.Replace(text, "") : text;

    /// <summary>
    /// sing-box reports every connection that ends with an error at ERROR level -
    /// resets, idle closes, "force closed via ClientConn.Close". They are the normal
    /// life of a tunnel, not faults, and painting them red buries the lines that are.
    /// </summary>
    internal static bool IsRoutineCoreChatter(string text) =>
        text.Contains("connection: connection ", StringComparison.Ordinal)
        && text.Contains(" closed", StringComparison.Ordinal);

    /// <summary>Record a line the core itself printed, keeping only the recent tail.</summary>
    private void RecordCoreOutput(string text)
    {
        lock (_gate)
        {
            _recentOutput.Enqueue(text);
            while (_recentOutput.Count > 25) _recentOutput.Dequeue();
        }
    }

    /// <summary>
    /// The most telling lines the core printed. Prefers lines that look like errors,
    /// falling back to whatever it said last.
    /// </summary>
    private string? CoreOutputTail(int max = 3)
    {
        string[] lines;
        lock (_gate) lines = [.. _recentOutput];

        if (lines.Length == 0) return null;

        var interesting = lines
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var chosen = (interesting.Length > 0 ? interesting : lines).TakeLast(max);
        return string.Join(Environment.NewLine, chosen);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _job.Dispose();
        GC.SuppressFinalize(this);
    }
}
