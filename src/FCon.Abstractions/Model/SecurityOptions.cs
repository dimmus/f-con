namespace FCon.Abstractions.Model;

public enum SecurityKind
{
    None,
    Tls,
    /// <summary>REALITY — Xray's TLS-camouflage layer.</summary>
    Reality,
}

/// <summary>TLS / REALITY parameters, engine-neutral.</summary>
public sealed record SecurityOptions
{
    public SecurityKind Kind { get; init; } = SecurityKind.None;

    /// <summary>SNI / server name sent in the ClientHello.</summary>
    public string? ServerName { get; init; }
    /// <summary>uTLS ClientHello fingerprint: chrome, firefox, safari, ios, android, edge, random, randomized.</summary>
    public string? Fingerprint { get; init; }
    public IReadOnlyList<string> Alpn { get; init; } = [];
    public bool AllowInsecure { get; init; }

    // --- REALITY ---
    /// <summary>Server's x25519 public key.</summary>
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    /// <summary>SpiderX crawl path.</summary>
    public string? SpiderX { get; init; }

    // --- plain TLS extras ---
    public bool DisableSystemRoot { get; init; }
    public bool EnableSessionResumption { get; init; }
    /// <summary>PEM certificate pinned by the user, if any.</summary>
    public string? PinnedPeerCertificate { get; init; }

    public static SecurityOptions None { get; } = new();
}

/// <summary>Multiplexing settings. Xray uses "mux", sing-box "multiplex".</summary>
public sealed record MuxOptions
{
    public bool Enabled { get; init; }
    public int Concurrency { get; init; } = 8;
    /// <summary>Xray xudp concurrency (-1 disables, 0 = reject).</summary>
    public int XudpConcurrency { get; init; } = 16;
    public string XudpProxyUdp443 { get; init; } = "reject";
    /// <summary>sing-box protocol: smux, yamux, h2mux.</summary>
    public string Protocol { get; init; } = "smux";
    public bool Padding { get; init; }

    public static MuxOptions Disabled { get; } = new();
}
