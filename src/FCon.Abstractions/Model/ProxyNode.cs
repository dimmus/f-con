using System.Text.Json.Serialization;

namespace FCon.Abstractions.Model;

/// <summary>
/// The canonical, engine-neutral description of one outbound server.
/// Protocol-specific credentials live in <see cref="Settings"/>, keyed by the
/// <c>Key</c> values a plugin declares in its <see cref="Plugins.ProtocolDescriptor"/>.
/// </summary>
public sealed record ProxyNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Plugin id that owns this node: "vless", "vmess", "trojan", ...</summary>
    public required string Protocol { get; init; }

    public string Remark { get; init; } = "";
    public required string Server { get; init; }
    public required int Port { get; init; }

    /// <summary>Protocol-specific fields (uuid, password, method, flow, alterId, ...).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TransportOptions Transport { get; init; } = TransportOptions.Default;
    public SecurityOptions Security { get; init; } = SecurityOptions.None;
    public MuxOptions Mux { get; init; } = MuxOptions.Disabled;

    /// <summary>Id of the subscription this node came from; null for hand-entered nodes.</summary>
    public Guid? SubscriptionId { get; init; }

    /// <summary>Original share link, kept verbatim for round-tripping and diagnostics.</summary>
    public string? SourceLink { get; init; }

    /// <summary>Free-form user grouping.</summary>
    public string? Group { get; init; }

    /// <summary>Last measured handshake latency in ms; null when never probed, -1 when unreachable.</summary>
    public int? LatencyMs { get; init; }

    [JsonIgnore]
    public string Endpoint => Server.Contains(':') && !Server.StartsWith('[')
        ? $"[{Server}]:{Port}"
        : $"{Server}:{Port}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? Endpoint : Remark;

    public string? Get(string key) =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public string GetOr(string key, string fallback) => Get(key) ?? fallback;
}
