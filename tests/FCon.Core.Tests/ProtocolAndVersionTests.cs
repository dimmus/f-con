using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Import;
using FCon.Core.Plugins;
using FCon.Plugins.Builtin;

namespace FCon.Core.Tests;

public sealed class ProtocolAndVersionTests
{
    private static readonly PluginRegistry Registry = PluginRegistry.CreateDefault(loadExternal: false);
    private static readonly LinkImporter Importer = new(Registry);

    [Theory]
    [InlineData("hysteria2://pw@hy2.example.com:443?sni=hy2.example.com&obfs=salamander&obfs-password=ob&mport=2080-3000&up=50#Hy2", "hysteria2")]
    [InlineData("hy2://pw@hy2.example.com:8443#short", "hysteria2")]
    [InlineData("tuic://b831381d-6324-4d53-ad4f-8cda48b30811:pw@t.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=t.example.com#TUIC", "tuic")]
    [InlineData("anytls://pw@any.example.com:443?sni=any.example.com&fp=chrome&insecure=1#Any", "anytls")]
    public void New_protocol_links_round_trip(string link, string protocol)
    {
        var node = Importer.Import(link).Nodes.Single();
        Assert.Equal(protocol, node.Protocol);
        Assert.Empty(Importer.Validate(node));

        var rebuilt = Registry.Require(node).BuildLink(node);
        var again = Importer.Import(rebuilt).Nodes.Single();

        Assert.Equal(node.Endpoint, again.Endpoint);
        Assert.Equal(node.Settings.OrderBy(k => k.Key), again.Settings.OrderBy(k => k.Key));
        Assert.Equal(node.Security.ServerName, again.Security.ServerName);
        Assert.Equal(node.Security.AllowInsecure, again.Security.AllowInsecure);
    }

