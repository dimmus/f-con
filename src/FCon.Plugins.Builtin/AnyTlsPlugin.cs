using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// AnyTLS: plain TLS sessions with padding and session reuse, designed to defeat
/// TLS-in-TLS fingerprinting. sing-box 1.12+ only.
/// Link: <c>anytls://password@host:port?sni=&amp;fp=chrome&amp;insecure=1&amp;alpn=</c>.
/// </summary>
public sealed class AnyTlsPlugin : ProtocolPluginBase
{
    public const string FieldPassword = "password";
    public const string FieldIdleTimeout = "idleTimeout";
    public const string FieldMinIdle = "minIdle";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "anytls",
        DisplayName = "AnyTLS",
        Description = "TLS with padding and session reuse against TLS-in-TLS detection. sing-box 1.12+ only.",
        Schemes = ["anytls"],
        Engines = EngineSupport.SingBox,
        Order = 82,
        Transports = [],
        Security = [SecurityKind.Tls],
        Fields =
        [
            new FieldSpec(FieldPassword, "Password", FieldKind.Secret) { Required = true },
            new FieldSpec(FieldIdleTimeout, "Idle session timeout (s)", FieldKind.Number)
            {
                Default = "30", Min = 5, Max = 600,
            },
            new FieldSpec(FieldMinIdle, "Sessions kept warm", FieldKind.Number)
            {
                Default = "0", Min = 0, Max = 16,
                Help = "Idle sessions to keep open so the next request skips the handshake.",
            },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var password = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "AnyTLS link carries no password.";
            return false;
        }

        var q = parts.QueryMap;
        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldPassword] = password,
                [FieldIdleTimeout] = StreamParamCodec.Value(q, "idle_session_timeout") ?? "30",
                [FieldMinIdle] = StreamParamCodec.Value(q, "min_idle_session") ?? "0",
            },
            Transport = TransportOptions.Default,
            Security = StreamParamCodec.ReadSecurity(q) with { Kind = SecurityKind.Tls },
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var s = node.Security;
        var query = new List<KeyValuePair<string, string?>>
        {
            new("sni", s.ServerName),
            new("fp", s.Fingerprint),
            new("alpn", s.Alpn.Count > 0 ? string.Join(",", s.Alpn) : null),
            new("insecure", s.AllowInsecure ? "1" : null),
        };
        return Compose("anytls", Uri.EscapeDataString(node.GetOr(FieldPassword, "")), node, query);
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        if (engine != EngineKind.SingBox)
            throw new NotSupportedException("AnyTLS needs the sing-box engine; Xray does not implement it.");

        if (!ctx.EngineAtLeast(1, 12))
            throw new NotSupportedException("AnyTLS needs sing-box 1.12 or newer.");

        var outbound = SingBoxShell("anytls", node, ctx.Tag);
        outbound["password"] = node.GetOr(FieldPassword, "");

        if (int.TryParse(node.Get(FieldIdleTimeout), out var idle) && idle > 0)
        {
            outbound["idle_session_check_interval"] = "30s";
            outbound["idle_session_timeout"] = $"{idle}s";
        }
        if (int.TryParse(node.Get(FieldMinIdle), out var min) && min > 0)
            outbound["min_idle_session"] = min;

        // The shared emitter handles SNI, uTLS, ALPN and insecure exactly as for VLESS.
        ctx.ApplySecurity(outbound, node, engine);
        return outbound;
    }
}
