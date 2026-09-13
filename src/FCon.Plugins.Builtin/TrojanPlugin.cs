using System.Text.Json.Nodes;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>Trojan — password auth over TLS, indistinguishable from ordinary HTTPS.</summary>
public sealed class TrojanPlugin : ProtocolPluginBase
{
    public const string FieldPassword = "password";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "trojan",
        DisplayName = "Trojan",
        Description = "Password authentication over TLS; rejects bad clients as a normal web server would.",
        Schemes = ["trojan"],
        Engines = EngineSupport.Both,
        Order = 30,
        SupportsMux = true,
        Transports =
        [
            TransportKind.Raw, TransportKind.Kcp, TransportKind.WebSocket, TransportKind.Http2,
            TransportKind.Quic, TransportKind.Grpc, TransportKind.HttpUpgrade, TransportKind.XHttp,
        ],
        Security = [SecurityKind.None, SecurityKind.Tls, SecurityKind.Reality],
        Fields =
        [
            new FieldSpec(FieldPassword, "Password", FieldKind.Secret) { Required = true },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var password = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Trojan link carries no password.";
            return false;
        }

        var q = parts.QueryMap;
        var security = StreamParamCodec.ReadSecurity(q);

        // Trojan is TLS-only by definition; links routinely omit the parameter.
        if (security.Kind == SecurityKind.None)
            security = security with { Kind = SecurityKind.Tls };

        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldPassword] = password,
            },
            Transport = StreamParamCodec.ReadTransport(q),
            Security = security,
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node) =>
        Compose("trojan",
            Uri.EscapeDataString(node.GetOr(FieldPassword, "")),
            node,
            StreamParamCodec.WriteParams(node));

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        var password = node.GetOr(FieldPassword, "");

        JsonObject outbound;
        if (engine == EngineKind.Xray)
        {
            outbound = XrayServers("trojan", node, ctx.Tag, new JsonObject
            {
                ["password"] = password,
                ["level"] = 0,
            });
        }
        else
        {
            outbound = SingBoxShell("trojan", node, ctx.Tag);
            outbound["password"] = password;
        }

        Decorate(outbound, node, engine, ctx);
        return outbound;
    }
}
