using System.Text.Json;
using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// VMess. Two link shapes exist in the wild: the widespread base64-JSON blob and the
/// newer URI form; both are accepted, and links round-trip back to the JSON form.
/// </summary>
public sealed class VmessPlugin : ProtocolPluginBase
{
    public const string FieldId = "id";
    public const string FieldAlterId = "aid";
    public const string FieldSecurity = "scy";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "vmess",
        DisplayName = "VMess",
        Description = "Legacy V2Ray protocol with per-request encryption.",
        Schemes = ["vmess"],
        Engines = EngineSupport.Both,
        Order = 20,
        SupportsMux = true,
        Transports =
        [
            TransportKind.Raw, TransportKind.Kcp, TransportKind.WebSocket, TransportKind.Http2,
            TransportKind.Quic, TransportKind.Grpc, TransportKind.HttpUpgrade, TransportKind.XHttp,
        ],
        Security = [SecurityKind.None, SecurityKind.Tls, SecurityKind.Reality],
        Fields =
        [
            new FieldSpec(FieldId, "UUID", FieldKind.Uuid) { Required = true },
            new FieldSpec(FieldAlterId, "Alter ID", FieldKind.Number)
            {
                Default = "0",
                Min = 0,
                Max = 65535,
                Help = "Legacy MD5 auth. Keep at 0; anything else is deprecated and insecure.",
            },
            new FieldSpec(FieldSecurity, "Encryption", FieldKind.Choice)
            {
                Default = "auto",
                Choices =
                [
                    ChoiceOption.Of("auto"), ChoiceOption.Of("aes-128-gcm"),
                    ChoiceOption.Of("chacha20-poly1305"), ChoiceOption.Of("none"),
                    ChoiceOption.Of("zero"),
                ],
            },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        var text = link.Trim();
        if (!text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not a vmess:// link.";
            return false;
        }

        var payload = text[8..];
        var hash = payload.IndexOf('#');
        var fragment = hash >= 0 ? Uri.UnescapeDataString(payload[(hash + 1)..]) : "";
        if (hash >= 0) payload = payload[..hash];

        // Shape 1: base64-encoded JSON object.
        if (UriKit.TryDecodeBase64(payload) is { } json && json.TrimStart().StartsWith('{'))
            return TryParseJson(json, text, fragment, out node, out error);

        // Shape 2: URI form, identical in layout to a VLESS link.
        return TryParseUri(text, out node, out error);
    }

    private bool TryParseJson(string json, string original, string fragment, out ProxyNode? node, out string? error)
    {
        node = null;
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            error = $"VMess payload is not valid JSON: {ex.Message}";
            return false;
        }
        if (o is null)
        {
            error = "VMess payload is not a JSON object.";
            return false;
        }

