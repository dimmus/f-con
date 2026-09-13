using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Plugins;

namespace FCon.Core.Engine;

/// <summary>
/// Builds a sing-box configuration on the 1.11+ schema: rule actions instead of the
/// retired block/dns outbounds, and binary rule-sets instead of the v2ray geo files.
/// </summary>
public sealed class SingBoxConfigBuilder(PluginRegistry registry)
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";

    private const string GeositeBase =
        "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-";
    private const string GeoipBase =
        "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-";

    public GeneratedConfig Build(ProxyNode node, AppSettings settings, RoutingProfile routing)
    {
        var plugin = registry.Require(node);
        var ctx = new EmitContext(ProxyTag);
        var proxy = plugin.EmitOutbound(node, EngineKind.SingBox, ctx);

        // Both the DNS rules and the route rules register rule-sets into this dictionary,
        // so every producer has to run before route.rule_set is materialised from it.
        var ruleSets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var dns = BuildDns(settings, routing, ruleSets);
        var routeRules = BuildRouteRules(settings, routing, ruleSets, ctx);

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = settings.RoutingMode == RoutingMode.Direct ? DirectTag : ProxyTag,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = new JsonObject { ["server"] = "local" },
        };
        if (ruleSets.Count > 0)
        {
            route["rule_set"] = new JsonArray(ruleSets.Values.Select(v => (JsonNode)v).ToArray());
            ctx.Warn($"{ruleSets.Count} geosite/geoip rule-set(s) are downloaded at startup, and "
                     + "sing-box refuses to start if a download fails - so a rule using geosite: "
                     + "or geoip: makes connecting depend on reaching GitHub. Explicit domain and "
                     + "IP rules avoid that, and avoid a mechanism sing-box removes in 1.16.");
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = MapLogLevel(settings.LogLevel),
                ["timestamp"] = true,
            },
            ["dns"] = dns,
            ["inbounds"] = BuildInbounds(settings),
            ["route"] = route,
            ["experimental"] = BuildExperimental(settings),
        };

        // WireGuard is an endpoint rather than an outbound from sing-box 1.11 onward.
        if (ctx.IsEndpoint)
        {
            root["endpoints"] = new JsonArray(proxy);
            root["outbounds"] = new JsonArray(BuildDirectOutbound());
        }
        else
        {
            root["outbounds"] = new JsonArray(proxy, BuildDirectOutbound());
        }

        return new GeneratedConfig(root, ctx.Warnings);
    }

    /// <summary>
    /// The direct outbound. It carries an explicit resolver rather than being a bare
    /// {type, tag} pair: sing-box 1.15 refuses a <c>detour</c> that points at an "empty"
    /// direct outbound, and the DNS servers below rely on exactly that detour to stay off
    /// the tunnel. Bootstrap is a plain IP-literal server, so naming it here is cycle-free.
    /// </summary>
    private static JsonObject BuildDirectOutbound() => new()
    {
        ["type"] = "direct",
        ["tag"] = DirectTag,
        ["domain_resolver"] = "bootstrap",
    };

    // ------------------------------------------------------------- inbounds

    private static JsonArray BuildInbounds(AppSettings settings)
    {
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = settings.ListenAddress,
                ["listen_port"] = settings.SocksPort,
            },
        };

        // A second listener so apps hard-coded to an HTTP proxy port keep working.
        if (settings.HttpPort > 0 && settings.HttpPort != settings.SocksPort)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "http-in",
                ["listen"] = settings.ListenAddress,
                ["listen_port"] = settings.HttpPort,
            });
        }

        if (settings.TrafficMode == TrafficMode.Tun)
        {
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = settings.TunInterfaceName,
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["mtu"] = settings.TunMtu,
                ["auto_route"] = true,
                ["strict_route"] = settings.TunStrictRoute,
                ["stack"] = settings.TunStack,
            };
            inbounds.Add(tun);
        }

        return inbounds;
    }

    // ---------------------------------------------------------------- route

    private JsonArray BuildRouteRules(
        AppSettings settings,
        RoutingProfile routing,
        Dictionary<string, JsonObject> ruleSets,
        EmitContext ctx)
    {
        var rules = new JsonArray();

        // Sniffing is a rule action from 1.11 onward, and must run before anything
        // that matches on a domain.
        if (settings.EnableSniffing)
            rules.Add(new JsonObject { ["action"] = "sniff" });

        // Under TUN, DNS queries must be answered locally rather than forwarded raw.
        if (settings.TrafficMode == TrafficMode.Tun)
        {
            rules.Add(new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns",
            });
        }

        if (settings.RoutingMode != RoutingMode.Rules) return rules;

        if (routing.BlockQuic)
        {
            rules.Add(new JsonObject
            {
                ["network"] = "udp",
                ["port"] = new JsonArray(443),
                ["action"] = "reject",
            });
        }

        if (routing.BlockAds)
            rules.Add(Reject(GeositeRule("category-ads-all", ruleSets)));

        if (routing.BypassPrivateNetworks)
        {
            rules.Add(new JsonObject
            {
                ["ip_is_private"] = true,
                ["outbound"] = DirectTag,
            });
        }

        if (routing.BypassMicrosoftServices)
        {
            var r = GeositeRule("microsoft", ruleSets);
            r["outbound"] = DirectTag;
            rules.Add(r);
        }

        foreach (var rule in routing.Rules.Where(r => r.Enabled && !r.IsEmpty))
        {
            if (TranslateRule(rule, ruleSets, ctx) is { } translated)
                rules.Add(translated);
        }

        return rules;
    }

    private static JsonObject Reject(JsonObject rule)
    {
        rule["action"] = "reject";
        return rule;
    }

    private JsonObject? TranslateRule(
        RoutingRule rule,
        Dictionary<string, JsonObject> ruleSets,
        EmitContext ctx)
    {
        var o = new JsonObject();
        var setTags = ClassifyDomains(rule.Domains, ruleSets, o);

        var cidrs = new List<string>();
        var privateIp = false;
        foreach (var entry in rule.Ips)
        {
            var (prefix, value) = SplitPrefix(entry);
            if (prefix == "geoip")
            {
                if (value.Equals("private", StringComparison.OrdinalIgnoreCase)) privateIp = true;
                else setTags.Add(RegisterGeoip(value, ruleSets));
            }
            else
            {
                cidrs.Add(NormaliseCidr(value));
            }
        }

        if (cidrs.Count > 0) o["ip_cidr"] = Arr(cidrs);
        if (privateIp) o["ip_is_private"] = true;
        if (setTags.Count > 0) o["rule_set"] = Arr(setTags.Distinct());
        if (rule.Processes.Count > 0) o["process_name"] = Arr(rule.Processes);
        if (rule.Protocols.Count > 0) o["protocol"] = Arr(rule.Protocols);
        if (rule.Network is { Length: > 0 }) o["network"] = rule.Network;

        if (rule.Ports.Count > 0)
        {
            var ports = new JsonArray();
            var ranges = new JsonArray();
            foreach (var p in rule.Ports)
            {
                if (p.Contains('-')) ranges.Add(p.Replace("-", ":"));
                else if (int.TryParse(p, out var n)) ports.Add(n);
            }
            if (ports.Count > 0) o["port"] = ports;
            if (ranges.Count > 0) o["port_range"] = ranges;
        }

        if (o.Count == 0)
        {
            ctx.Warn($"Routing rule \"{rule.Name}\" had nothing sing-box could match on and was skipped.");
            return null;
        }

        if (rule.Action == RuleAction.Block) o["action"] = "reject";
        else o["outbound"] = rule.Action == RuleAction.Proxy ? ProxyTag : DirectTag;

        return o;
    }

    /// <summary>
    /// Sort Xray-style domain entries into the four match fields sing-box exposes, writing
    /// them onto <paramref name="target"/> and returning the rule-set tags that were needed.
    /// Shared by the route and DNS rule builders so both honour the same prefixes.
    /// </summary>
    private static List<string> ClassifyDomains(
        IEnumerable<string> entries,
        Dictionary<string, JsonObject> ruleSets,
        JsonObject target)
    {
        var setTags = new List<string>();
        var suffixes = new List<string>();
        var exact = new List<string>();
        var keywords = new List<string>();
        var regexes = new List<string>();

        foreach (var entry in entries)
        {
            var (prefix, value) = SplitPrefix(entry);
            switch (prefix)
            {
                case "geosite": setTags.Add(RegisterGeosite(value, ruleSets)); break;
                case "full": exact.Add(value); break;
                case "keyword": keywords.Add(value); break;
                case "regexp":
                case "regex": regexes.Add(value); break;
                default: suffixes.Add(value); break;
            }
        }

        if (suffixes.Count > 0) target["domain_suffix"] = Arr(suffixes);
        if (exact.Count > 0) target["domain"] = Arr(exact);
        if (keywords.Count > 0) target["domain_keyword"] = Arr(keywords);
        if (regexes.Count > 0) target["domain_regex"] = Arr(regexes);

        return setTags;
    }

    // ------------------------------------------------------------ rule sets

    private static string RegisterGeosite(string name, Dictionary<string, JsonObject> sets)
    {
        var tag = $"geosite-{name.ToLowerInvariant()}";
        sets.TryAdd(tag, RemoteRuleSet(tag, GeositeBase + name.ToLowerInvariant() + ".srs"));
        return tag;
    }

    private static string RegisterGeoip(string name, Dictionary<string, JsonObject> sets)
    {
        var tag = $"geoip-{name.ToLowerInvariant()}";
        sets.TryAdd(tag, RemoteRuleSet(tag, GeoipBase + name.ToLowerInvariant() + ".srs"));
        return tag;
    }

    // download_detour was deprecated in sing-box 1.14 and is fatal in 1.15; rule-sets
    // now download over the default route.
    private static JsonObject RemoteRuleSet(string tag, string url) => new()
    {
        ["type"] = "remote",
        ["tag"] = tag,
        ["format"] = "binary",
        ["url"] = url,
        ["update_interval"] = "72h",
    };

    private static JsonObject GeositeRule(string name, Dictionary<string, JsonObject> sets) =>
        new() { ["rule_set"] = new JsonArray(RegisterGeosite(name, sets)) };

    // ------------------------------------------------------------------ dns

    private JsonObject BuildDns(
        AppSettings settings,
        RoutingProfile routing,
        Dictionary<string, JsonObject> ruleSets)
    {
        var servers = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "remote",
                ["type"] = DnsType(settings.RemoteDns),
                ["server"] = DnsHost(settings.RemoteDns),
                ["path"] = DnsPath(settings.RemoteDns),
                ["domain_resolver"] = "bootstrap",
                ["detour"] = ProxyTag,
            },
            new JsonObject
            {
                ["tag"] = "local",
                ["type"] = DnsType(settings.DirectDns),
                ["server"] = DnsHost(settings.DirectDns),
                ["path"] = DnsPath(settings.DirectDns),
                ["domain_resolver"] = "bootstrap",
                ["detour"] = DirectTag,
            },
            new JsonObject
            {
                ["tag"] = "bootstrap",
                ["type"] = "udp",
                ["server"] = settings.BootstrapDns,
                ["detour"] = DirectTag,
            },
        };

        foreach (var server in servers.OfType<JsonObject>().ToList())
        {
            if (server["path"] is null || server["path"]!.GetValue<string>().Length == 0)
                server.Remove("path");
        }

        // No geosite rule for private networks: the ip_is_private route rule already
        // covers the addresses, and pulling in a rule-set here would give the default
        // config a network dependency it has to satisfy before it can start.
        var rules = new JsonArray();

        // Domains routed direct must also resolve through the direct resolver, or the
        // answer comes from the exit node and the routing decision is undone.
        foreach (var rule in routing.Rules.Where(r => r.Enabled && r.Action == RuleAction.Direct))
        {
            if (rule.Domains.Count == 0) continue;

            var o = new JsonObject { ["server"] = "local" };
            var setTags = ClassifyDomains(rule.Domains, ruleSets, o);
            if (setTags.Count > 0) o["rule_set"] = Arr(setTags.Distinct());
            if (o.Count > 1) rules.Add(o);
        }

        if (settings.FakeIp)
        {
            servers.Add(new JsonObject
            {
                ["tag"] = "fakeip",
                ["type"] = "fakeip",
                ["inet4_range"] = "198.18.0.0/15",
                ["inet6_range"] = "fc00::/18",
            });
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray("A", "AAAA"),
                ["server"] = "fakeip",
            });
        }

        // independent_cache was deprecated in 1.14 and is fatal from 1.15; caching is
        // per-server by default now, so there is nothing to replace it with.
        return new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = "remote",
            ["strategy"] = "prefer_ipv4",
        };
    }

    private static string DnsType(string address)
    {
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "https";
        if (address.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)) return "tls";
        if (address.StartsWith("quic://", StringComparison.OrdinalIgnoreCase)) return "quic";
        if (address.StartsWith("h3://", StringComparison.OrdinalIgnoreCase)) return "h3";
        if (address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) return "tcp";
        return "udp";
    }

    private static string DnsHost(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme < 0 ? address : address[(scheme + 3)..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }

    private static string DnsPath(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        var rest = address[(scheme + 3)..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? "" : rest[slash..];
    }

    // --------------------------------------------------------- experimental

    private static JsonObject BuildExperimental(AppSettings settings)
    {
        var experimental = new JsonObject
        {
            ["cache_file"] = new JsonObject
            {
                ["enabled"] = true,
                ["path"] = Path.Combine(AppPaths.RuntimeDirectory, "cache.db"),
            },
        };

        if (settings.ApiPort > 0)
        {
            experimental["clash_api"] = new JsonObject
            {
                ["external_controller"] = $"127.0.0.1:{settings.ApiPort}",
                ["default_mode"] = "rule",
            };
        }

        return experimental;
    }

    // -------------------------------------------------------------- helpers

    private static string MapLogLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" => "debug",
        "info" => "info",
        "warning" or "warn" => "warn",
        "error" => "error",
        "none" => "panic",
        _ => "warn",
    };

    /// <summary>Split an Xray-style <c>prefix:value</c> entry; bare values get an empty prefix.</summary>
    private static (string Prefix, string Value) SplitPrefix(string entry)
    {
        var idx = entry.IndexOf(':');
        return idx <= 0
            ? ("", entry.Trim())
            : (entry[..idx].Trim().ToLowerInvariant(), entry[(idx + 1)..].Trim());
    }

    /// <summary>sing-box wants explicit prefix lengths; a bare IP is a /32 or /128.</summary>
    private static string NormaliseCidr(string value)
    {
        if (value.Contains('/')) return value;
        return value.Contains(':') ? value + "/128" : value + "/32";
    }

    private static JsonArray Arr(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)v).ToArray());
}
