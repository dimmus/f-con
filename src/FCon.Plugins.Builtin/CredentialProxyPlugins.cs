using System.Text.Json.Nodes;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// Shared implementation for the two username/password proxies in the 3x-ui set,
/// SOCKS5 and HTTP CONNECT. They differ only in scheme names and outbound type.
/// </summary>
public abstract class CredentialProxyPluginBase : ProtocolPluginBase
{
    public const string FieldUser = "user";
    public const string FieldPassword = "pass";

    protected static IReadOnlyList<FieldSpec> CredentialFields =>
    [
        new FieldSpec(FieldUser, "Username") { Placeholder = "leave empty for no auth" },
        new FieldSpec(FieldPassword, "Password", FieldKind.Secret),
    ];

    public override bool TryParseLink(string link, out ProxyNode? node, out string? error)
    {
        node = null;
        if (!TrySplitOwn(link, out var parts, out error)) return false;

        var (user, password) = ReadCredentials(parts.UserInfo);
        var q = parts.QueryMap;

        node = new ProxyNode
        {
            Protocol = Descriptor.Id,
            Remark = parts.Fragment,
            Server = parts.Host,
            Port = parts.Port,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [FieldUser] = user,
                [FieldPassword] = password,
            },
            Transport = TransportOptions.Default,
            Security = StreamParamCodec.ReadSecurity(q),
            SourceLink = link.Trim(),
        };
        return true;
    }

    public override string BuildLink(ProxyNode node)
    {
        var user = node.GetOr(FieldUser, "");
        var pass = node.GetOr(FieldPassword, "");
        var userInfo = string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass)
            ? ""
            : UriKit.EncodeBase64Url($"{user}:{pass}");

        var query = new List<KeyValuePair<string, string?>>();
        if (node.Security.Kind != SecurityKind.None)
        {
            query.Add(new("security", "tls"));
            query.Add(new("sni", node.Security.ServerName));
            if (node.Security.AllowInsecure) query.Add(new("allowInsecure", "1"));
        }

        return Compose(Descriptor.Schemes[0], userInfo, node, query);
    }

    /// <summary>Userinfo is either base64("user:pass") or a plain "user:pass" pair.</summary>
    private static (string User, string Password) ReadCredentials(string userInfo)
    {
        if (string.IsNullOrWhiteSpace(userInfo)) return ("", "");

        var decoded = UriKit.TryDecodeBase64(userInfo);
        var text = decoded is not null && decoded.Contains(':')
            ? decoded
            : Uri.UnescapeDataString(userInfo);

        var colon = text.IndexOf(':');
        return colon < 0 ? (text, "") : (text[..colon], text[(colon + 1)..]);
    }

    /// <summary>Xray expresses SOCKS and HTTP credentials as a single-entry users array.</summary>
    protected JsonObject EmitXray(string protocol, ProxyNode node, IEmitContext ctx)
    {
        var server = new JsonObject();
        var user = node.Get(FieldUser);
        if (!string.IsNullOrWhiteSpace(user))
        {
            server["users"] = new JsonArray(new JsonObject
            {
                ["user"] = user,
                ["pass"] = node.GetOr(FieldPassword, ""),
                ["level"] = 0,
            });
        }
        return XrayServers(protocol, node, ctx.Tag, server);
    }

    protected JsonObject EmitSingBox(string type, ProxyNode node, IEmitContext ctx)
    {
        var outbound = SingBoxShell(type, node, ctx.Tag);
        var user = node.Get(FieldUser);
        if (!string.IsNullOrWhiteSpace(user))
        {
            outbound["username"] = user;
            outbound["password"] = node.GetOr(FieldPassword, "");
        }
        return outbound;
    }
}

/// <summary>SOCKS5 outbound.</summary>
public sealed class SocksPlugin : CredentialProxyPluginBase
{
    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "socks",
        DisplayName = "SOCKS5",
        Description = "Plain SOCKS5 proxy with optional username/password authentication.",
        Schemes = ["socks", "socks5"],
        Engines = EngineSupport.Both,
        Order = 50,
        Transports = [TransportKind.Raw],
        Security = [SecurityKind.None, SecurityKind.Tls],
        Fields = CredentialFields,
    };

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        JsonObject outbound;
        if (engine == EngineKind.Xray)
        {
            outbound = EmitXray("socks", node, ctx);
        }
        else
        {
            outbound = EmitSingBox("socks", node, ctx);
            outbound["version"] = "5";
        }

        ctx.ApplySecurity(outbound, node, engine);
        return outbound;
    }
}

/// <summary>HTTP CONNECT outbound.</summary>
public sealed class HttpProxyPlugin : CredentialProxyPluginBase
{
    public override ProtocolDescriptor Descriptor { get; } = new()
    {
        Id = "http",
        DisplayName = "HTTP",
        Description = "HTTP CONNECT proxy. TCP only — it cannot carry UDP traffic.",
        Schemes = ["http-proxy", "https-proxy"],
        Engines = EngineSupport.Both,
        Order = 60,
        Transports = [TransportKind.Raw],
        Security = [SecurityKind.None, SecurityKind.Tls],
        Fields = CredentialFields,
    };

    public override JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        var outbound = engine == EngineKind.Xray
            ? EmitXray("http", node, ctx)
            : EmitSingBox("http", node, ctx);

        ctx.ApplySecurity(outbound, node, engine);
        return outbound;
    }
}
