using System.Text.Json.Nodes;
using FCon.Abstractions.Model;

namespace FCon.Abstractions.Emit;

/// <summary>Translates <see cref="SecurityOptions"/> into Xray or sing-box TLS blocks.</summary>
public static class SecurityEmitter
{
    /// <summary>uTLS fingerprints both cores accept.</summary>
    public static readonly string[] Fingerprints =
    [
        "chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized",
    ];

    public static SecurityKind ParseKind(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "tls" or "xtls" => SecurityKind.Tls,
        "reality" => SecurityKind.Reality,
        _ => SecurityKind.None,
    };

    public static string KindName(SecurityKind kind) => kind switch
    {
        SecurityKind.Tls => "tls",
        SecurityKind.Reality => "reality",
        _ => "none",
    };

    // ---------------------------------------------------------------- Xray

    public static void ApplyXray(JsonObject outbound, ProxyNode node)
    {
        var s = node.Security;
        var ss = outbound["streamSettings"] as JsonObject ?? new JsonObject();
        outbound["streamSettings"] = ss;
        ss["security"] = KindName(s.Kind);

        switch (s.Kind)
        {
            case SecurityKind.Tls:
                {
                    var tls = new JsonObject();
                    tls.SetIf("serverName", s.ServerName ?? DeriveSni(node));
                    tls.SetIf("allowInsecure", s.AllowInsecure);
                    tls.SetIf("fingerprint", s.Fingerprint);
                    tls.SetIf("disableSystemRoot", s.DisableSystemRoot);
                    if (s.Alpn.Count > 0)
                        tls["alpn"] = new JsonArray(s.Alpn.Select(a => (JsonNode)a!).ToArray());
                    if (!string.IsNullOrWhiteSpace(s.PinnedPeerCertificate))
                    {
                        tls["certificates"] = new JsonArray(new JsonObject
                        {
                            ["usage"] = "verify",
                            ["certificate"] = new JsonArray(
                                SplitPem(s.PinnedPeerCertificate).Select(l => (JsonNode)l!).ToArray()),
                        });
                    }
                    ss["tlsSettings"] = tls;
                    break;
                }

            case SecurityKind.Reality:
                {
                    var reality = new JsonObject { ["show"] = false };
                    reality.SetIf("serverName", s.ServerName);
                    reality.SetIf("fingerprint", string.IsNullOrWhiteSpace(s.Fingerprint) ? "chrome" : s.Fingerprint);
                    reality.SetIf("publicKey", s.PublicKey);
                    reality.SetIf("shortId", s.ShortId);
                    reality.SetIf("spiderX", s.SpiderX);
                    ss["realitySettings"] = reality;
                    break;
                }
        }
    }

    // ------------------------------------------------------------ sing-box

    /// <summary>Returns a warning when an option cannot be represented in sing-box.</summary>
    public static string? ApplySingBox(JsonObject outbound, ProxyNode node)
    {
        var s = node.Security;
        if (s.Kind == SecurityKind.None) return null;

        var tls = new JsonObject { ["enabled"] = true };
        tls.SetIf("server_name", s.ServerName ?? DeriveSni(node));
        tls.SetIf("insecure", s.AllowInsecure);
        if (s.Alpn.Count > 0)
            tls["alpn"] = new JsonArray(s.Alpn.Select(a => (JsonNode)a!).ToArray());

        if (!string.IsNullOrWhiteSpace(s.Fingerprint))
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = s.Fingerprint,
            };
        }

        string? warning = null;

        if (s.Kind == SecurityKind.Reality)
        {
            // REALITY in sing-box requires uTLS; force it on when the link omitted a fingerprint.
            tls["utls"] ??= new JsonObject { ["enabled"] = true, ["fingerprint"] = "chrome" };

            var reality = new JsonObject { ["enabled"] = true };
            reality.SetIf("public_key", s.PublicKey);
            reality.SetIf("short_id", s.ShortId);
            tls["reality"] = reality;

            if (!string.IsNullOrWhiteSpace(s.SpiderX))
                warning = "sing-box ignores the REALITY spiderX parameter.";
        }

        if (!string.IsNullOrWhiteSpace(s.PinnedPeerCertificate))
            tls["certificate"] = s.PinnedPeerCertificate;

        outbound["tls"] = tls;
        return warning;
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// When a link omits SNI, fall back to the transport Host header and finally to the
    /// server address — matching what the mainstream clients do.
    /// </summary>
    public static string DeriveSni(ProxyNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Security.ServerName)) return node.Security.ServerName!;
        var host = TransportEmitter.SplitHosts(node.Transport.Host).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(host)) return host;
        return node.Server;
    }

    private static IEnumerable<string> SplitPem(string pem) =>
        pem.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Translates <see cref="MuxOptions"/> for either core.</summary>
public static class MuxEmitter
{
    public static void ApplyXray(JsonObject outbound, MuxOptions m)
    {
        if (!m.Enabled) return;
        outbound["mux"] = new JsonObject
        {
            ["enabled"] = true,
            ["concurrency"] = m.Concurrency,
            ["xudpConcurrency"] = m.XudpConcurrency,
            ["xudpProxyUDP443"] = m.XudpProxyUdp443,
        };
    }

    public static void ApplySingBox(JsonObject outbound, MuxOptions m)
    {
        if (!m.Enabled) return;
        outbound["multiplex"] = new JsonObject
        {
            ["enabled"] = true,
            ["protocol"] = m.Protocol,
            ["max_streams"] = m.Concurrency,
            ["padding"] = m.Padding,
        };
    }
}
