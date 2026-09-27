using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;

namespace FCon.Core.Health;

public enum AdviceSeverity
{
    Info,
    Warning,
    Critical,
}

public sealed record Advice(
    string Id,
    AdviceSeverity Severity,
    string Title,
    string Detail)
{
    /// <summary>True when <see cref="ConfigAdvisor.ApplyFixes"/> can correct this without asking.</summary>
    public bool AutoFixable { get; init; }

    public string Grade => Severity switch
    {
        AdviceSeverity.Critical => "bad",
        AdviceSeverity.Warning => "warn",
        _ => "good",
    };
}

/// <summary>
/// Reviews the app settings, routing and the selected server, and reports what is
/// actually worth changing. Deliberately opinionated and short: a list nobody reads is
/// worse than three findings that matter.
/// </summary>
public static class ConfigAdvisor
{
    /// <summary>The Chinese public resolvers older clients ship as defaults.</summary>
    private static bool IsFarAwayResolver(string address) =>
        address.Contains("223.5.5.5") || address.Contains("223.6.6.6")
        || address.Contains("119.29.29.29") || address.Contains("dns.alidns.com") || address.Contains("doh.pub");

    /// <summary>Ciphers that predate AEAD and offer no integrity protection.</summary>
    private static readonly string[] LegacyShadowsocksCiphers =
        ["aes-256-cfb", "aes-128-cfb", "aes-192-cfb", "chacha20-ietf", "rc4-md5", "none", "plain"];

    public static IReadOnlyList<Advice> Inspect(
        AppSettings settings,
        RoutingProfile routing,
        ProxyNode? node)
    {
        var findings = new List<Advice>();

        InspectServer(node, findings);
        InspectSettings(settings, findings);
        InspectRouting(settings, routing, findings);

        return [.. findings.OrderByDescending(a => a.Severity)];
    }

    private static void InspectServer(ProxyNode? node, List<Advice> findings)
    {
        if (node is null) return;

        if (node.Security.AllowInsecure)
        {
            findings.Add(new Advice(
                "tls.insecure",
                AdviceSeverity.Critical,
                "Certificate checking is turned off",
                $"{node.DisplayName} accepts any TLS certificate, so anyone able to intercept the "
                + "connection can read it. This removes the protection TLS exists to provide. "
                + "Fix the certificate or the SNI instead of skipping verification."));
        }

        // A protocol with no transport encryption of its own, carried in clear.
        if (node.Security.Kind == SecurityKind.None
            && node.Protocol is "vmess" or "vless" or "trojan")
        {
            findings.Add(new Advice(
                "tls.none",
                AdviceSeverity.Critical,
                "Traffic is not encrypted in transport",
                $"{node.DisplayName} runs {node.Protocol.ToUpperInvariant()} without TLS or REALITY. "
                + "VLESS and Trojan carry no encryption of their own, so the contents are exposed "
                + "to anyone on the path."));
        }

        if (node.Protocol == "vmess")
        {
            if (int.TryParse(node.GetOr("aid", "0"), out var alterId) && alterId > 0)
            {
                findings.Add(new Advice(
                    "vmess.alterid",
                    AdviceSeverity.Warning,
                    "VMess is using legacy authentication",
                    $"Alter ID is {alterId}. The MD5-based scheme it enables was withdrawn years ago "
                    + "as insecure. Set it to 0 on both ends to use AEAD."));
            }

            var cipher = node.GetOr("scy", "auto").ToLowerInvariant();
            if (cipher is "none" or "zero")
            {
                findings.Add(new Advice(
                    "vmess.cipher",
                    AdviceSeverity.Warning,
                    "VMess payload encryption is disabled",
                    $"Encryption is set to \"{cipher}\". This is only safe underneath TLS, and "
                    + "pointless otherwise."));
            }
        }

        if (node.Protocol == "shadowsocks")
        {
            var method = node.GetOr("method", "").ToLowerInvariant();
            if (LegacyShadowsocksCiphers.Contains(method))
            {
                findings.Add(new Advice(
                    "ss.cipher",
                    AdviceSeverity.Warning,
                    "Shadowsocks is using a pre-AEAD cipher",
                    $"\"{method}\" provides no integrity protection and is detectable. Prefer "
                    + "2022-blake3-aes-256-gcm, or aes-256-gcm on older servers."));
            }
        }

        // Vision multiplexes internally; stacking mux on top costs throughput.
        if (node.Mux.Enabled && node.Get("flow") is { Length: > 0 } flow && flow.Contains("vision"))
        {
            findings.Add(new Advice(
                "mux.vision",
                AdviceSeverity.Warning,
                "Multiplexing is stacked on XTLS Vision",
                "Vision already splits streams itself. Running mux over it adds overhead and "
                + "usually reduces speed. Turn mux off for this server.")
            {
                AutoFixable = false,
            });
        }

        if (node.Security.Kind == SecurityKind.Reality
            && string.IsNullOrWhiteSpace(node.Security.Fingerprint))
        {
            findings.Add(new Advice(
                "reality.fingerprint",
                AdviceSeverity.Warning,
                "REALITY has no browser fingerprint set",
                "Without a uTLS fingerprint the handshake does not resemble a browser, which is "
                + "the point of REALITY. Set it to chrome."));
        }
    }