    [Fact]
    public void Hysteria2_emits_port_hopping_only_on_a_core_that_has_it()
    {
        var node = Importer.Import("hysteria2://pw@hy2.example.com:443?mport=2080-3000,5000&obfs=salamander&obfs-password=ob#Hy2").Nodes.Single();
        var plugin = Registry.Require(node);

        var modern = new EmitContext("proxy", "sing-box version 1.12.0");
        var outbound = plugin.EmitOutbound(node, EngineKind.SingBox, modern);
        Assert.Equal(["2080:3000", "5000:5000"], outbound["server_ports"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("salamander", outbound["obfs"]!["type"]!.GetValue<string>());
        Assert.Equal("h3", outbound["tls"]!["alpn"]![0]!.GetValue<string>());

        var old = new EmitContext("proxy", "sing-box version 1.11.9");
        var legacy = plugin.EmitOutbound(node, EngineKind.SingBox, old);
        Assert.Null(legacy["server_ports"]);
        Assert.Contains(old.Warnings, w => w.Contains("1.12"));

        Assert.Throws<NotSupportedException>(() => plugin.EmitOutbound(node, EngineKind.Xray, modern));
    }

    [Fact]
    public void Hysteria2_port_ranges_are_validated()
    {
        Assert.Equal(["2080:3000"], Hysteria2Plugin.ParsePortRanges("2080-3000")!);
        Assert.Null(Hysteria2Plugin.ParsePortRanges("3000-2080"));
        Assert.Null(Hysteria2Plugin.ParsePortRanges("abc"));
        Assert.Null(Hysteria2Plugin.ParsePortRanges("70000"));
    }

    [Fact]
    public void AnyTls_refuses_a_core_older_than_1_12()
    {
        var node = Importer.Import("anytls://pw@any.example.com:443?sni=any.example.com#Any").Nodes.Single();
        var plugin = Registry.Require(node);

        Assert.Throws<NotSupportedException>(() =>
            plugin.EmitOutbound(node, EngineKind.SingBox, new EmitContext("proxy", "sing-box version 1.11.0")));

        var unknown = plugin.EmitOutbound(node, EngineKind.SingBox, new EmitContext("proxy"));
        Assert.Equal("anytls", unknown["type"]!.GetValue<string>());
        Assert.Equal("any.example.com", unknown["tls"]!["server_name"]!.GetValue<string>());
    }

    [Fact]
    public void Tuic_emits_v5_fields()
    {
        var node = Importer.Import("tuic://b831381d-6324-4d53-ad4f-8cda48b30811:pw@t.example.com:443?congestion_control=cubic&udp_relay_mode=quic&zero_rtt_handshake=1#TUIC").Nodes.Single();
        var outbound = Registry.Require(node).EmitOutbound(node, EngineKind.SingBox, new EmitContext("proxy"));

        Assert.Equal("tuic", outbound["type"]!.GetValue<string>());
        Assert.Equal("cubic", outbound["congestion_control"]!.GetValue<string>());
        Assert.Equal("quic", outbound["udp_relay_mode"]!.GetValue<string>());
        Assert.True(outbound["zero_rtt_handshake"]!.GetValue<bool>());
        Assert.Equal("h3", outbound["tls"]!["alpn"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Shadowtls_needs_host_and_password_in_the_plugin_options()
    {
        var node = Importer.Import("ss://MjAyMi1ibGFrZTMtYWVzLTEyOC1nY206UnMxbW1rd0o4cHRONGtqU1ozSDVmQT09@stls.example.com:443?plugin=shadow-tls%3Bversion%3D3#STLS").Nodes.Single();
        var issues = Importer.Validate(node);
        Assert.Contains(issues, i => i.Contains("password"));
        Assert.Contains(issues, i => i.Contains("host"));

        var opts = ShadowsocksPlugin.ParsePluginOpts("host=a.b;password=p;version=3");
        Assert.Equal("a.b", opts["host"]);
        Assert.Equal("3", opts["VERSION"]);
    }

    [Theory]
    [InlineData("sing-box version 1.15.0-alpha.2", 1, 15, 0, true)]
    [InlineData("sing-box version 1.12.4", 1, 12, 4, false)]
    [InlineData("Xray 26.7.28 (Xray, Penetrates Everything.) 5ca6f4b (go1.26.5 windows/amd64)", 26, 7, 28, false)]
    [InlineData("Xray 1.8 (something)", 1, 8, 0, false)]
    public void Engine_versions_parse_from_banners(string banner, int major, int minor, int patch, bool pre)
    {
        Assert.Equal(new Version(major, minor, patch), EngineVersionKit.Parse(banner));
        Assert.Equal(pre, EngineVersionKit.IsPreRelease(banner));
    }

    [Fact]
    public void Unknown_version_never_blocks_a_feature()
    {
        Assert.Null(EngineVersionKit.Parse(null));
        Assert.Null(EngineVersionKit.Parse("garbage"));
        Assert.True(EngineVersionKit.AtLeast(null, 9, 9));
        Assert.False(EngineVersionKit.AtLeast(new Version(1, 11, 0), 1, 12));
        Assert.True(new EmitContext("t").EngineAtLeast(99, 0));
    }

    [Fact]
    public void Xray_checksum_file_yields_the_sha256_line()
    {
        const string dgst = "MD5= 0123\nSHA1= 4567\nSHA2-256= "
                            + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                            + "\nSHA2-512= zzz\n";
        var hash = EngineDownloader.ParseDgst(dgst);
        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
        Assert.Null(EngineDownloader.ParseDgst("MD5= abc\n"));
    }

    [Fact]
    public void Node_quality_quarantines_after_a_streak_and_recovers_on_success()
    {
        var now = DateTimeOffset.Now;
        var q = new NodeQuality { NodeId = Guid.NewGuid() };

        q = q.WithFailure(now, "x").WithFailure(now, "x");
        Assert.False(q.IsQuarantined(now));

        q = q.WithFailure(now, "x");
        Assert.True(q.IsQuarantined(now));
        Assert.Equal(double.MinValue, q.Score(now));

        q = q.WithSuccess(now);
        Assert.False(q.IsQuarantined(now));
        Assert.True(q.Verified);
        Assert.True(q.Score(now) > 0);
    }

    [Fact]
    public void Quality_ranking_prefers_a_reliable_server_over_a_fast_flaky_one()
    {
        var store = TestKit.TempQuality();
        var reliable = TestKit.Node("Reliable");
        var flaky = TestKit.Node("Flaky");

        for (var i = 0; i < 5; i++) store.RecordSuccess(reliable.Id);
        store.RecordLatency(reliable.Id, 180);

        store.RecordSuccess(flaky.Id);
        store.RecordFailure(flaky.Id, "x");
        store.RecordFailure(flaky.Id, "x");
        store.RecordLatency(flaky.Id, 40);

        var ranked = store.Rank([flaky, reliable]);
        Assert.Equal(reliable.Id, ranked[0].Id);
    }
}
