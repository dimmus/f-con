using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;

namespace FCon.App.ViewModels;

/// <summary>
/// Describes the transport and TLS sections using the same <see cref="FieldSpec"/> vocabulary
/// plugins use, so the editor has exactly one renderer for every field in the app — and a
/// third-party plugin gets the same UI quality as the built-ins for free.
/// </summary>
public static class NodeFormSchema
{
    // Transport keys
    public const string Path = "t.path";
    public const string Host = "t.host";
    public const string Obfs = "t.obfs";
    public const string KcpSeed = "t.kcpSeed";
    public const string KcpHeader = "t.kcpHeader";
    public const string QuicSecurity = "t.quicSecurity";
    public const string QuicKey = "t.quicKey";
    public const string QuicHeader = "t.quicHeader";
    public const string GrpcMulti = "t.grpcMulti";
    public const string GrpcAuthority = "t.grpcAuthority";
    public const string XHttpModeKey = "t.xhttpMode";
    public const string XHttpExtra = "t.xhttpExtra";

    // Security keys
    public const string Sni = "s.sni";
    public const string Fingerprint = "s.fp";
    public const string Alpn = "s.alpn";
    public const string AllowInsecure = "s.allowInsecure";
    public const string PublicKey = "s.pbk";
    public const string ShortId = "s.sid";
    public const string SpiderX = "s.spx";

    // Mux keys
    public const string MuxEnabled = "m.enabled";
    public const string MuxConcurrency = "m.concurrency";
    public const string MuxProtocol = "m.protocol";

    private static readonly string[] HeaderTypes =
        ["none", "srtp", "utp", "wechat-video", "dtls", "wireguard", "dns"];

    public static IReadOnlyList<FieldSpec> ForTransport(TransportKind kind) => kind switch
    {
        TransportKind.Raw =>
        [
            new FieldSpec(Obfs, "HTTP header obfuscation", FieldKind.Toggle)
            {
                Help = "Wrap the stream in a fake HTTP request. Xray only.",
            },
            new FieldSpec(Host, "Fake Host header")
            {
                VisibleWhenKey = Obfs, VisibleWhenValues = ["true"],
                Placeholder = "example.com, cdn.example.com",
            },
            new FieldSpec(Path, "Fake request path")
            {
                VisibleWhenKey = Obfs, VisibleWhenValues = ["true"], Placeholder = "/",
            },
        ],

        TransportKind.Kcp =>
        [
            new FieldSpec(KcpHeader, "Header type", FieldKind.Choice)
            {
                Default = "none", Choices = [.. HeaderTypes.Select(ChoiceOption.Of)],
            },
            new FieldSpec(KcpSeed, "Seed", FieldKind.Secret)
            {
                Help = "Shared obfuscation password. Must match the server exactly.",
            },
        ],

        TransportKind.WebSocket =>
        [
            new FieldSpec(Path, "Path") { Default = "/", Placeholder = "/ws or /ws?ed=2048", Required = true },
            new FieldSpec(Host, "Host header") { Placeholder = "cdn.example.com" },
        ],

        TransportKind.Http2 =>
        [
            new FieldSpec(Path, "Path") { Default = "/", Required = true },
            new FieldSpec(Host, "Hosts") { Placeholder = "a.example.com, b.example.com" },
        ],

        TransportKind.Quic =>
        [
            new FieldSpec(QuicSecurity, "Obfuscation", FieldKind.Choice)
            {
                Default = "none",
                Choices = [ChoiceOption.Of("none"), ChoiceOption.Of("aes-128-gcm"), ChoiceOption.Of("chacha20-poly1305")],
            },
            new FieldSpec(QuicKey, "Obfuscation key", FieldKind.Secret)
            {
                VisibleWhenKey = QuicSecurity, VisibleWhenValues = ["aes-128-gcm", "chacha20-poly1305"],
            },
            new FieldSpec(QuicHeader, "Header type", FieldKind.Choice)
            {
                Default = "none", Choices = [.. HeaderTypes.Select(ChoiceOption.Of)],
            },
        ],

        TransportKind.Grpc =>
        [
            new FieldSpec(Path, "Service name") { Required = true, Placeholder = "GunService" },
            new FieldSpec(GrpcMulti, "Multi mode", FieldKind.Toggle),
            new FieldSpec(GrpcAuthority, "Authority") { Placeholder = "optional override" },
        ],

        TransportKind.HttpUpgrade =>
        [
            new FieldSpec(Path, "Path") { Default = "/", Required = true },
            new FieldSpec(Host, "Host header"),
        ],

        TransportKind.XHttp =>
        [
            new FieldSpec(Path, "Path") { Default = "/", Required = true },
            new FieldSpec(Host, "Host header"),
            new FieldSpec(XHttpModeKey, "Mode", FieldKind.Choice)
            {
                Default = "auto",
                Choices =
                [
                    ChoiceOption.Of("auto"), ChoiceOption.Of("packet-up"),
                    ChoiceOption.Of("stream-up"), ChoiceOption.Of("stream-one"),
                ],
            },
            new FieldSpec(XHttpExtra, "Extra (JSON)", FieldKind.Multiline)
            {
                Help = "Merged verbatim into xhttpSettings.extra.",
            },
        ],

        _ => [],
    };

