using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// WireGuard. No transport or TLS layer applies — the protocol is its own encrypted
/// UDP tunnel, so the descriptor advertises neither.
/// </summary>
public sealed class WireGuardPlugin : ProtocolPluginBase
{
    public const string FieldPrivateKey = "privateKey";
    public const string FieldPeerPublicKey = "publicKey";
    public const string FieldPreSharedKey = "preSharedKey";
    public const string FieldAddress = "address";
    public const string FieldMtu = "mtu";
    public const string FieldReserved = "reserved";
    public const string FieldKeepAlive = "keepAlive";

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "wireguard",
        DisplayName = "WireGuard",
        Description = "Modern UDP VPN tunnel. Carries no separate transport or TLS layer.",
        Schemes = ["wireguard", "wg"],
        Engines = EngineSupport.Both,
        Order = 70,
        Transports = [],
        Security = [],
        Fields =
        [
            new FieldSpec(FieldPrivateKey, "Private key", FieldKind.Secret) { Required = true },
            new FieldSpec(FieldPeerPublicKey, "Peer public key", FieldKind.Text) { Required = true },
            new FieldSpec(FieldPreSharedKey, "Pre-shared key", FieldKind.Secret),
            new FieldSpec(FieldAddress, "Local addresses")
            {
                Required = true,
                Default = "172.16.0.2/32",
                Help = "Comma-separated CIDRs assigned to this client by the panel.",
            },
            new FieldSpec(FieldMtu, "MTU", FieldKind.Number) { Default = "1420", Min = 576, Max = 9000 },
            new FieldSpec(FieldReserved, "Reserved")
            {
                Placeholder = "0,0,0",
                Help = "Three bytes some providers require in the WireGuard header.",
            },
            new FieldSpec(FieldKeepAlive, "Keepalive (s)", FieldKind.Number) { Default = "0", Min = 0, Max = 600 },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var q = parts.QueryMap;
        var privateKey = Uri.UnescapeDataString(parts.UserInfo);
        if (string.IsNullOrWhiteSpace(privateKey))
            privateKey = StreamParamCodec.Value(q, "privatekey", "privateKey", "secretkey") ?? "";

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            error = "WireGuard link carries no private key.";
            return false;
        }

        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldPrivateKey] = privateKey,
                [FieldPeerPublicKey] = StreamParamCodec.Value(q, "publickey", "publicKey", "peer") ?? "",
                [FieldPreSharedKey] = StreamParamCodec.Value(q, "presharedkey", "preSharedKey", "psk") ?? "",
                [FieldAddress] = StreamParamCodec.Value(q, "address", "ip") ?? "172.16.0.2/32",
                [FieldMtu] = StreamParamCodec.Value(q, "mtu") ?? "1420",
                [FieldReserved] = StreamParamCodec.Value(q, "reserved") ?? "",
                [FieldKeepAlive] = StreamParamCodec.Value(q, "keepalive", "keepAlive") ?? "0",
            },
            Transport = TransportOptions.Default,
            Security = SecurityOptions.None,
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("publickey", node.Get(FieldPeerPublicKey)),
            new("presharedkey", node.Get(FieldPreSharedKey)),
            new("address", node.Get(FieldAddress)),
            new("mtu", node.Get(FieldMtu)),
            new("reserved", node.Get(FieldReserved)),
            new("keepalive", node.Get(FieldKeepAlive) is "0" ? null : node.Get(FieldKeepAlive)),
        };
        return Compose("wireguard", Uri.EscapeDataString(node.GetOr(FieldPrivateKey, "")), node, query);
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();

        foreach (var (key, label) in new[]
                 {
                     (FieldPrivateKey, "Private key"),
                     (FieldPeerPublicKey, "Peer public key"),
                     (FieldPreSharedKey, "Pre-shared key"),
                 })
        {
            var value = node.Get(key);
            if (!string.IsNullOrWhiteSpace(value) && !IsWireGuardKey(value))
                errors.Add($"{label} is not a 32-byte base64 WireGuard key.");
        }

        var reserved = ParseReserved(node.Get(FieldReserved));
        if (node.Get(FieldReserved) is { Length: > 0 } && reserved is null)
            errors.Add("Reserved must be three comma-separated bytes, e.g. 0,0,0.");

        if (string.IsNullOrWhiteSpace(node.Get(FieldAddress)))
            errors.Add("At least one local address is required.");

        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        var addresses = SplitList(node.GetOr(FieldAddress, ""));
        _ = int.TryParse(node.GetOr(FieldMtu, "1420"), out var mtu);
        _ = int.TryParse(node.GetOr(FieldKeepAlive, "0"), out var keepAlive);
        var reserved = ParseReserved(node.Get(FieldReserved));

        if (engine == EngineKind.Xray)
        {
            var peer = new JsonObject
            {
                ["publicKey"] = node.GetOr(FieldPeerPublicKey, ""),
                ["endpoint"] = node.Endpoint,
                ["allowedIPs"] = new JsonArray("0.0.0.0/0", "::/0"),
            };
            peer.SetIf("preSharedKey", node.Get(FieldPreSharedKey));
            peer.SetIfPositive("keepAlive", keepAlive);

            var settings = new JsonObject
            {
                ["secretKey"] = node.GetOr(FieldPrivateKey, ""),
                ["address"] = new JsonArray(addresses.Select(a => (JsonNode)a!).ToArray()),
                ["peers"] = new JsonArray(peer),
                ["mtu"] = mtu > 0 ? mtu : 1420,
            };
            if (reserved is not null)
                settings["reserved"] = new JsonArray(reserved.Select(r => (JsonNode)r).ToArray());

            return new JsonObject
            {
                ["tag"] = ctx.Tag,
                ["protocol"] = "wireguard",
                ["settings"] = settings,
            };
        }

        // From sing-box 1.11 WireGuard is an "endpoint", not an outbound: the interface
        // fields sit at the top level and the remote moves into a peers array.
        ctx.DeclareEndpoint();

        var singBoxPeer = new JsonObject
        {
            ["address"] = node.Server,
            ["port"] = node.Port,
            ["public_key"] = node.GetOr(FieldPeerPublicKey, ""),
            ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"),
        };
        singBoxPeer.SetIf("pre_shared_key", node.Get(FieldPreSharedKey));
        singBoxPeer.SetIfPositive("persistent_keepalive_interval", keepAlive);
        if (reserved is not null)
            singBoxPeer["reserved"] = new JsonArray(reserved.Select(r => (JsonNode)r).ToArray());

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = ctx.Tag,
            ["address"] = new JsonArray(addresses.Select(a => (JsonNode)a!).ToArray()),
            ["private_key"] = node.GetOr(FieldPrivateKey, ""),
            ["peers"] = new JsonArray(singBoxPeer),
        };
        if (mtu > 0) endpoint["mtu"] = mtu;

        return endpoint;
    }

    private static string[] SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Reserved is either "1,2,3" or the base64 form some panels emit.</summary>
    private static int[]? ParseReserved(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = SplitList(value);
        if (parts.Length == 3 && parts.All(p => byte.TryParse(p, out _)))
            return [.. parts.Select(p => (int)byte.Parse(p))];

        if (UriKit.TryDecodeBase64(value) is not null)
        {
            var buffer = new byte[4];
            if (Convert.TryFromBase64String(value.Replace('-', '+').Replace('_', '/'), buffer, out var written)
                && written == 3)
            {
                return [buffer[0], buffer[1], buffer[2]];
            }
        }

        return null;
    }

    /// <summary>WireGuard keys are exactly 32 bytes, base64-encoded to 44 characters.</summary>
    private static bool IsWireGuardKey(string value)
    {
        var normalised = value.Replace('-', '+').Replace('_', '/');
        if (normalised.Length is not (44 or 43)) return false;
        if (normalised.Length == 43) normalised += "=";
        var buffer = new byte[33];
        return Convert.TryFromBase64String(normalised, buffer, out var written) && written == 32;
    }
}
