using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// VLESS — the protocol 3x-ui provisions by default, and the only one carrying
/// XTLS Vision flow control and REALITY.
/// </summary>
public sealed class VlessPlugin : ProtocolPluginBase
{
    public const string FieldId = "id";
    public const string FieldFlow = "flow";
    public const string FieldEncryption = "encryption";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "vless",
        DisplayName = "VLESS",
        Description = "Stateless lightweight transport. Supports XTLS Vision flow control and REALITY.",
        Schemes = ["vless"],
        Engines = EngineSupport.Both,
        Order = 10,
        SupportsMux = true,
        Transports =
        [
            TransportKind.Raw, TransportKind.Kcp, TransportKind.WebSocket, TransportKind.Http2,
            TransportKind.Quic, TransportKind.Grpc, TransportKind.HttpUpgrade, TransportKind.XHttp,
        ],
        Security = [SecurityKind.None, SecurityKind.Tls, SecurityKind.Reality],
        Fields =
        [
            new FieldSpec(FieldId, "UUID", FieldKind.Uuid) { Required = true, Help = "Client id issued by the panel." },
            new FieldSpec(FieldFlow, "Flow", FieldKind.Choice)
            {
                Default = "",
                Help = "XTLS Vision requires the Raw transport with TLS or REALITY.",
                Choices =
                [
                    new ChoiceOption("", "none"),
                    ChoiceOption.Of("xtls-rprx-vision"),
                    ChoiceOption.Of("xtls-rprx-vision-udp443"),
                ],
            },
            new FieldSpec(FieldEncryption, "Encryption")
            {
                Default = "none",
                Help = "Kept at \"none\" unless the server enables the newer VLESS encryption.",
            },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var uuid = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "VLESS link carries no UUID.";
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
                [FieldId] = uuid,
                [FieldFlow] = StreamParamCodec.Value(q, "flow") ?? "",
                [FieldEncryption] = StreamParamCodec.Value(q, "encryption") ?? "none",
            },
            Transport = StreamParamCodec.ReadTransport(q),
            Security = StreamParamCodec.ReadSecurity(q),
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var query = StreamParamCodec.WriteParams(node);
        query.Insert(0, new("encryption", node.GetOr(FieldEncryption, "none")));
        query.Add(new("flow", node.Get(FieldFlow)));
        return Compose("vless", Uri.EscapeDataString(node.GetOr(FieldId, "")), node, query);
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();

        if (!string.IsNullOrWhiteSpace(node.Get(FieldId)) && !Guid.TryParse(node.Get(FieldId), out _))
            errors.Add("UUID is not a valid GUID.");

        var flow = node.Get(FieldFlow);
        if (!string.IsNullOrWhiteSpace(flow))
        {
            if (node.Transport.Kind != TransportKind.Raw)
                errors.Add("XTLS Vision flow requires the Raw (TCP) transport.");
            if (node.Security.Kind == SecurityKind.None)
                errors.Add("XTLS Vision flow requires TLS or REALITY.");
        }

        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        var uuid = node.GetOr(FieldId, "");
        var flow = node.Get(FieldFlow);

        JsonObject outbound;
        if (engine == EngineKind.Xray)
        {
            var user = new JsonObject
            {
                ["id"] = uuid,
                ["encryption"] = node.GetOr(FieldEncryption, "none"),
                ["level"] = 0,
            };
            user.SetIf("flow", flow);
            outbound = XrayVnext("vless", node, ctx.Tag, user);
        }
        else
        {
            outbound = SingBoxShell("vless", node, ctx.Tag);
            outbound["uuid"] = uuid;
            outbound.SetIf("flow", flow);
            // xudp keeps UDP-over-TCP framing compatible with Xray servers.
            outbound["packet_encoding"] = "xudp";

            var encryption = node.GetOr(FieldEncryption, "none");
            if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
                ctx.Warn("sing-box does not implement VLESS encryption; use the Xray engine for this server.");
        }

        Decorate(outbound, node, engine, ctx);
        return outbound;
    }
}