    private static void InspectSettings(AppSettings settings, List<Advice> findings)
    {
        if (settings.AllowLan)
        {
            findings.Add(new Advice(
                "lan.open",
                AdviceSeverity.Warning,
                "The proxy is reachable from your network",
                $"Ports {settings.SocksPort} and {settings.HttpPort} are bound to all interfaces with "
                + "no authentication, so any device on the network can route traffic through you. "
                + "Only leave this on for a network you control."));
        }

        if (!settings.VerifyOnConnect)
        {
            findings.Add(new Advice(
                "health.verify",
                AdviceSeverity.Warning,
                "Connections are not verified",
                "A proxy core binds its port whether or not the server works, so without "
                + "verification a dead server still looks connected.")
            {
                AutoFixable = true,
            });
        }

        if (!settings.ContinuousHealthCheck)
        {
            findings.Add(new Advice(
                "health.monitor",
                AdviceSeverity.Info,
                "The tunnel is not monitored while connected",
                "Nothing will notice if the link stops carrying traffic. Turning this on lets "
                + "FCon reconnect or move to another server on its own.")
            {
                AutoFixable = true,
            });
        }

        if (IsFarAwayResolver(settings.DirectDns))
        {
            findings.Add(new Advice(
                "dns.direct",
                AdviceSeverity.Warning,
                "Direct DNS depends on a resolver in China",
                $"The direct resolver is {settings.DirectDns}. The proxy server's own name is looked up "
                + "through it before the tunnel exists, so if that resolver is slow or blocked from "
                + "here, nothing connects. The system resolver has no such dependency.")
            {
                AutoFixable = true,
            });
        }

        if (settings is { InCoreFailover: false, AutoFailover: true })
        {
            findings.Add(new Advice(
                "failover.restart",
                AdviceSeverity.Info,
                "Failover restarts the core",
                "Servers are not grouped inside the core, so moving to another one means "
                + "stopping the core, which drops every open connection and briefly sends "
                + "traffic direct. Grouping lets the core swap servers in about a second.")
            {
                AutoFixable = true,
            });
        }

        if (settings is { EnableSniffing: false, RoutingMode: RoutingMode.Rules })
        {
            findings.Add(new Advice(
                "sniff.off",
                AdviceSeverity.Info,
                "Domain rules cannot match reliably",
                "Rules are on, but destination sniffing is off, so routing sees only IP addresses "
                + "and domain rules will mostly miss.")
            {
                AutoFixable = true,
            });
        }

        if (settings.LogLevel == "debug")
        {
            findings.Add(new Advice(
                "log.debug",
                AdviceSeverity.Info,
                "Debug logging is on",
                "Debug output is verbose enough to cost throughput, and records the domains you "
                + "visit. Use warning unless you are chasing a problem.")
            {
                AutoFixable = true,
            });
        }
    }

    private static void InspectRouting(AppSettings settings, RoutingProfile routing, List<Advice> findings)
    {
        if (settings.RoutingMode == RoutingMode.Rules && !routing.BypassPrivateNetworks)
        {
            findings.Add(new Advice(
                "route.private",
                AdviceSeverity.Warning,
                "Local network traffic goes through the tunnel",
                "Printers, NAS and router pages will be sent to the exit node, where they cannot "
                + "be reached. Bypassing private networks fixes that.")
            {
                AutoFixable = true,
            });
        }

        var geoRules = routing.Rules.Count(r =>
            r.Enabled && (r.Domains.Any(d => d.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                          || r.Ips.Any(i => i.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))));

        if (geoRules > 0 && settings.Engine == EngineKind.SingBox)
        {
            findings.Add(new Advice(
                "route.ruleset",
                AdviceSeverity.Info,
                "Connecting depends on downloading rule-sets",
                $"{geoRules} rule(s) use geosite:/geoip:. sing-box downloads those at startup and "
                + "refuses to start if it cannot, so a blocked GitHub means no connection at all. "
                + "Explicit domain and IP rules avoid the dependency."));
        }
    }

    /// <summary>
    /// Apply the findings that are safe to correct automatically. Returns what changed, so
    /// the UI can say what happened rather than silently rewriting the user's settings.
    /// </summary>
    public static IReadOnlyList<string> ApplyFixes(AppSettings settings, RoutingProfile routing)
    {
        var applied = new List<string>();

        if (!settings.VerifyOnConnect)
        {
            settings.VerifyOnConnect = true;
            applied.Add("Verify connections before reporting success");
        }

        if (!settings.ContinuousHealthCheck)
        {
            settings.ContinuousHealthCheck = true;
            applied.Add("Monitor the tunnel while connected");
        }

        if (settings is { InCoreFailover: false, AutoFailover: true })
        {
            settings.InCoreFailover = true;
            applied.Add("Switch servers inside the core instead of restarting it");
        }

        if (IsFarAwayResolver(settings.DirectDns))
        {
            settings.DirectDns = "local";
            applied.Add("Use the system resolver for direct traffic and the server's own name");
        }

        if (settings is { EnableSniffing: false, RoutingMode: RoutingMode.Rules })
        {
            settings.EnableSniffing = true;
            applied.Add("Enable destination sniffing so domain rules match");
        }

        if (settings.LogLevel == "debug")
        {
            settings.LogLevel = "warning";
            applied.Add("Reduce log level to warning");
        }

        if (settings.RoutingMode == RoutingMode.Rules && !routing.BypassPrivateNetworks)
        {
            routing.BypassPrivateNetworks = true;
            applied.Add("Keep local network traffic off the tunnel");
        }

        return applied;
    }
}
