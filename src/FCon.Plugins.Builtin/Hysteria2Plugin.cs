using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// Hysteria 2: QUIC with its own congestion control, built for lossy and throttled
/// links. sing-box only. Link format follows the upstream URI scheme:
/// <c>hysteria2://password@host:port?sni=&amp;obfs=salamander&amp;obfs-password=&amp;insecure=1&amp;mport=2080-3000</c>.
/// </summary>
public sealed class Hysteria2Plugin : ProtocolPluginBase
{
    public const string FieldPassword = "password";
    public const string FieldObfs = "obfs";
    public const string FieldObfsPassword = "obfsPassword";
    public const string FieldPorts = "ports";
    public const string FieldUpMbps = "upMbps";
    public const string FieldDownMbps = "downMbps";
    public const string FieldPinSha256 = "pinSHA256";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "hysteria2",
        DisplayName = "Hysteria 2",
        Description = "QUIC tunnel with Brutal congestion control. Holds up on lossy links; sing-box only.",
        Schemes = ["hysteria2", "hy2"],
        Engines = EngineSupport.SingBox,
        Order = 80,
        Transports = [],
        Security = [SecurityKind.Tls],
        Fields =
        [
            new FieldSpec(FieldPassword, "Password", FieldKind.Secret) { Required = true },
            new FieldSpec(FieldObfs, "Obfuscation", FieldKind.Choice)
            {
                Default = "",
                Choices = [new ChoiceOption("", "none"), ChoiceOption.Of("salamander")],
            },
            new FieldSpec(FieldObfsPassword, "Obfuscation password", FieldKind.Secret)
            {
                VisibleWhenKey = FieldObfs, VisibleWhenValues = ["salamander"], Required = true,
            },
            new FieldSpec(FieldPorts, "Port hopping")
            {
                Placeholder = "2080-3000,5000",
                Help = "Extra port ranges the server listens on; the client rotates through them. Needs sing-box 1.12+.",
            },
            new FieldSpec(FieldUpMbps, "Upload Mbps", FieldKind.Number) { Default = "0", Min = 0, Max = 100000 },
            new FieldSpec(FieldDownMbps, "Download Mbps", FieldKind.Number) { Default = "0", Min = 0, Max = 100000 },
            new FieldSpec(FieldPinSha256, "Pinned cert SHA-256") { Placeholder = "optional" },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var q = parts.QueryMap;
        var password = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Hysteria 2 link carries no password.";
            return false;
        }

        var security = StreamParamCodec.ReadSecurity(q);
        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldPassword] = password,
                [FieldObfs] = StreamParamCodec.Value(q, "obfs") ?? "",
                [FieldObfsPassword] = StreamParamCodec.Value(q, "obfs-password", "obfsPassword") ?? "",
                [FieldPorts] = StreamParamCodec.Value(q, "mport", "ports") ?? "",
                [FieldUpMbps] = StreamParamCodec.Value(q, "up", "upmbps") ?? "0",
                [FieldDownMbps] = StreamParamCodec.Value(q, "down", "downmbps") ?? "0",
                [FieldPinSha256] = StreamParamCodec.Value(q, "pinSHA256") ?? "",
            },
            Transport = TransportOptions.Default,
            // Always TLS; the link never says so explicitly.
            Security = security with { Kind = SecurityKind.Tls },
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
            new("insecure", s.AllowInsecure ? "1" : null),
            new("alpn", s.Alpn.Count > 0 ? string.Join(",", s.Alpn) : null),
            new("obfs", node.Get(FieldObfs)),
            new("obfs-password", node.Get(FieldObfs) is { Length: > 0 } ? node.Get(FieldObfsPassword) : null),
            new("mport", node.Get(FieldPorts)),
            new("up", node.Get(FieldUpMbps) is "0" ? null : node.Get(FieldUpMbps)),
            new("down", node.Get(FieldDownMbps) is "0" ? null : node.Get(FieldDownMbps)),
            new("pinSHA256", node.Get(FieldPinSha256)),
        };
        return Compose("hysteria2", Uri.EscapeDataString(node.GetOr(FieldPassword, "")), node, query);
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();
        if (node.Get(FieldPorts) is { Length: > 0 } ports && ParsePortRanges(ports) is null)
            errors.Add("Port hopping must be a comma-separated list of ports or ranges, e.g. 2080-3000,5000.");
        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        if (engine != EngineKind.SingBox)
            throw new NotSupportedException("Hysteria 2 needs the sing-box engine; Xray does not implement it.");

        var outbound = SingBoxShell("hysteria2", node, ctx.Tag);
        outbound["password"] = node.GetOr(FieldPassword, "");

        if (int.TryParse(node.Get(FieldUpMbps), out var up) && up > 0) outbound["up_mbps"] = up;
        if (int.TryParse(node.Get(FieldDownMbps), out var down) && down > 0) outbound["down_mbps"] = down;

        if (node.Get(FieldObfs) is { Length: > 0 } obfs)
        {
            outbound["obfs"] = new JsonObject
            {
                ["type"] = obfs,
                ["password"] = node.GetOr(FieldObfsPassword, ""),
            };
        }

        if (node.Get(FieldPorts) is { Length: > 0 } ports && ParsePortRanges(ports) is { } ranges)
        {
            if (ctx.EngineAtLeast(1, 12))
            {
                outbound["server_ports"] = new JsonArray(ranges.Select(r => (JsonNode)r).ToArray());
                outbound["hop_interval"] = "30s";
            }
            else
            {
                ctx.Warn("Port hopping needs sing-box 1.12 or newer; only the main port is used.");
            }
        }

        // Hysteria 2 is always TLS over QUIC, and the ALPN is fixed by the protocol.
        var tls = new JsonObject { ["enabled"] = true };
        tls.SetIf("server_name", SecurityEmitter.DeriveSni(node));
        tls.SetIf("insecure", node.Security.AllowInsecure);
        tls["alpn"] = new JsonArray("h3");
        if (node.Get(FieldPinSha256) is { Length: > 0 })
            ctx.Warn("sing-box has no certificate pin for Hysteria 2; pinSHA256 was ignored.");
        outbound["tls"] = tls;

        return outbound;
    }

    /// <summary>"2080-3000,5000" → ["2080:3000", "5000:5000"], the form sing-box wants.</summary>
    internal static string[]? ParsePortRanges(string value)
    {
        var result = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out var p) || p is <= 0 or > 65535) return null;
                result.Add($"{p}:{p}");
                continue;
            }

            if (!int.TryParse(part[..dash], out var from) || !int.TryParse(part[(dash + 1)..], out var to)) return null;
            if (from is <= 0 or > 65535 || to is <= 0 or > 65535 || to < from) return null;
            result.Add($"{from}:{to}");
        }
        return result.Count == 0 ? null : [.. result];
    }
}
