using System.Text.Json.Nodes;
using FCon.Abstractions.Model;

namespace FCon.Abstractions.Emit;

/// <summary>
/// Translates <see cref="TransportOptions"/> into Xray <c>streamSettings</c> or a
/// sing-box <c>transport</c> object. Shared by every plugin so the transport matrix
/// is implemented exactly once.
/// </summary>
public static class TransportEmitter
{
    public static string XrayNetworkName(TransportKind kind) => kind switch
    {
        TransportKind.Raw => "tcp",
        TransportKind.Kcp => "kcp",
        TransportKind.WebSocket => "ws",
        TransportKind.Http2 => "http",
        TransportKind.Quic => "quic",
        TransportKind.Grpc => "grpc",
        TransportKind.HttpUpgrade => "httpupgrade",
        TransportKind.XHttp => "xhttp",
        _ => "tcp",
    };

    public static TransportKind ParseNetwork(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "kcp" or "mkcp" => TransportKind.Kcp,
        "ws" or "websocket" => TransportKind.WebSocket,
        "h2" or "http" or "http2" => TransportKind.Http2,
        "quic" => TransportKind.Quic,
        "grpc" or "gun" => TransportKind.Grpc,
        "httpupgrade" => TransportKind.HttpUpgrade,
        "xhttp" or "splithttp" => TransportKind.XHttp,
        _ => TransportKind.Raw,
    };

    // ---------------------------------------------------------------- Xray

    /// <summary>Populates <c>streamSettings</c> on an Xray outbound, creating it if absent.</summary>
    public static void ApplyXray(JsonObject outbound, TransportOptions t)
    {
        var ss = outbound["streamSettings"] as JsonObject ?? new JsonObject();
        outbound["streamSettings"] = ss;
        ss["network"] = XrayNetworkName(t.Kind);

        switch (t.Kind)
        {
            case TransportKind.Raw:
                if (t.Obfs == HeaderObfs.Http)
                {
                    var request = new JsonObject
                    {
                        ["version"] = "1.1",
                        ["method"] = "GET",
                        ["path"] = new JsonArray(SplitPaths(t.Path).Select(p => (JsonNode)p!).ToArray()),
                        ["headers"] = new JsonObject
                        {
                            ["Host"] = new JsonArray(SplitHosts(t.Host).Select(h => (JsonNode)h!).ToArray()),
                            ["User-Agent"] = new JsonArray(DefaultUserAgents().Select(u => (JsonNode)u!).ToArray()),
                            ["Accept-Encoding"] = new JsonArray("gzip, deflate"),
                            ["Connection"] = new JsonArray("keep-alive"),
                            ["Pragma"] = "no-cache",
                        },
                    };
                    ss["tcpSettings"] = new JsonObject
                    {
                        ["header"] = new JsonObject { ["type"] = "http", ["request"] = request },
                    };
                }
                else
                {
                    ss["tcpSettings"] = new JsonObject
                    {
                        ["header"] = new JsonObject { ["type"] = "none" },
                    };
                }
                break;

            case TransportKind.Kcp:
                var kcp = new JsonObject
                {
                    ["mtu"] = t.KcpMtu,
                    ["tti"] = t.KcpTti,
                    ["uplinkCapacity"] = t.KcpUplinkCapacity,
                    ["downlinkCapacity"] = t.KcpDownlinkCapacity,
                    ["congestion"] = t.KcpCongestion,
                    ["readBufferSize"] = t.KcpReadBufferSize,
                    ["writeBufferSize"] = t.KcpWriteBufferSize,
                    ["header"] = new JsonObject { ["type"] = t.KcpHeaderType ?? "none" },
                };
                kcp.SetIf("seed", t.KcpSeed);
                ss["kcpSettings"] = kcp;
                break;

            case TransportKind.WebSocket:
                var ws = new JsonObject();
                ws.SetIf("path", t.Path);
                ws.SetIf("host", t.Host);
                if (t.Headers.Count > 0) ws["headers"] = HeadersObject(t.Headers);
                ss["wsSettings"] = ws;
                break;

            case TransportKind.Http2:
                var h2 = new JsonObject
                {
                    ["host"] = new JsonArray(SplitHosts(t.Host).Select(h => (JsonNode)h!).ToArray()),
                    ["path"] = string.IsNullOrWhiteSpace(t.Path) ? "/" : t.Path,
                };
                if (t.Headers.Count > 0) h2["headers"] = HeadersObject(t.Headers);
                ss["httpSettings"] = h2;
                break;

            case TransportKind.Quic:
                ss["quicSettings"] = new JsonObject
                {
                    ["security"] = t.QuicSecurity ?? "none",
                    ["key"] = t.QuicKey ?? "",
                    ["header"] = new JsonObject { ["type"] = t.QuicHeaderType ?? "none" },
                };
                break;

            case TransportKind.Grpc:
                var grpc = new JsonObject { ["serviceName"] = t.Path ?? "" };
                grpc.SetIf("multiMode", t.GrpcMultiMode);
                grpc.SetIf("authority", t.GrpcAuthority);
                grpc.SetIfPositive("idle_timeout", t.GrpcIdleTimeout);
                grpc.SetIfPositive("health_check_timeout", t.GrpcHealthCheckTimeout);
                ss["grpcSettings"] = grpc;
                break;

            case TransportKind.HttpUpgrade:
                var hu = new JsonObject();
                hu.SetIf("path", t.Path);
                hu.SetIf("host", t.Host);
                if (t.Headers.Count > 0) hu["headers"] = HeadersObject(t.Headers);
                ss["httpupgradeSettings"] = hu;
                break;

            case TransportKind.XHttp:
                var xh = new JsonObject();
                xh.SetIf("path", t.Path);
                xh.SetIf("host", t.Host);
                xh["mode"] = XHttpModeName(t.XHttpModeValue);
                if (t.Headers.Count > 0) xh["headers"] = HeadersObject(t.Headers);
                if (JsonKit.TryParseObject(t.XHttpExtra) is { } extra) xh["extra"] = extra;
                ss["xhttpSettings"] = xh;
                break;
        }
    }

