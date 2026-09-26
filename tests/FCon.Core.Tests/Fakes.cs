using FCon.Abstractions.Model;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Net;

namespace FCon.Core.Tests;

/// <summary>Scripted stand-in for the core: never starts a process, records every call.</summary>
internal sealed class FakeEngine : IEngineController
{
    private readonly Lock _gate = new();

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ProxyNode? ActiveNode { get; private set; }
    public GeneratedConfig? ActiveConfig { get; set; }
    public IClashApi? Api { get; set; }

    public event Action<ConnectionStatus>? StatusChanged;

    public List<(ProxyNode Node, bool AutoSelect)> Connects { get; } = [];
    public int Disconnects { get; private set; }

    /// <summary>Decides the outcome of each connect; null means success.</summary>
    public Func<ProxyNode, bool, ConnectionStatus?>? OnConnect { get; set; }

    public Task<ConnectionStatus> ConnectAsync(ProxyNode node, bool autoSelect = false, CancellationToken ct = default)
    {
        ConnectionStatus status;
        lock (_gate)
        {
            Connects.Add((node, autoSelect));
            status = OnConnect?.Invoke(node, autoSelect)
                     ?? new ConnectionStatus(ConnectionState.Connected, node, "ok", []);
            State = status.State;
            ActiveNode = status.State == ConnectionState.Connected ? node : null;
        }
        StatusChanged?.Invoke(status);
        return Task.FromResult(status);
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            Disconnects++;
            State = ConnectionState.Disconnected;
            ActiveNode = null;
        }
        StatusChanged?.Invoke(new ConnectionStatus(ConnectionState.Disconnected, null, null, []));
        return Task.CompletedTask;
    }

    /// <summary>Simulate the core dying on its own.</summary>
    public void RaiseFault(string message)
    {
        ProxyNode? node;
        lock (_gate)
        {
            node = ActiveNode;
            State = ConnectionState.Faulted;
        }
        StatusChanged?.Invoke(new ConnectionStatus(ConnectionState.Faulted, node, message, []));
    }

    public int ConnectCount
    {
        get { lock (_gate) return Connects.Count; }
    }
}

/// <summary>Answers health checks from a script; the last entry repeats forever.</summary>
internal sealed class FakeProbe : IHealthProbe
{
    private readonly Queue<HealthResult> _script = new();
    private HealthResult _last = Healthy(50);
    private readonly Lock _gate = new();

    public int Calls { get; private set; }
    public ExitInfo? Exit { get; set; }

    public static HealthResult Healthy(int ms) => new(HealthVerdict.Healthy, ms, null);
    public static HealthResult Down() => new(HealthVerdict.TimedOut, 5000, "Timed out.");

    public FakeProbe Then(params HealthResult[] results)
    {
        lock (_gate)
        {
            foreach (var r in results) _script.Enqueue(r);
        }
        return this;
    }

    public Task<HealthResult> CheckAsync(int httpPort, string url, int timeoutMs, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls++;
            if (_script.Count > 0) _last = _script.Dequeue();
            return Task.FromResult(_last);
        }
    }

    public Task<ExitInfo?> LookupExitAsync(int httpPort, CancellationToken ct) => Task.FromResult(Exit);
}

/// <summary>In-memory selector/urltest state, the way sing-box's API would report it.</summary>
internal sealed class FakeApi : IClashApi
{
    private readonly Lock _gate = new();

    public Dictionary<string, string?> Now { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int?> Delays { get; } = new(StringComparer.Ordinal);
    public List<(string Selector, string Tag)> Selections { get; } = [];

    /// <summary>What the automatic group reports once the selector points at it.</summary>
    public string? AutoPick { get; set; }

    public Task<ProxyStatus?> GetProxyAsync(string tag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Now.TryGetValue(tag, out var now);
            return Task.FromResult<ProxyStatus?>(new ProxyStatus(tag, "Selector", now, [], null));
        }
    }

    public Task<bool> SelectAsync(string selector, string tag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Selections.Add((selector, tag));
            Now[selector] = tag;
            if (AutoPick is not null) Now[tag] = AutoPick;
        }
        return Task.FromResult(true);
    }

    public Task<int?> DelayAsync(string tag, string url, int timeoutMs, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Delays.TryGetValue(tag, out var d) ? d : null);
    }

    public Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.FromResult<ConnectionsSnapshot?>(null);
}

internal static class TestKit
{
    public static ProxyNode Node(string name, string protocol = "vless") => new()
    {
        Protocol = protocol,
        Remark = name,
        Server = $"{name.ToLowerInvariant()}.example.com",
        Port = 443,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = Guid.NewGuid().ToString(),
        },
        Security = new SecurityOptions { Kind = SecurityKind.Tls, ServerName = $"{name.ToLowerInvariant()}.example.com" },
    };

    public static AppSettings Settings() => new()
    {
        VerifyOnConnect = true,
        ContinuousHealthCheck = true,
        AutoReconnect = true,
        AutoFailover = true,
        InCoreFailover = true,
        UnhealthyThreshold = 2,
        PassiveHealthMinBytes = 256 * 1024,
        ApiSecret = "test",
    };

    public static SupervisorTiming FastTiming() => new()
    {
        MonitorInterval = TimeSpan.FromMilliseconds(40),
        DegradedInterval = TimeSpan.FromMilliseconds(20),
        SwitchSettle = TimeSpan.FromMilliseconds(10),
        RetryDelay = _ => TimeSpan.FromMilliseconds(40),
    };

    public static QualityStore TempQuality() =>
        new(Path.Combine(Path.GetTempPath(), $"fcon-tests-{Guid.NewGuid():N}.json"));

    /// <summary>A grouped sing-box config as the engine would report it, without building JSON.</summary>
    public static GeneratedConfig GroupedConfig(params ProxyNode[] nodes) => new(new System.Text.Json.Nodes.JsonObject(), [])
    {
        Members = [.. nodes.Select(n => new PoolMember(n, "node-" + n.Remark.ToLowerInvariant()))],
        PrimaryTag = "node-" + nodes[0].Remark.ToLowerInvariant(),
        SelectorTag = "proxy",
        AutoTag = "auto",
    };

    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }
}

/// <summary>Collects every snapshot the supervisor publishes, safely across threads.</summary>
internal sealed class SnapshotLog
{
    private readonly List<LinkSnapshot> _items = [];
    private readonly Lock _gate = new();

    public void Add(LinkSnapshot s)
    {
        lock (_gate) _items.Add(s);
    }

    public IReadOnlyList<LinkSnapshot> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public bool Any(Func<LinkSnapshot, bool> pred) => Items.Any(pred);
    public LinkSnapshot Last => Items[^1];
}
