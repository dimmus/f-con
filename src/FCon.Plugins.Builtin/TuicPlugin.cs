using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// TUIC v5: multiplexed QUIC with a choice of congestion controller. sing-box only.
/// Link: <c>tuic://uuid:password@host:port?congestion_control=bbr&amp;udp_relay_mode=native&amp;sni=&amp;alpn=h3</c>.
/// </summary>
public sealed class TuicPlugin : ProtocolPluginBase
{
    public const string FieldUuid = "uuid";
    public const string FieldPassword = "password";
    public const string FieldCongestion = "congestion";
    public const string FieldUdpRelayMode = "udpRelayMode";
    public const string FieldZeroRtt = "zeroRtt";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "tuic",
        DisplayName = "TUIC",
        Description = "Multiplexed QUIC tunnel (v5) with BBR. sing-box only.",
        Schemes = ["tuic"],
        Engines = EngineSupport.SingBox,
        Order = 81,
        Transports = [],
        Security = [SecurityKind.Tls],
        Fields =
        [
            new FieldSpec(FieldUuid, "UUID", FieldKind.Uuid) { Required = true },
            new FieldSpec(FieldPassword, "Password", FieldKind.Secret) { Required = true },
            new FieldSpec(FieldCongestion, "Congestion control", FieldKind.Choice)
            {
                Default = "bbr",
                Choices = [ChoiceOption.Of("bbr"), ChoiceOption.Of("cubic"), ChoiceOption.Of("new_reno")],
            },
            new FieldSpec(FieldUdpRelayMode, "UDP relay mode", FieldKind.Choice)
            {
                Default = "native",
                Choices = [ChoiceOption.Of("native"), ChoiceOption.Of("quic")],
            },
            new FieldSpec(FieldZeroRtt, "0-RTT handshake", FieldKind.Toggle)
            {
                Default = "false",
                Help = "Faster reconnects at the cost of replay protection on the first packet.",
            },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var userInfo = Uri.UnescapeDataString(parts.UserInfo);
        var colon = userInfo.IndexOf(':');
        var uuid = colon < 0 ? userInfo : userInfo[..colon];
        var password = colon < 0 ? "" : userInfo[(colon + 1)..];
        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "TUIC link carries no UUID.";
            return false;
        }

        var q = parts.QueryMap;
        var security = StreamParamCodec.ReadSecurity(q);
        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldUuid] = uuid,
                [FieldPassword] = password,
                [FieldCongestion] = StreamParamCodec.Value(q, "congestion_control", "congestion") ?? "bbr",
                [FieldUdpRelayMode] = StreamParamCodec.Value(q, "udp_relay_mode") ?? "native",
                [FieldZeroRtt] = StreamParamCodec.IsTruthy(StreamParamCodec.Value(q, "zero_rtt_handshake", "reduce_rtt"))
                    ? "true"
                    : "false",
            },
            Transport = TransportOptions.Default,
            Security = security with
            {
                Kind = SecurityKind.Tls,
                AllowInsecure = security.AllowInsecure
                                || StreamParamCodec.IsTruthy(StreamParamCodec.Value(q, "allow_insecure")),
            },
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var s = node.Security;
        var query = new List<KeyValuePair<string, string?>>
        {
            new("congestion_control", node.Get(FieldCongestion)),
            new("udp_relay_mode", node.Get(FieldUdpRelayMode)),
            new("sni", s.ServerName),
            new("alpn", s.Alpn.Count > 0 ? string.Join(",", s.Alpn) : null),
            new("allow_insecure", s.AllowInsecure ? "1" : null),
            new("zero_rtt_handshake", StreamParamCodec.IsTruthy(node.Get(FieldZeroRtt)) ? "1" : null),
        };
        var userInfo = Uri.EscapeDataString(node.GetOr(FieldUuid, ""))
                       + ":" + Uri.EscapeDataString(node.GetOr(FieldPassword, ""));
        return Compose("tuic", userInfo, node, query);
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();
        if (!string.IsNullOrWhiteSpace(node.Get(FieldUuid)) && !Guid.TryParse(node.Get(FieldUuid), out _))
            errors.Add("UUID is not a valid GUID.");
        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        if (engine != EngineKind.SingBox)
            throw new NotSupportedException("TUIC needs the sing-box engine; Xray does not implement it.");

        var outbound = SingBoxShell("tuic", node, ctx.Tag);
        outbound["uuid"] = node.GetOr(FieldUuid, "");
        outbound["password"] = node.GetOr(FieldPassword, "");
        outbound["congestion_control"] = node.GetOr(FieldCongestion, "bbr");
        outbound["udp_relay_mode"] = node.GetOr(FieldUdpRelayMode, "native");
        outbound.SetIf("zero_rtt_handshake", StreamParamCodec.IsTruthy(node.Get(FieldZeroRtt)));
        outbound["heartbeat"] = "10s";

        var tls = new JsonObject { ["enabled"] = true };
        tls.SetIf("server_name", SecurityEmitter.DeriveSni(node));
        tls.SetIf("insecure", node.Security.AllowInsecure);
        tls["alpn"] = node.Security.Alpn.Count > 0
            ? new JsonArray(node.Security.Alpn.Select(a => (JsonNode)a).ToArray())
            : new JsonArray("h3");
        outbound["tls"] = tls;

        return outbound;
    }
}
