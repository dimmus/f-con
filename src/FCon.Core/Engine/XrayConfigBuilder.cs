using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Plugins;

namespace FCon.Core.Engine;

/// <summary>
/// Builds a complete Xray-core configuration around one selected node.
///
/// With more than one server the extra ones ride along as additional outbounds, and
/// when automatic selection is asked for the routing points at a least-ping balancer
/// fed by Xray's observatory. Xray has no runtime switch equivalent to sing-box's
/// selector, so moving from a fixed server to the balancer means a config regeneration.
/// </summary>
public sealed class XrayConfigBuilder(PluginRegistry registry)
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string AutoTag = "auto";
    private const string ApiTag = "api";

    /// <summary>Single-server config, as used by the preview pane and the smoke tool.</summary>
    public GeneratedConfig Build(ProxyNode node, AppSettings settings, RoutingProfile routing) =>
        Build(node, [node], settings, routing, engineVersion: null, autoSelect: false);

    public GeneratedConfig Build(
        ProxyNode primary,
        IReadOnlyList<ProxyNode> pool,
        AppSettings settings,
        RoutingProfile routing,
        string? engineVersion,
        bool autoSelect)
    {
        var warnings = new List<string>();
        var grouped = pool.Count > 1;
        var primaryTag = grouped ? PoolEmitter.TagFor(primary, []) : ProxyTag;

        var emitted = PoolEmitter.Emit(registry, primary, pool, EngineKind.Xray, engineVersion, primaryTag, warnings);
        if (grouped && emitted.Count == 1)
        {
            grouped = false;
            primaryTag = ProxyTag;
            emitted = PoolEmitter.Emit(registry, primary, [primary], EngineKind.Xray, engineVersion, primaryTag, []);
        }

        var useBalancer = grouped && autoSelect;
        var target = new ProxyTarget(useBalancer ? null : primaryTag, useBalancer ? AutoTag : null);

        var outbounds = new JsonArray();
        foreach (var e in emitted)
        {
            outbounds.Add(e.Outbound);
            foreach (var aux in e.Auxiliary) outbounds.Add(aux);
        }
        outbounds.Add(new JsonObject
        {
            ["tag"] = DirectTag,
            ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["domainStrategy"] = "UseIP" },
        });
        outbounds.Add(new JsonObject
        {
            ["tag"] = BlockTag,
            ["protocol"] = "blackhole",
            ["settings"] = new JsonObject { ["response"] = new JsonObject { ["type"] = "http" } },
        });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = settings.LogLevel },
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = outbounds,
            ["routing"] = BuildRouting(settings, routing, target),
            ["dns"] = BuildDns(settings, routing),
        };

        if (useBalancer)
        {
            // The observatory probes every "node-" outbound; the balancer picks the
            // one with the lowest measured round trip and falls back to the user's choice.
            root["observatory"] = new JsonObject
            {
                ["subjectSelector"] = new JsonArray(PoolEmitter.NodeTagPrefix),
                ["probeUrl"] = settings.LatencyTestUrl,
                ["probeInterval"] = $"{Math.Max(10, settings.HealthCheckIntervalSeconds)}s",
                ["enableConcurrency"] = true,
            };
            warnings.Add($"{emitted.Count} servers are in the balancer; Xray picks the fastest one itself.");
        }
        else if (grouped)
        {
            warnings.Add($"{emitted.Count} servers are in the config; a failure switches to Xray's balancer.");
        }

        if (settings.ApiPort > 0)
        {
            root["api"] = new JsonObject
            {
                ["tag"] = ApiTag,
                ["services"] = new JsonArray("StatsService"),
            };
            root["stats"] = new JsonObject();
            root["policy"] = new JsonObject
            {
                ["system"] = new JsonObject
                {
                    ["statsOutboundUplink"] = true,
                    ["statsOutboundDownlink"] = true,
                },
            };
        }

        return new GeneratedConfig(root, warnings)
        {
            Members = [.. emitted.Select(e => new PoolMember(e.Node, e.Tag))],
            PrimaryTag = primaryTag,
            SelectorTag = null,
            AutoTag = useBalancer ? AutoTag : null,
            StartsOnAuto = useBalancer,
        };
    }

    /// <summary>Where "the proxy" traffic goes: a fixed outbound or the balancer.</summary>
    private sealed record ProxyTarget(string? OutboundTag, string? BalancerTag)
    {
        public void Apply(JsonObject rule)
        {
            if (BalancerTag is not null) rule["balancerTag"] = BalancerTag;
            else rule["outboundTag"] = OutboundTag;
        }
    }

    private static JsonArray BuildInbounds(AppSettings settings)
    {
        var inbounds = new JsonArray();

        if (settings.TrafficMode == TrafficMode.Tun)
        {
            // Xray has no TUN of its own; the app pairs it with a system-wide redirector.
            // The SOCKS listener below is what that redirector forwards into.
        }

        inbounds.Add(new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = settings.ListenAddress,
            ["port"] = settings.SocksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
            ["sniffing"] = Sniffing(settings),
        });

        inbounds.Add(new JsonObject
        {
            ["tag"] = "http-in",
            ["listen"] = settings.ListenAddress,
            ["port"] = settings.HttpPort,
            ["protocol"] = "http",
            ["settings"] = new JsonObject { ["allowTransparent"] = false },
            ["sniffing"] = Sniffing(settings),
        });

        if (settings.ApiPort > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = ApiTag,
                ["listen"] = "127.0.0.1",
                ["port"] = settings.ApiPort,
                ["protocol"] = "dokodemo-door",
                ["settings"] = new JsonObject { ["address"] = "127.0.0.1" },
            });
        }

        return inbounds;
    }

    private static JsonObject Sniffing(AppSettings settings) => new()
    {
        ["enabled"] = settings.EnableSniffing,
        ["destOverride"] = new JsonArray("http", "tls", "quic"),
        ["routeOnly"] = settings.RouteByDomain,
    };

    private static JsonObject BuildRouting(AppSettings settings, RoutingProfile routing, ProxyTarget target)
    {
        var rules = new JsonArray();

        // The API inbound must never be routed anywhere but the API handler.
        if (settings.ApiPort > 0)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(ApiTag),
                ["outboundTag"] = ApiTag,
            });
        }

        if (settings.RoutingMode == RoutingMode.Rules)
        {
            if (routing.BlockQuic)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["network"] = "udp",
                    ["port"] = "443",
                    ["outboundTag"] = BlockTag,
                });
            }

            if (routing.BlockAds)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = new JsonArray("geosite:category-ads-all"),
                    ["outboundTag"] = BlockTag,
                });
            }

            if (routing.BypassPrivateNetworks)
            {
                // Match the sing-box preset: private domains as well as private addresses.
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = new JsonArray("geosite:private"),
                    ["outboundTag"] = DirectTag,
                });
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["ip"] = new JsonArray("geoip:private"),
                    ["outboundTag"] = DirectTag,
                });
            }

            if (routing.BypassMicrosoftServices)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = new JsonArray("geosite:microsoft"),
                    ["outboundTag"] = DirectTag,
                });
            }

            foreach (var rule in routing.Rules.Where(r => r.Enabled && !r.IsEmpty))
                rules.Add(TranslateRule(rule, target));
        }
        else if (settings.RoutingMode == RoutingMode.Direct)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = DirectTag,
            });
        }

        // Xray's default route is the first outbound, which is the primary server. When
        // the balancer is in charge, unmatched traffic has to be sent there explicitly.
        if (target.BalancerTag is not null && settings.RoutingMode != RoutingMode.Direct)
        {
            var catchAll = new JsonObject { ["type"] = "field", ["network"] = "tcp,udp" };
            target.Apply(catchAll);
            rules.Add(catchAll);
        }

        var result = new JsonObject
        {
            ["domainStrategy"] = routing.ResolveDomainsForIpRules ? "IPIfNonMatch" : "AsIs",
            ["rules"] = rules,
        };

        if (target.BalancerTag is not null)
        {
            result["balancers"] = new JsonArray(new JsonObject
            {
                ["tag"] = target.BalancerTag,
                ["selector"] = new JsonArray(PoolEmitter.NodeTagPrefix),
                ["strategy"] = new JsonObject { ["type"] = "leastPing" },
            });
        }

        return result;
    }

    private static JsonObject TranslateRule(RoutingRule rule, ProxyTarget target)
    {
        var o = new JsonObject { ["type"] = "field" };

        if (rule.Action == RuleAction.Proxy) target.Apply(o);
        else o["outboundTag"] = rule.Action == RuleAction.Block ? BlockTag : DirectTag;

        if (rule.Domains.Count > 0)
            o["domain"] = new JsonArray(rule.Domains.Select(d => (JsonNode)d).ToArray());
        if (rule.Ips.Count > 0)
            o["ip"] = new JsonArray(rule.Ips.Select(i => (JsonNode)i).ToArray());
        if (rule.Ports.Count > 0)
            o["port"] = string.Join(",", rule.Ports);
        if (rule.Protocols.Count > 0)
            o["protocol"] = new JsonArray(rule.Protocols.Select(p => (JsonNode)p).ToArray());
        if (rule.Processes.Count > 0)
            o["process"] = new JsonArray(rule.Processes.Select(p => (JsonNode)p).ToArray());
        o.SetIf("network", rule.Network);

        return o;
    }

    private static JsonObject BuildDns(AppSettings settings, RoutingProfile routing)
    {
        var servers = new JsonArray { settings.RemoteDns };

        if (settings.RoutingMode == RoutingMode.Rules && !string.IsNullOrWhiteSpace(settings.DirectDns))
        {
            var direct = new JsonObject
            {
                // Xray spells the system resolver "localhost".
                ["address"] = SingBoxConfigBuilder.IsLocal(settings.DirectDns) ? "localhost" : settings.DirectDns,
                ["domains"] = new JsonArray("geosite:private"),
            };
            if (routing.BypassMicrosoftServices)
                direct["domains"]!.AsArray().Add("geosite:microsoft");
            servers.Add(direct);
        }

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["queryStrategy"] = "UseIP",
            ["disableCache"] = false,
        };
        dns.SetIf("tag", "dns-inbound");
        return dns;
    }
}