    public static string XHttpModeName(XHttpMode mode) => mode switch
    {
        XHttpMode.PacketUp => "packet-up",
        XHttpMode.StreamUp => "stream-up",
        XHttpMode.StreamOne => "stream-one",
        _ => "auto",
    };

    public static XHttpMode ParseXHttpMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "packet-up" => XHttpMode.PacketUp,
        "stream-up" => XHttpMode.StreamUp,
        "stream-one" => XHttpMode.StreamOne,
        _ => XHttpMode.Auto,
    };

    // ------------------------------------------------------------ sing-box

    /// <summary>
    /// Populates the sing-box <c>transport</c> object. Returns a warning when the
    /// transport has no sing-box equivalent (mKCP, XHTTP, raw HTTP obfuscation).
    /// </summary>
    public static string? ApplySingBox(JsonObject outbound, TransportOptions t)
    {
        switch (t.Kind)
        {
            case TransportKind.Raw:
                return t.Obfs == HeaderObfs.Http
                    ? "sing-box has no equivalent of the raw/TCP HTTP header obfuscation; it was dropped."
                    : null;

            case TransportKind.Kcp:
                return "sing-box does not implement mKCP. Switch this server to the Xray engine.";

            case TransportKind.XHttp:
                return "sing-box does not implement XHTTP. Switch this server to the Xray engine.";

            case TransportKind.WebSocket:
                {
                    var (path, earlyData) = SplitEarlyData(t.Path);
                    var ws = new JsonObject { ["type"] = "ws" };
                    ws.SetIf("path", path);
                    var headers = HeadersObject(t.Headers);
                    if (!string.IsNullOrWhiteSpace(t.Host)) headers["Host"] = t.Host;
                    if (headers.Count > 0) ws["headers"] = headers;
                    if (earlyData > 0)
                    {
                        ws["max_early_data"] = earlyData;
                        ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                    }
                    outbound["transport"] = ws;
                    return null;
                }

            case TransportKind.Http2:
                {
                    var h2 = new JsonObject { ["type"] = "http" };
                    var hosts = SplitHosts(t.Host);
                    if (hosts.Length > 0) h2["host"] = new JsonArray(hosts.Select(h => (JsonNode)h!).ToArray());
                    h2["path"] = string.IsNullOrWhiteSpace(t.Path) ? "/" : t.Path;
                    if (t.Headers.Count > 0) h2["headers"] = HeadersObject(t.Headers);
                    outbound["transport"] = h2;
                    return null;
                }

            case TransportKind.Quic:
                outbound["transport"] = new JsonObject { ["type"] = "quic" };
                return t.QuicSecurity is { Length: > 0 } and not "none"
                    ? "The sing-box QUIC transport has no per-stream obfuscation key; quicSecurity was dropped."
                    : null;

            case TransportKind.Grpc:
                {
                    var grpc = new JsonObject { ["type"] = "grpc" };
                    grpc.SetIf("service_name", t.Path);
                    grpc.SetIfPositive("idle_timeout", t.GrpcIdleTimeout);
                    grpc.SetIfPositive("ping_timeout", t.GrpcHealthCheckTimeout);
                    outbound["transport"] = grpc;
                    return null;
                }

            case TransportKind.HttpUpgrade:
                {
                    var hu = new JsonObject { ["type"] = "httpupgrade" };
                    hu.SetIf("host", t.Host);
                    hu.SetIf("path", t.Path);
                    if (t.Headers.Count > 0) hu["headers"] = HeadersObject(t.Headers);
                    outbound["transport"] = hu;
                    return null;
                }
        }
        return null;
    }

    // ------------------------------------------------------------- helpers

    /// <summary>WS paths carry early-data size as <c>?ed=2048</c>; sing-box wants it split out.</summary>
    public static (string Path, int EarlyData) SplitEarlyData(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("/", 0);
        var idx = path.IndexOf("?ed=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (path, 0);
        var tail = path[(idx + 4)..];
        var end = tail.IndexOf('&');
        if (end >= 0) tail = tail[..end];
        return (path[..idx], int.TryParse(tail, out var n) ? n : 0);
    }

    public static string[] SplitHosts(string? host) =>
        string.IsNullOrWhiteSpace(host)
            ? []
            : host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string[] SplitPaths(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? ["/"]
            : path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonObject HeadersObject(IReadOnlyDictionary<string, string> headers)
    {
        var o = new JsonObject();
        foreach (var (k, v) in headers) o[k] = v;
        return o;
    }

    private static string[] DefaultUserAgents() =>
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
    ];
}