        var address = Str(o, "add");
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "VMess JSON has no server address.";
            return false;
        }
        if (!int.TryParse(Str(o, "port"), out var port))
        {
            error = "VMess JSON has no usable port.";
            return false;
        }

        var net = TransportEmitter.ParseNetwork(Str(o, "net"));
        var headerType = Str(o, "type");
        var host = Str(o, "host");
        var path = Str(o, "path");
        var tls = Str(o, "tls");

        var transport = new TransportOptions
        {
            Kind = net,
            Path = path,
            Host = host,
            Obfs = net == TransportKind.Raw && string.Equals(headerType, "http", StringComparison.OrdinalIgnoreCase)
                ? HeaderObfs.Http
                : HeaderObfs.None,
            KcpHeaderType = net == TransportKind.Kcp ? headerType : null,
            KcpSeed = net == TransportKind.Kcp ? path : null,
            QuicHeaderType = net == TransportKind.Quic ? headerType : null,
            QuicSecurity = net == TransportKind.Quic ? host : null,
            QuicKey = net == TransportKind.Quic ? path : null,
            GrpcMultiMode = net == TransportKind.Grpc
                            && string.Equals(headerType, "multi", StringComparison.OrdinalIgnoreCase),
            XHttpModeValue = TransportEmitter.ParseXHttpMode(headerType),
        };

        var alpn = Str(o, "alpn");
        var securityKind = SecurityEmitter.ParseKind(tls);
        var security = new SecurityOptions
        {
            Kind = securityKind,
            ServerName = Str(o, "sni"),
            Fingerprint = Str(o, "fp"),
            Alpn = string.IsNullOrWhiteSpace(alpn)
                ? []
                : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowInsecure = StreamParamCodec.IsTruthy(Str(o, "allowInsecure")),
            PublicKey = Str(o, "pbk"),
            ShortId = Str(o, "sid"),
            SpiderX = Str(o, "spx"),
        };

        var remark = Str(o, "ps");
        if (string.IsNullOrWhiteSpace(remark)) remark = fragment;

        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = remark ?? "",
            Server = address!,
            Port = port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldId] = Str(o, "id") ?? "",
                [FieldAlterId] = Str(o, "aid") ?? "0",
                [FieldSecurity] = Str(o, "scy") ?? Str(o, "security") ?? "auto",
            },
            Transport = transport,
            Security = security,
            SourceLink = original,
        };
        error = null;
        return true;
    }

    private bool TryParseUri(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var uuid = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "VMess link carries no UUID.";
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
                [FieldAlterId] = StreamParamCodec.Value(q, "aid", "alterId") ?? "0",
                [FieldSecurity] = StreamParamCodec.Value(q, "scy", "security", "encryption") ?? "auto",
            },
            Transport = StreamParamCodec.ReadTransport(q),
            Security = StreamParamCodec.ReadSecurity(q),
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var t = node.Transport;
        var s = node.Security;

        var headerType = t.Kind switch
        {
            TransportKind.Raw => t.Obfs == HeaderObfs.Http ? "http" : "none",
            TransportKind.Kcp => t.KcpHeaderType ?? "none",
            TransportKind.Quic => t.QuicHeaderType ?? "none",
            TransportKind.Grpc => t.GrpcMultiMode ? "multi" : "gun",
            TransportKind.XHttp => TransportEmitter.XHttpModeName(t.XHttpModeValue),
            _ => "none",
        };

        var o = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = node.Remark,
            ["add"] = node.Server,
            ["port"] = node.Port.ToString(),
            ["id"] = node.GetOr(FieldId, ""),
            ["aid"] = node.GetOr(FieldAlterId, "0"),
            ["scy"] = node.GetOr(FieldSecurity, "auto"),
            ["net"] = TransportEmitter.XrayNetworkName(t.Kind),
            ["type"] = headerType,
            ["host"] = t.Kind == TransportKind.Quic ? t.QuicSecurity ?? "" : t.Host ?? "",
            ["path"] = t.Kind switch
            {
                TransportKind.Kcp => t.KcpSeed ?? "",
                TransportKind.Quic => t.QuicKey ?? "",
                _ => t.Path ?? "",
            },
            ["tls"] = SecurityEmitter.KindName(s.Kind) == "none" ? "" : SecurityEmitter.KindName(s.Kind),
        };

        if (!string.IsNullOrWhiteSpace(s.ServerName)) o["sni"] = s.ServerName;
        if (!string.IsNullOrWhiteSpace(s.Fingerprint)) o["fp"] = s.Fingerprint;
        if (s.Alpn.Count > 0) o["alpn"] = string.Join(",", s.Alpn);
        if (s.AllowInsecure) o["allowInsecure"] = "true";
        if (s.Kind == SecurityKind.Reality)
        {
            if (!string.IsNullOrWhiteSpace(s.PublicKey)) o["pbk"] = s.PublicKey;
            if (!string.IsNullOrWhiteSpace(s.ShortId)) o["sid"] = s.ShortId;
            if (!string.IsNullOrWhiteSpace(s.SpiderX)) o["spx"] = s.SpiderX;
        }

        return "vmess://" + UriKit.EncodeBase64(o.ToJsonString());
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();
        if (!string.IsNullOrWhiteSpace(node.Get(FieldId)) && !Guid.TryParse(node.Get(FieldId), out _))
            errors.Add("UUID is not a valid GUID.");
        if (!int.TryParse(node.GetOr(FieldAlterId, "0"), out var aid) || aid is < 0 or > 65535)
            errors.Add("Alter ID must be between 0 and 65535.");
        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        _ = int.TryParse(node.GetOr(FieldAlterId, "0"), out var alterId);
        var security = node.GetOr(FieldSecurity, "auto");

        JsonObject outbound;
        if (engine == EngineKind.Xray)
        {
            outbound = XrayVnext("vmess", node, ctx.Tag, new JsonObject
            {
                ["id"] = node.GetOr(FieldId, ""),
                ["alterId"] = alterId,
                ["security"] = security,
                ["level"] = 0,
            });
        }
        else
        {
            outbound = SingBoxShell("vmess", node, ctx.Tag);
            outbound["uuid"] = node.GetOr(FieldId, "");
            outbound["security"] = security;
            outbound["alter_id"] = alterId;
            outbound["packet_encoding"] = "xudp";
        }

        Decorate(outbound, node, engine, ctx);
        return outbound;
    }

    private static string? Str(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString().Trim('"');
    }
}
