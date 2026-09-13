using System.Text.Json.Nodes;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Plugins.Builtin;

/// <summary>
/// Shared scaffolding for the built-in plugins: link splitting, common validation and
/// the two outbound skeletons (Xray vnext/servers, sing-box flat) that every protocol
/// in the 3x-ui matrix fits into.
/// </summary>
public abstract class ProtocolPluginBase : IProtocolPlugin
{
    public abstract ProtocolDescriptor Descriptor { get; }

    public abstract bool TryParseLink(string link, out ProxyNode? node, out string? error);

    public abstract string BuildLink(ProxyNode node);

    public abstract JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx);

    public virtual IReadOnlyList<string> Validate(ProxyNode node)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(node.Server))
        {
            errors.Add("Server address is empty.");
        }
        else if (DescribeBadAddress(node.Server) is { } addressProblem)
        {
            errors.Add(addressProblem);
        }

        if (node.Port is <= 0 or > 65535) errors.Add($"Port {node.Port} is out of range.");

        foreach (var field in Descriptor.Fields.Where(f => f.Required))
        {
            if (!IsFieldVisible(field, node)) continue;
            if (string.IsNullOrWhiteSpace(node.Get(field.Key)))
                errors.Add($"{field.Label} is required.");
        }

        if (node.Security.Kind == SecurityKind.Reality)
        {
            if (string.IsNullOrWhiteSpace(node.Security.PublicKey))
                errors.Add("REALITY requires a public key (pbk).");
            if (string.IsNullOrWhiteSpace(node.Security.ServerName))
                errors.Add("REALITY requires an SNI.");
        }

        if (!Descriptor.Transports.Contains(node.Transport.Kind) && Descriptor.Transports.Count > 0)
            errors.Add($"{Descriptor.DisplayName} does not support the {node.Transport.Kind} transport.");

        return errors;
    }

    /// <summary>
    /// Catches the common paste mistakes in the address box — a whole share link, a URL with
    /// a scheme or path, or stray whitespace — rather than letting them reach the core as a
    /// hostname that can never resolve.
    /// </summary>
    private static string? DescribeBadAddress(string server)
    {
        var value = server.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
            return "Address must be a hostname or IP, not a full link. Use \"Paste link\" to import one.";

        if (value.Any(char.IsWhiteSpace))
            return "Address must not contain spaces.";

        if (value.Contains('/'))
            return "Address must not contain a path.";

        if (value.Contains('@'))
            return "Address must not contain credentials.";

        // A bare colon means a port was typed into the address; bracketed IPv6 is fine.
        if (!value.StartsWith('[') && value.Count(c => c == ':') == 1)
            return "Enter the port in the Port box, not in the address.";

        return null;
    }

    /// <summary>Evaluates a field's <c>VisibleWhen</c> guard against a node.</summary>
    public static bool IsFieldVisible(FieldSpec field, ProxyNode node)
    {
        if (field.VisibleWhenKey is null) return true;
        var current = node.GetOr(field.VisibleWhenKey, "");
        return field.VisibleWhenValues.Contains(current, StringComparer.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------- link helpers

    /// <summary>Split a link and confirm the scheme belongs to this plugin.</summary>
    protected bool TrySplitOwn(string link, out LinkParts parts, out string? error)
    {
        error = null;
        if (!UriKit.TrySplit(link, out parts))
        {
            error = "Link is not a well-formed URI.";
            return false;
        }
        if (!Descriptor.Schemes.Contains(parts.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Scheme \"{parts.Scheme}\" is not handled by the {Descriptor.DisplayName} plugin.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parts.Host))
        {
            error = "Link has no server address.";
            return false;
        }
        return true;
    }

    /// <summary>Compose <c>scheme://userinfo@host:port?query#remark</c>.</summary>
    protected static string Compose(
        string scheme,
        string userInfo,
        ProxyNode node,
        IEnumerable<KeyValuePair<string, string?>> query)
    {
        var q = UriKit.BuildQuery(query);
        var host = UriKit.FormatHost(node.Server);
        var user = string.IsNullOrEmpty(userInfo) ? "" : userInfo + "@";
        var tail = q.Length > 0 ? "?" + q : "";
        var frag = string.IsNullOrWhiteSpace(node.Remark)
            ? ""
            : "#" + Uri.EscapeDataString(node.Remark);
        return $"{scheme}://{user}{host}:{node.Port}{tail}{frag}";
    }

    // -------------------------------------------------------- outbound shells

    /// <summary>
    /// Xray outbound whose settings use the <c>vnext</c> shape (VLESS, VMess).
    /// <paramref name="user"/> is appended to the single vnext entry.
    /// </summary>
    protected static JsonObject XrayVnext(string protocol, ProxyNode node, string tag, JsonObject user)
    {
        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = protocol,
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = node.Server,
                    ["port"] = node.Port,
                    ["users"] = new JsonArray(user),
                }),
            },
        };
    }

    /// <summary>Xray outbound whose settings use the <c>servers</c> shape (Trojan, SS, SOCKS, HTTP).</summary>
    protected static JsonObject XrayServers(string protocol, ProxyNode node, string tag, JsonObject server)
    {
        server["address"] = node.Server;
        server["port"] = node.Port;
        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = protocol,
            ["settings"] = new JsonObject { ["servers"] = new JsonArray(server) },
        };
    }

    /// <summary>sing-box outbounds are flat: type, tag, server, server_port, then credentials.</summary>
    protected static JsonObject SingBoxShell(string type, ProxyNode node, string tag) => new()
    {
        ["type"] = type,
        ["tag"] = tag,
        ["server"] = node.Server,
        ["server_port"] = node.Port,
    };

    /// <summary>Apply transport, TLS and mux through the host-supplied emitters.</summary>
    protected static void Decorate(JsonObject outbound, ProxyNode node, EngineKind engine, IEmitContext ctx)
    {
        ctx.ApplyTransport(outbound, node, engine);
        ctx.ApplySecurity(outbound, node, engine);
        ctx.ApplyMux(outbound, node, engine);
    }
}
