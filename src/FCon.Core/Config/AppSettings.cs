using System.Text.Json.Serialization;
using FCon.Abstractions.Plugins;

namespace FCon.Core.Config;

public enum TrafficMode
{
    /// <summary>Local SOCKS/HTTP listeners plus the Windows system proxy.</summary>
    SystemProxy,
    /// <summary>A TUN adapter capturing everything, including apps that ignore the proxy.</summary>
    Tun,
    /// <summary>Listeners only; nothing is redirected until the user points an app at them.</summary>
    Manual,
}

public enum RoutingMode
{
    /// <summary>Everything except loopback goes through the proxy.</summary>
    Global,
    /// <summary>Apply the routing rule set.</summary>
    Rules,
    /// <summary>Everything direct — the engine still runs, so stats and probes work.</summary>
    Direct,
}

public sealed record AppSettings
{
    public EngineKind Engine { get; set; } = EngineKind.SingBox;
    public TrafficMode TrafficMode { get; set; } = TrafficMode.SystemProxy;
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Rules;

    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    /// <summary>Local API/stats listener. 0 disables it.</summary>
    public int ApiPort { get; set; } = 10810;

    /// <summary>
    /// False until the app has confirmed the listener ports are free. 10808/10809 are the
    /// de-facto defaults across proxy clients, so a machine with another one installed
    /// would otherwise fail to connect on first run with a bind error.
    /// </summary>
    public bool PortsInitialised { get; set; }

    /// <summary>Bind listeners to 0.0.0.0 instead of 127.0.0.1.</summary>
    public bool AllowLan { get; set; }

    public bool EnableSniffing { get; set; } = true;
    /// <summary>Route by the sniffed domain rather than the resolved IP.</summary>
    public bool RouteByDomain { get; set; } = true;

    public string LogLevel { get; set; } = "warning";

    // --- TUN ---
    public string TunInterfaceName { get; set; } = "FCon";
    public int TunMtu { get; set; } = 9000;
    /// <summary>system, gvisor or mixed. gvisor needs no admin driver features.</summary>
    public string TunStack { get; set; } = "mixed";
    public bool TunStrictRoute { get; set; } = true;

    // --- DNS ---
    public string RemoteDns { get; set; } = "https://1.1.1.1/dns-query";
    public string DirectDns { get; set; } = "https://223.5.5.5/dns-query";
    public string BootstrapDns { get; set; } = "1.1.1.1";
    /// <summary>Answer A/AAAA from a synthetic range so routing can act on domains under TUN.</summary>
    public bool FakeIp { get; set; }

    // --- behaviour ---
    public bool AutoStartLastServer { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutoUpdateSubscriptions { get; set; } = true;
    public int SubscriptionUpdateHours { get; set; } = 12;

    /// <summary>URL used to measure real end-to-end latency through the tunnel.</summary>
    public string LatencyTestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int LatencyTimeoutMs { get; set; } = 5000;
    public int LatencyConcurrency { get; set; } = 16;

    // --- health and resilience ---

    /// <summary>
    /// Confirm a connection carries real traffic before reporting success. Without this a
    /// dead server still looks connected, because the core binds its port either way.
    /// </summary>
    public bool VerifyOnConnect { get; set; } = true;

    /// <summary>Keep probing while connected so a tunnel that dies is noticed.</summary>
    public bool ContinuousHealthCheck { get; set; } = true;

    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>Consecutive failed probes before the connection is treated as broken.</summary>
    public int UnhealthyThreshold { get; set; } = 2;

    /// <summary>
    /// Reconnect on a dropped or unhealthy link. Retrying is unbounded: it continues,
    /// cycling servers and backing off between rounds, until the user disconnects.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Move to the next best server when the current one cannot be recovered.</summary>
    public bool AutoFailover { get; set; } = true;

    /// <summary>Connect to the best-ranked server rather than the last used one.</summary>
    public bool PreferBestServer { get; set; }

    /// <summary>URL used for throughput measurement. Must serve a few MB.</summary>
    public string SpeedTestUrl { get; set; } = "https://speed.cloudflare.com/__down?bytes=10000000";

    public Guid? ActiveNodeId { get; set; }
    public string Theme { get; set; } = "system";

    [JsonIgnore]
    public string ListenAddress => AllowLan ? "0.0.0.0" : "127.0.0.1";
}
