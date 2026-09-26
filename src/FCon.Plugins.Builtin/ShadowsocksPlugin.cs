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
                Placeholder = "obfs-local, v2ray-plugin, shadow-tls, ...",
                Help = "\"shadow-tls\" wraps the stream in a real TLS handshake against a decoy site (sing-box only).",
            },
            new FieldSpec(FieldPluginOpts, "Plugin options")
            {
                Placeholder = "obfs=http;obfs-host=example.com  |  host=decoy.example.com;password=...;version=3",
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

        if (IsShadowTls(node.Get(FieldPlugin)))
        {
            var opts = ParsePluginOpts(node.Get(FieldPluginOpts));
            if (!opts.ContainsKey("password"))
                errors.Add("shadow-tls needs a password in the plugin options, e.g. host=decoy.example.com;password=...;version=3.");
            if (!opts.ContainsKey("host"))
                errors.Add("shadow-tls needs the decoy host (host=...) in the plugin options.");
        }

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

            if (IsShadowTls(plugin))
            {
                // sing-box models ShadowTLS as its own outbound that the Shadowsocks
                // stream detours through, not as a SIP003 plugin.
                outbound["detour"] = EmitShadowTls(node, pluginOpts, ctx);
            }
            else
            {
                outbound.SetIf("plugin", plugin);
                outbound.SetIf("plugin_opts", pluginOpts);
            }

            if (uot)
            {
                outbound["udp_over_tcp"] = new JsonObject { ["enabled"] = true, ["version"] = 2 };
            }
        }

        Decorate(outbound, node, engine, ctx);
        return outbound;
    }

    public static bool IsShadowTls(string? plugin) =>
        plugin is not null
        && (plugin.Equals("shadow-tls", StringComparison.OrdinalIgnoreCase)
            || plugin.Equals("shadowtls", StringComparison.OrdinalIgnoreCase));

    /// <summary>Emit the shadowtls outbound and return its tag for the detour.</summary>
    private static string EmitShadowTls(ProxyNode node, string? pluginOpts, IEmitContext ctx)
    {
        var opts = ParsePluginOpts(pluginOpts);
        var tag = ctx.Tag + "-stls";

        var version = opts.TryGetValue("version", out var v) && int.TryParse(v, out var n) ? n : 3;
        var fingerprint = opts.TryGetValue("fingerprint", out var fp) ? fp
            : opts.TryGetValue("fp", out fp) ? fp
            : node.Security.Fingerprint is { Length: > 0 } f ? f
            : "chrome";

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint },
        };
        tls.SetIf("server_name", opts.TryGetValue("host", out var host) ? host : node.Security.ServerName);
        if (opts.TryGetValue("alpn", out var alpn) && alpn.Length > 0)
            tls["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => (JsonNode)a).ToArray());

        var shadowTls = SingBoxShell("shadowtls", node, tag);
        shadowTls["version"] = version;
        if (version >= 2) shadowTls["password"] = opts.TryGetValue("password", out var pw) ? pw : "";
        shadowTls["tls"] = tls;

        ctx.EmitAuxiliary(shadowTls);
        return tag;
    }

    /// <summary>SIP003 option syntax: <c>key=value;key=value</c>. Keys are case-insensitive.</summary>
    internal static Dictionary<string, string> ParsePluginOpts(string? opts)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(opts)) return map;
        foreach (var part in opts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) map[part] = "";
            else map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
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
