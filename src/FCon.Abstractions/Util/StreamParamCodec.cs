using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;

namespace FCon.Abstractions.Util;

/// <summary>
/// Reads and writes the transport/TLS query parameters shared by the VLESS, Trojan,
/// Shadowsocks and (URI-form) VMess link formats. Centralising this keeps every plugin
/// agreeing on what <c>type</c>, <c>security</c>, <c>fp</c>, <c>pbk</c> and friends mean.
/// </summary>
public static class StreamParamCodec
{
    public static TransportOptions ReadTransport(IReadOnlyDictionary<string, string> q)
    {
        var kind = TransportEmitter.ParseNetwork(Value(q, "type", "net", "network"));

        // Every transport spells its path and host differently; normalise here.
        var path = kind switch
        {
            TransportKind.Grpc => Value(q, "serviceName", "path"),
            _ => Value(q, "path"),
        };
        var host = Value(q, "host", "sni");

        return new TransportOptions
        {
            Kind = kind,
            Path = path,
            Host = kind == TransportKind.Grpc ? Value(q, "host") : host,
            Obfs = string.Equals(Value(q, "headerType"), "http", StringComparison.OrdinalIgnoreCase)
                ? HeaderObfs.Http
                : HeaderObfs.None,
            KcpSeed = Value(q, "seed"),
            KcpHeaderType = kind == TransportKind.Kcp ? Value(q, "headerType") : null,
            QuicSecurity = Value(q, "quicSecurity"),
            QuicKey = Value(q, "key"),
            QuicHeaderType = kind == TransportKind.Quic ? Value(q, "headerType") : null,
            GrpcMultiMode = string.Equals(Value(q, "mode"), "multi", StringComparison.OrdinalIgnoreCase),
            GrpcAuthority = Value(q, "authority"),
            XHttpModeValue = TransportEmitter.ParseXHttpMode(Value(q, "mode")),
            XHttpExtra = Value(q, "extra"),
        };
    }

    public static SecurityOptions ReadSecurity(IReadOnlyDictionary<string, string> q)
    {
        var kind = SecurityEmitter.ParseKind(Value(q, "security"));

        // A REALITY public key implies REALITY even when the link forgot to say so.
        if (kind == SecurityKind.None && !string.IsNullOrWhiteSpace(Value(q, "pbk")))
            kind = SecurityKind.Reality;

        var alpn = Value(q, "alpn");
        return new SecurityOptions
        {
            Kind = kind,
            ServerName = Value(q, "sni", "peer", "servername"),
            Fingerprint = Value(q, "fp", "fingerprint"),
            Alpn = string.IsNullOrWhiteSpace(alpn)
                ? []
                : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowInsecure = IsTruthy(Value(q, "allowInsecure", "insecure", "skip-cert-verify")),
            PublicKey = Value(q, "pbk", "publicKey"),
            ShortId = Value(q, "sid", "shortId"),
            SpiderX = Value(q, "spx", "spiderX"),
        };
    }

    /// <summary>Emit the query parameters describing a node transport and TLS layer.</summary>
    public static List<KeyValuePair<string, string?>> WriteParams(ProxyNode node)
    {
        var t = node.Transport;
        var s = node.Security;
        var list = new List<KeyValuePair<string, string?>>
        {
            new("type", TransportEmitter.XrayNetworkName(t.Kind)),
            new("security", SecurityEmitter.KindName(s.Kind)),
        };

        switch (t.Kind)
        {
            case TransportKind.Raw:
                if (t.Obfs == HeaderObfs.Http)
                {
                    list.Add(new("headerType", "http"));
                    list.Add(new("host", t.Host));
                    list.Add(new("path", t.Path));
                }
                break;

            case TransportKind.Kcp:
                list.Add(new("headerType", t.KcpHeaderType));
                list.Add(new("seed", t.KcpSeed));
                break;

            case TransportKind.WebSocket:
            case TransportKind.HttpUpgrade:
                list.Add(new("path", t.Path));
                list.Add(new("host", t.Host));
                break;

            case TransportKind.Http2:
                list.Add(new("path", t.Path));
                list.Add(new("host", t.Host));
                break;

            case TransportKind.Quic:
                list.Add(new("quicSecurity", t.QuicSecurity));
                list.Add(new("key", t.QuicKey));
                list.Add(new("headerType", t.QuicHeaderType));
                break;

            case TransportKind.Grpc:
                list.Add(new("serviceName", t.Path));
                list.Add(new("authority", t.GrpcAuthority));
                if (t.GrpcMultiMode) list.Add(new("mode", "multi"));
                break;

            case TransportKind.XHttp:
                list.Add(new("path", t.Path));
                list.Add(new("host", t.Host));
                list.Add(new("mode", TransportEmitter.XHttpModeName(t.XHttpModeValue)));
                list.Add(new("extra", t.XHttpExtra));
                break;
        }

        if (s.Kind != SecurityKind.None)
        {
            list.Add(new("sni", s.ServerName));
            list.Add(new("fp", s.Fingerprint));
            if (s.Alpn.Count > 0) list.Add(new("alpn", string.Join(",", s.Alpn)));
            if (s.AllowInsecure) list.Add(new("allowInsecure", "1"));
        }

        if (s.Kind == SecurityKind.Reality)
        {
            list.Add(new("pbk", s.PublicKey));
            list.Add(new("sid", s.ShortId));
            list.Add(new("spx", s.SpiderX));
        }

        return list;
    }

    /// <summary>First non-empty value among <paramref name="keys"/>.</summary>
    public static string? Value(IReadOnlyDictionary<string, string> q, params string[] keys)
    {
        foreach (var key in keys)
            if (q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    public static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
