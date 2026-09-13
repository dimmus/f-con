namespace FCon.Abstractions.Model;

/// <summary>Stream transports provisioned by 3x-ui / Xray. Names match Xray's <c>network</c> values.</summary>
public enum TransportKind
{
    /// <summary>Plain TCP. Xray renamed this to "raw" in 25.x; "tcp" remains accepted.</summary>
    Raw,
    /// <summary>mKCP (UDP based).</summary>
    Kcp,
    WebSocket,
    /// <summary>HTTP/2 ("h2"/"http").</summary>
    Http2,
    Quic,
    Grpc,
    /// <summary>HTTPUpgrade — WS handshake without the WS framing overhead.</summary>
    HttpUpgrade,
    /// <summary>XHTTP, the successor to SplitHTTP.</summary>
    XHttp,
}

/// <summary>HTTP header obfuscation for the Raw/TCP transport.</summary>
public enum HeaderObfs { None, Http }

/// <summary>XHTTP operating mode.</summary>
public enum XHttpMode { Auto, PacketUp, StreamUp, StreamOne }

/// <summary>
/// Engine-neutral description of the stream transport underneath a proxy protocol.
/// One instance covers every transport; irrelevant fields stay null.
/// </summary>
public sealed record TransportOptions
{
    public TransportKind Kind { get; init; } = TransportKind.Raw;

    // --- shared by ws / h2 / grpc / httpupgrade / xhttp ---
    /// <summary>Request path, or the gRPC service name when <see cref="Kind"/> is Grpc.</summary>
    public string? Path { get; init; }
    /// <summary>Host header. Comma-separated list for h2, single value elsewhere.</summary>
    public string? Host { get; init; }
    /// <summary>Extra request headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // --- Raw/TCP ---
    public HeaderObfs Obfs { get; init; } = HeaderObfs.None;

    // --- mKCP ---
    public string? KcpSeed { get; init; }
    public string? KcpHeaderType { get; init; }
    public int KcpMtu { get; init; } = 1350;
    public int KcpTti { get; init; } = 50;
    public int KcpUplinkCapacity { get; init; } = 5;
    public int KcpDownlinkCapacity { get; init; } = 20;
    public bool KcpCongestion { get; init; }
    public int KcpReadBufferSize { get; init; } = 2;
    public int KcpWriteBufferSize { get; init; } = 2;

    // --- QUIC ---
    public string? QuicSecurity { get; init; }
    public string? QuicKey { get; init; }
    public string? QuicHeaderType { get; init; }

    // --- gRPC ---
    public bool GrpcMultiMode { get; init; }
    public string? GrpcAuthority { get; init; }
    public int GrpcIdleTimeout { get; init; }
    public int GrpcHealthCheckTimeout { get; init; }

    // --- XHTTP ---
    public XHttpMode XHttpModeValue { get; init; } = XHttpMode.Auto;
    /// <summary>Raw JSON blob merged into the XHTTP <c>extra</c> object.</summary>
    public string? XHttpExtra { get; init; }

    public static TransportOptions Default { get; } = new();
}
