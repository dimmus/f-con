using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Plugins;

namespace FCon.Core.Engine;

/// <summary>Generated engine configuration plus anything the user should know about it.</summary>
public sealed record GeneratedConfig(JsonObject Root, IReadOnlyList<string> Warnings)
{
    public string ToJson() => Root.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

/// <summary>Builds a complete Xray-core configuration around one selected node.</summary>
public sealed class XrayConfigBuilder(PluginRegistry registry)
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    private const string ApiTag = "api";

    public GeneratedConfig Build(ProxyNode node, AppSettings settings, RoutingProfile routing)
    {
        var plugin = registry.Require(node);
        var ctx = new EmitContext(ProxyTag);
        var proxy = plugin.EmitOutbound(node, EngineKind.Xray, ctx);

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = settings.LogLevel },
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = new JsonArray(
                proxy,
                new JsonObject
                {
                    ["tag"] = DirectTag,
                    ["protocol"] = "freedom",
                    ["settings"] = new JsonObject { ["domainStrategy"] = "UseIP" },
                },
                new JsonObject
                {
                    ["tag"] = BlockTag,
                    ["protocol"] = "blackhole",
                    ["settings"] = new JsonObject { ["response"] = new JsonObject { ["type"] = "http" } },
                }),
            ["routing"] = BuildRouting(settings, routing),
            ["dns"] = BuildDns(settings, routing),
        };

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

        return new GeneratedConfig(root, ctx.Warnings);
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

    private static JsonObject BuildRouting(AppSettings settings, RoutingProfile routing)
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
                rules.Add(TranslateRule(rule));
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

        return new JsonObject
        {
            ["domainStrategy"] = routing.ResolveDomainsForIpRules ? "IPIfNonMatch" : "AsIs",
            ["rules"] = rules,
        };
    }

    private static JsonObject TranslateRule(RoutingRule rule)
    {
        var o = new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = TagFor(rule.Action),
        };

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

    private static string TagFor(RuleAction action) => action switch
    {
        RuleAction.Proxy => ProxyTag,
        RuleAction.Block => BlockTag,
        _ => DirectTag,
    };

    private static JsonObject BuildDns(AppSettings settings, RoutingProfile routing)
    {
        var servers = new JsonArray { settings.RemoteDns };

        if (settings.RoutingMode == RoutingMode.Rules && !string.IsNullOrWhiteSpace(settings.DirectDns))
        {
            var direct = new JsonObject
            {
                ["address"] = settings.DirectDns,
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