    public static IReadOnlyList<FieldSpec> ForSecurity(SecurityKind kind)
    {
        if (kind == SecurityKind.None) return [];

        var common = new List<FieldSpec>
        {
            new(Sni, "SNI") { Placeholder = "defaults to the Host header, then the address" },
            new(Fingerprint, "uTLS fingerprint", FieldKind.Choice)
            {
                Default = "chrome",
                Choices = [new ChoiceOption("", "none"), .. SecurityEmitter.Fingerprints.Select(ChoiceOption.Of)],
            },
        };

        if (kind == SecurityKind.Reality)
        {
            common.Add(new FieldSpec(PublicKey, "REALITY public key") { Required = true });
            common.Add(new FieldSpec(ShortId, "Short ID"));
            common.Add(new FieldSpec(SpiderX, "SpiderX") { Placeholder = "/" });
            return common;
        }

        common.Add(new FieldSpec(Alpn, "ALPN") { Placeholder = "h2,http/1.1" });
        common.Add(new FieldSpec(AllowInsecure, "Skip certificate verification", FieldKind.Toggle)
        {
            Help = "Leave off. Enabling this removes the protection TLS is there to provide.",
        });
        return common;
    }

    public static IReadOnlyList<FieldSpec> ForMux() =>
    [
        new FieldSpec(MuxEnabled, "Enable multiplexing", FieldKind.Toggle),
        new FieldSpec(MuxConcurrency, "Max streams", FieldKind.Number)
        {
            Default = "8", Min = 1, Max = 1024,
            VisibleWhenKey = MuxEnabled, VisibleWhenValues = ["true"],
        },
        new FieldSpec(MuxProtocol, "Protocol (sing-box)", FieldKind.Choice)
        {
            Default = "smux",
            Choices = [ChoiceOption.Of("smux"), ChoiceOption.Of("yamux"), ChoiceOption.Of("h2mux")],
            VisibleWhenKey = MuxEnabled, VisibleWhenValues = ["true"],
        },
    ];

    // ------------------------------------------------------------- mapping

    /// <summary>Flatten a node's transport, TLS and mux settings into the editor's value map.</summary>
    public static void Read(ProxyNode node, IDictionary<string, string> values)
    {
        var t = node.Transport;
        values[Path] = t.Kind == TransportKind.Grpc ? t.Path ?? "" : t.Path ?? "";
        values[Host] = t.Host ?? "";
        values[Obfs] = t.Obfs == HeaderObfs.Http ? "true" : "false";
        values[KcpSeed] = t.KcpSeed ?? "";
        values[KcpHeader] = t.KcpHeaderType ?? "none";
        values[QuicSecurity] = t.QuicSecurity ?? "none";
        values[QuicKey] = t.QuicKey ?? "";
        values[QuicHeader] = t.QuicHeaderType ?? "none";
        values[GrpcMulti] = t.GrpcMultiMode ? "true" : "false";
        values[GrpcAuthority] = t.GrpcAuthority ?? "";
        values[XHttpModeKey] = TransportEmitter.XHttpModeName(t.XHttpModeValue);
        values[XHttpExtra] = t.XHttpExtra ?? "";

        var s = node.Security;
        values[Sni] = s.ServerName ?? "";
        values[Fingerprint] = s.Fingerprint ?? "";
        values[Alpn] = string.Join(",", s.Alpn);
        values[AllowInsecure] = s.AllowInsecure ? "true" : "false";
        values[PublicKey] = s.PublicKey ?? "";
        values[ShortId] = s.ShortId ?? "";
        values[SpiderX] = s.SpiderX ?? "";

        var m = node.Mux;
        values[MuxEnabled] = m.Enabled ? "true" : "false";
        values[MuxConcurrency] = m.Concurrency.ToString();
        values[MuxProtocol] = m.Protocol;
    }

    public static TransportOptions WriteTransport(TransportKind kind, IReadOnlyDictionary<string, string> v) => new()
    {
        Kind = kind,
        Path = Get(v, Path),
        Host = Get(v, Host),
        Obfs = IsTrue(v, Obfs) ? HeaderObfs.Http : HeaderObfs.None,
        KcpSeed = Get(v, KcpSeed),
        KcpHeaderType = Get(v, KcpHeader),
        QuicSecurity = Get(v, QuicSecurity),
        QuicKey = Get(v, QuicKey),
        QuicHeaderType = Get(v, QuicHeader),
        GrpcMultiMode = IsTrue(v, GrpcMulti),
        GrpcAuthority = Get(v, GrpcAuthority),
        XHttpModeValue = TransportEmitter.ParseXHttpMode(Get(v, XHttpModeKey)),
        XHttpExtra = Get(v, XHttpExtra),
    };

    public static SecurityOptions WriteSecurity(SecurityKind kind, IReadOnlyDictionary<string, string> v)
    {
        var alpn = Get(v, Alpn);
        return new SecurityOptions
        {
            Kind = kind,
            ServerName = Get(v, Sni),
            Fingerprint = Get(v, Fingerprint),
            Alpn = alpn is null
                ? []
                : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowInsecure = IsTrue(v, AllowInsecure),
            PublicKey = Get(v, PublicKey),
            ShortId = Get(v, ShortId),
            SpiderX = Get(v, SpiderX),
        };
    }

    public static MuxOptions WriteMux(IReadOnlyDictionary<string, string> v) => new()
    {
        Enabled = IsTrue(v, MuxEnabled),
        Concurrency = int.TryParse(Get(v, MuxConcurrency), out var n) ? n : 8,
        Protocol = Get(v, MuxProtocol) ?? "smux",
    };

    private static string? Get(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool IsTrue(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
