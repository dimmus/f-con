using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// Shadowsocks, including the Shadowsocks-2022 AEAD ciphers that 3x-ui provisions.
/// Handles both the legacy fully-base64 link and the SIP002 form.
/// </summary>
public sealed class ShadowsocksPlugin : ProtocolPluginBase
{
    public const string FieldMethod = "method";
    public const string FieldPassword = "password";
    public const string FieldPlugin = "plugin";
    public const string FieldPluginOpts = "pluginOpts";
    public const string FieldUot = "uot";

    private static readonly string[] Methods =
    [
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-256-gcm", "aes-128-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "aes-256-cfb", "aes-128-cfb", "chacha20-ietf", "none",
    ];

    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "shadowsocks",
        DisplayName = "Shadowsocks",
        Description = "AEAD-encrypted SOCKS tunnel, including the Shadowsocks-2022 ciphers.",
        Schemes = ["ss"],
        Engines = EngineSupport.Both,
        Order = 40,
        SupportsMux = true,
        Transports = [TransportKind.Raw, TransportKind.WebSocket, TransportKind.Grpc, TransportKind.HttpUpgrade],
        Security = [SecurityKind.None, SecurityKind.Tls],
        Fields =
        [
            new FieldSpec(FieldMethod, "Cipher", FieldKind.Choice)
            {
                Required = true,
                Default = "2022-blake3-aes-256-gcm",
                Choices = [.. Methods.Select(ChoiceOption.Of)],
            },
            new FieldSpec(FieldPassword, "Password", FieldKind.Secret)
            {
                Required = true,
                Help = "For Shadowsocks-2022 multi-user servers this is \"serverKey:userKey\".",
            },
            new FieldSpec(FieldPlugin, "SIP003 plugin")
            {
                Placeholder = "obfs-local, v2ray-plugin, ...",
            },
            new FieldSpec(FieldPluginOpts, "Plugin options")
            {
                Placeholder = "obfs=http;obfs-host=example.com",
                VisibleWhenKey = FieldPlugin,
            },
            new FieldSpec(FieldUot, "UDP over TCP", FieldKind.Toggle) { Default = "false" },
        ],
    };

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;
        var text = link.Trim();
        if (!text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not an ss:// link.";
            return false;
        }

        var body = text[5..];
        var hash = body.IndexOf('#');
        var remark = hash >= 0 ? Uri.UnescapeDataString(body[(hash + 1)..].Replace('+', ' ')) : "";
        if (hash >= 0) body = body[..hash];

        string query = "";
        var qm = body.IndexOf('?');
        if (qm >= 0)
        {
            query = body[(qm + 1)..];
            body = body[..qm];
        }
        body = body.TrimEnd('/');

        string method, password, host;
        int port;

        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: userinfo is base64(method:password), or occasionally plain.
            var userInfo = body[..at];
            var authority = body[(at + 1)..];
            var decoded = UriKit.TryDecodeBase64(userInfo) ?? Uri.UnescapeDataString(userInfo);
            if (!TrySplitCredentials(decoded, out method, out password))
            {
                error = "Could not read cipher and password from the link.";
                return false;
            }
            if (!UriKit.TrySplitHostPort(authority, out host, out port))
            {
                error = "Could not read host and port from the link.";
                return false;
            }
        }
        else
        {
            // Legacy: the whole body is base64(method:password@host:port).
            var decoded = UriKit.TryDecodeBase64(body);
            if (decoded is null)
            {
                error = "Legacy ss:// payload is not valid base64.";
                return false;
            }
            var split = decoded.LastIndexOf('@');
            if (split < 0 || !TrySplitCredentials(decoded[..split], out method, out password))
            {
                error = "Legacy ss:// payload is malformed.";
                return false;
            }
            if (!UriKit.TrySplitHostPort(decoded[(split + 1)..], out host, out port))
            {
                error = "Legacy ss:// payload has no usable host and port.";
                return false;
            }
        }

        var q = UriKit.ParseQuery(query);
        var (pluginName, pluginOpts) = SplitPlugin(StreamParamCodec.Value(q, "plugin"));

        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = remark,
            Server = host,
            Port = port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldMethod] = method,
                [FieldPassword] = password,
                [FieldPlugin] = pluginName ?? "",
                [FieldPluginOpts] = pluginOpts ?? "",
                [FieldUot] = StreamParamCodec.IsTruthy(StreamParamCodec.Value(q, "uot")) ? "true" : "false",
            },
            Transport = StreamParamCodec.ReadTransport(q),
            Security = StreamParamCodec.ReadSecurity(q),
            SourceLink = text,
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var userInfo = UriKit.EncodeBase64Url(
            $"{node.GetOr(FieldMethod, "")}:{node.GetOr(FieldPassword, "")}");

        var query = StreamParamCodec.WriteParams(node);
        var plugin = node.Get(FieldPlugin);
        if (!string.IsNullOrWhiteSpace(plugin))
        {
            var opts = node.Get(FieldPluginOpts);
            query.Add(new("plugin", string.IsNullOrWhiteSpace(opts) ? plugin : $"{plugin};{opts}"));
        }
        if (StreamParamCodec.IsTruthy(node.Get(FieldUot))) query.Add(new("uot", "1"));

        return Compose("ss", userInfo, node, query);
    }

    public override IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = base.Validate(node).ToList();
        var method = node.GetOr(FieldMethod, "");
        var password = node.GetOr(FieldPassword, "");

        if (method.StartsWith("2022-", StringComparison.Ordinal) && password.Length > 0)
        {
            // 2022 ciphers take base64 pre-shared keys of a cipher-determined length.
            var keys = password.Split(':');
            foreach (var key in keys)
            {
                if (UriKit.TryDecodeBase64(key) is null && !IsBase64Bytes(key))
                {
                    errors.Add("Shadowsocks-2022 keys must be base64-encoded pre-shared keys.");
                    break;
                }
            }
        }

        return errors;
    }

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        var method = node.GetOr(FieldMethod, "");
        var password = node.GetOr(FieldPassword, "");
        var plugin = node.Get(FieldPlugin);
        var pluginOpts = node.Get(FieldPluginOpts);
        var uot = StreamParamCodec.IsTruthy(node.Get(FieldUot));

        JsonObject outbound;
        if (engine == EngineKind.Xray)
        {
            var server = new JsonObject
            {
                ["method"] = method,
                ["password"] = password,
                ["level"] = 0,
            };
            server.SetIf("uot", uot);
            outbound = XrayServers("shadowsocks", node, ctx.Tag, server);
            if (!string.IsNullOrWhiteSpace(plugin))
                ctx.Warn("Xray does not run SIP003 plugins; use the sing-box engine for this server.");
        }
        else
        {
            outbound = SingBoxShell("shadowsocks", node, ctx.Tag);
            outbound["method"] = method;
            outbound["password"] = password;
            outbound.SetIf("plugin", plugin);
            outbound.SetIf("plugin_opts", pluginOpts);
            if (uot)
            {
                outbound["udp_over_tcp"] = new JsonObject { ["enabled"] = true, ["version"] = 2 };
            }
        }

        Decorate(outbound, node, engine, ctx);
        return outbound;
    }

    private static bool TrySplitCredentials(string value, out string method, out string password)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            method = "";
            password = "";
            return false;
        }
        method = value[..colon];
        password = value[(colon + 1)..];
        return true;
    }

    /// <summary>SIP003 packs the plugin as <c>name;opt=value;opt=value</c>.</summary>
    private static (string? Name, string? Opts) SplitPlugin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var semi = value.IndexOf(';');
        return semi < 0 ? (value, null) : (value[..semi], value[(semi + 1)..]);
    }

    private static bool IsBase64Bytes(string value) =>
        Convert.TryFromBase64String(value, new byte[value.Length], out _);
}
