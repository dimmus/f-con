using System.Text.Json.Nodes;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Import;
using FCon.Core.Plugins;

namespace FCon.Core.Tests;

public sealed class ConfigBuilderTests
{
    private static readonly PluginRegistry Registry = PluginRegistry.CreateDefault(loadExternal: false);
    private static readonly LinkImporter Importer = new(Registry);

    private static ProxyNode Parse(string link) => Importer.Import(link).Nodes.Single();

    private static AppSettings Settings() => new() { ApiSecret = "s3cret", ApiPort = 10810 };

    [Fact]
    public void Single_server_singbox_config_has_no_group()
    {
        var a = TestKit.Node("A");
        var config = new SingBoxConfigBuilder(Registry).Build(a, Settings(), RoutingProfile.CreateDefault());

        Assert.Null(config.SelectorTag);
        Assert.Null(config.AutoTag);
        Assert.Equal("proxy", config.PrimaryTag);
        Assert.Equal("proxy", config.Root["outbounds"]![0]!["tag"]!.GetValue<string>());
        Assert.Equal("s3cret", config.Root["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());
    }

    [Fact]
    public void Grouped_singbox_config_wraps_servers_in_selector_and_urltest()
    {
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        var c = TestKit.Node("C");
        var settings = Settings();
        settings.HealthCheckIntervalSeconds = 20;

        var config = new SingBoxConfigBuilder(Registry).Build(a, [a, b, c], settings, RoutingProfile.CreateDefault(), "sing-box version 1.12.4", autoSelect: false);

        Assert.Equal("proxy", config.SelectorTag);
        Assert.Equal("auto", config.AutoTag);
        Assert.False(config.StartsOnAuto);
        Assert.Equal(3, config.Members.Count);
        Assert.StartsWith("node-", config.PrimaryTag);

        var outbounds = config.Root["outbounds"]!.AsArray();
        var selector = outbounds[0]!.AsObject();
        Assert.Equal("selector", selector["type"]!.GetValue<string>());
        Assert.Equal("proxy", selector["tag"]!.GetValue<string>());
        Assert.Equal(config.PrimaryTag, selector["default"]!.GetValue<string>());
        Assert.Equal(["auto", .. config.Members.Select(m => m.Tag)], selector["outbounds"]!.AsArray().Select(n => n!.GetValue<string>()));

        var urltest = outbounds[1]!.AsObject();
        Assert.Equal("urltest", urltest["type"]!.GetValue<string>());
        Assert.Equal("20s", urltest["interval"]!.GetValue<string>());
        Assert.Equal(3, urltest["outbounds"]!.AsArray().Count);

        // Routing still points at "proxy", now the selector.
        Assert.Equal("proxy", config.Root["route"]!["final"]!.GetValue<string>());
        Assert.Equal("proxy", config.Root["dns"]!["servers"]![0]!["detour"]!.GetValue<string>());
    }

    [Fact]
    public void Grouped_singbox_config_can_start_on_the_automatic_group()
    {
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        var config = new SingBoxConfigBuilder(Registry).Build(a, [a, b], Settings(), RoutingProfile.CreateDefault(), null, autoSelect: true);

        Assert.True(config.StartsOnAuto);
        Assert.Equal("auto", config.Root["outbounds"]![0]!["default"]!.GetValue<string>());
    }

    [Fact]
    public void Unusable_pool_members_are_dropped_with_a_note_and_the_primary_survives()
    {
        var a = TestKit.Node("A");
        var ghost = TestKit.Node("Ghost", protocol: "no-such-protocol");
        var config = new SingBoxConfigBuilder(Registry).Build(a, [a, ghost], Settings(), RoutingProfile.CreateDefault(), null, false);

        // Only the primary was usable, so no group forms and the config is the plain shape.
        Assert.Null(config.SelectorTag);
        Assert.Single(config.Members);
        Assert.Contains(config.Warnings, w => w.Contains("Left out"));
    }

    [Fact]
    public void Wireguard_goes_to_endpoints_but_still_joins_the_group()
    {
        var wg = Parse("wireguard://DpFqN%2Bf%2Fq3lfTCpixdyFUYc2egR3SdSi9DE2DL%2FArNI%3D@wg.example.com:51820?publickey=GgGDJ1%2FxwGjL6l6%2FgeMb2Rfb5ksGfVZmtdt1BBxXFKI%3D&address=172.16.0.2/32#WG");
        var a = TestKit.Node("A");
        var config = new SingBoxConfigBuilder(Registry).Build(a, [a, wg], Settings(), RoutingProfile.CreateDefault(), null, false);

        var endpointTag = config.Root["endpoints"]![0]!["tag"]!.GetValue<string>();
        var groupTags = config.Root["outbounds"]![1]!["outbounds"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains(endpointTag, groupTags);
    }

    [Fact]
    public void Shadowtls_emits_a_helper_outbound_the_shadowsocks_one_detours_through()
    {
        var ss = Parse("ss://MjAyMi1ibGFrZTMtYWVzLTEyOC1nY206UnMxbW1rd0o4cHRONGtqU1ozSDVmQT09@stls.example.com:443?plugin=shadow-tls%3Bhost%3Dcloud.tencent.com%3Bpassword%3Dstls%3Bversion%3D3#STLS");
        var config = new SingBoxConfigBuilder(Registry).Build(ss, Settings(), RoutingProfile.CreateDefault());

        var outbounds = config.Root["outbounds"]!.AsArray().Select(o => o!.AsObject()).ToList();
        var main = outbounds.Single(o => o["tag"]!.GetValue<string>() == "proxy");
        var helper = outbounds.Single(o => o["tag"]!.GetValue<string>() == "proxy-stls");

        Assert.Equal("proxy-stls", main["detour"]!.GetValue<string>());
        Assert.Null(main["plugin"]);
        Assert.Equal("shadowtls", helper["type"]!.GetValue<string>());
        Assert.Equal(3, helper["version"]!.GetValue<int>());
        Assert.Equal("stls", helper["password"]!.GetValue<string>());
        Assert.Equal("cloud.tencent.com", helper["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("chrome", helper["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void Probe_config_has_no_listeners_and_a_private_api()
    {
        var nodes = new[] { TestKit.Node("A"), TestKit.Node("B") };
        var config = new SingBoxConfigBuilder(Registry).BuildProbe(nodes, Settings(), 39555, "probe-secret", null);

        Assert.Null(config.Root["inbounds"]);
        Assert.Equal(2, config.Members.Count);
        Assert.Equal("127.0.0.1:39555", config.Root["experimental"]!["clash_api"]!["external_controller"]!.GetValue<string>());
        Assert.Equal("probe-secret", config.Root["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());
        Assert.Equal("direct", config.Root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void Xray_grouped_config_uses_balancer_only_when_auto_selected()
    {
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        var builder = new XrayConfigBuilder(Registry);

        var fixedConfig = builder.Build(a, [a, b], Settings(), RoutingProfile.CreateDefault(), "Xray 25.1.30", autoSelect: false);
        Assert.Null(fixedConfig.AutoTag);
        Assert.Null(fixedConfig.Root["observatory"]);
        Assert.Null(fixedConfig.Root["routing"]!["balancers"]);
        Assert.Equal(fixedConfig.PrimaryTag, fixedConfig.Root["outbounds"]![0]!["tag"]!.GetValue<string>());

        var auto = builder.Build(a, [a, b], Settings(), RoutingProfile.CreateDefault(), "Xray 25.1.30", autoSelect: true);
        Assert.Equal("auto", auto.AutoTag);
        Assert.True(auto.StartsOnAuto);
        Assert.NotNull(auto.Root["observatory"]);
        var balancer = auto.Root["routing"]!["balancers"]![0]!;
        Assert.Equal("leastPing", balancer["strategy"]!["type"]!.GetValue<string>());
        Assert.Equal("node-", balancer["selector"]![0]!.GetValue<string>());

        var rules = auto.Root["routing"]!["rules"]!.AsArray();
        Assert.Equal("auto", rules[^1]!["balancerTag"]!.GetValue<string>());
    }

    [Fact]
    public void Xray_drops_singbox_only_members_from_the_pool()
    {
        var a = TestKit.Node("A");
        var hy2 = Parse("hysteria2://pw@hy2.example.com:443?sni=hy2.example.com#Hy2");
        var config = new XrayConfigBuilder(Registry).Build(a, [a, hy2], Settings(), RoutingProfile.CreateDefault(), null, false);

        Assert.Single(config.Members);
        Assert.Contains(config.Warnings, w => w.Contains("Left out"));
    }

    [Fact]
    public void Engine_controller_builds_the_pool_from_ranked_candidates_up_to_the_cap()
    {
        var nodes = Enumerable.Range(0, 6).Select(i => TestKit.Node($"N{i}")).ToList();
        var settings = Settings();
        settings.FailoverPoolSize = 4;
        var controller = new EngineController(Registry, () => settings, RoutingProfile.CreateDefault, () => nodes);

        var config = controller.Generate(nodes[2], settings);

        Assert.Equal(4, config.Members.Count);
        Assert.Equal(nodes[2].Id, config.Members[0].Node.Id);
        Assert.Equal([nodes[0].Id, nodes[1].Id, nodes[3].Id], config.Members.Skip(1).Select(m => m.Node.Id));

        settings.InCoreFailover = false;
        Assert.Single(controller.Generate(nodes[2], settings).Members);
    }

    [Fact]
    public void Advisor_flags_restart_based_failover_and_can_fix_it()
    {
        var settings = new AppSettings { InCoreFailover = false, AutoFailover = true };
        var routing = RoutingProfile.CreateDefault();

        Assert.Contains(ConfigAdvisor.Inspect(settings, routing, null), a => a.Id == "failover.restart");
        var applied = ConfigAdvisor.ApplyFixes(settings, routing);
        Assert.True(settings.InCoreFailover);
        Assert.Contains(applied, a => a.Contains("inside the core"));
    }
}
