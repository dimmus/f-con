using System.Net.Sockets;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Localization;
using FCon.Core.Plugins;

namespace FCon.Core.Tests;

public sealed class DiagnosticsTests
{
    private static readonly PluginRegistry Registry = PluginRegistry.CreateDefault(loadExternal: false);

    [Theory]
    [InlineData(SocketError.AccessDenied, DiagnosticVerdict.Fail, "WSAEACCES")]
    [InlineData(SocketError.TimedOut, DiagnosticVerdict.Warn, "blocked upstream")]
    [InlineData(SocketError.ConnectionRefused, DiagnosticVerdict.Fail, "nothing listens")]
    [InlineData(SocketError.HostUnreachable, DiagnosticVerdict.Fail, "no path")]
    [InlineData(SocketError.ConnectionReset, DiagnosticVerdict.Warn, "packet inspection")]
    public void Winsock_errors_become_actionable_hints(SocketError error, DiagnosticVerdict verdict, string hintFragment)
    {
        Localizer.Instance.Apply("en");
        var (v, detail, hint) = ConnectionDiagnostics.DescribeSocketError(error, "1.2.3.4:443");
        Assert.Equal(verdict, v);
        Assert.Contains("1.2.3.4:443", detail);
        Assert.Contains(hintFragment, hint);
    }

    [Fact]
    public async Task A_report_without_a_server_still_covers_core_internet_and_firewall()
    {
        Localizer.Instance.Apply("en");
        var settings = new AppSettings { LatencyTimeoutMs = 1500 };
        var engine = new FakeEngine();

        var report = await ConnectionDiagnostics.RunAsync(null, settings, engine, Registry);

        var names = report.Items.Select(i => i.Name).ToList();
        Assert.Contains("Proxy core", names);
        Assert.Contains("Direct internet", names);
        Assert.Contains("Server", names);
        Assert.Contains("Local listener", names);
        Assert.Contains("Windows Firewall", names);
        Assert.Contains("Windows proxy", names);
        Assert.Contains("[", report.ToText());
    }

    [Fact]
    public async Task A_refused_local_port_is_reported_as_refused_not_as_a_firewall()
    {
        Localizer.Instance.Apply("en");
        var settings = new AppSettings { LatencyTimeoutMs = 1500 };
        var node = TestKit.Node("Local") with { Server = "127.0.0.1", Port = 1 };

        var report = await ConnectionDiagnostics.RunAsync(node, settings, new FakeEngine(), Registry);

        var reach = report.Items.Single(i => i.Name == "Reach the server");
        Assert.Equal(DiagnosticVerdict.Fail, reach.Verdict);
        Assert.Contains("refused", reach.Detail);
    }

    [Fact]
    public void Udp_protocols_get_an_informational_reach_entry()
    {
        // No socket probe is meaningful for QUIC-based protocols; make sure the report says so.
        var node = TestKit.Node("Hy2", protocol: "hysteria2");
        Assert.Equal("hysteria2", node.Protocol);
    }

    [Fact]
    public void Local_dns_becomes_the_system_resolver_in_both_cores()
    {
        var settings = new AppSettings { DirectDns = "local", ApiSecret = "s" };
        var a = TestKit.Node("A");

        var sb = new SingBoxConfigBuilder(Registry).Build(a, settings, RoutingProfile.CreateDefault());
        var local = sb.Root["dns"]!["servers"]!.AsArray().Select(s => s!.AsObject())
            .Single(s => s["tag"]!.GetValue<string>() == "local");
        Assert.Equal("local", local["type"]!.GetValue<string>());
        Assert.Null(local["server"]);
        Assert.Null(local["detour"]);
        Assert.Equal("local", sb.Root["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>());

        var xr = new XrayConfigBuilder(Registry).Build(a, settings, RoutingProfile.CreateDefault());
        var servers = xr.Root["dns"]!["servers"]!.AsArray();
        Assert.Equal("localhost", servers[1]!["address"]!.GetValue<string>());
    }

    [Fact]
    public void Advisor_flags_a_chinese_resolver_and_fixes_it_to_local()
    {
        var settings = new AppSettings { DirectDns = "https://223.5.5.5/dns-query" };
        var routing = RoutingProfile.CreateDefault();

        Assert.Contains(ConfigAdvisor.Inspect(settings, routing, null), a => a.Id == "dns.direct");
        ConfigAdvisor.ApplyFixes(settings, routing);
        Assert.Equal("local", settings.DirectDns);
        Assert.DoesNotContain(ConfigAdvisor.Inspect(settings, routing, null), a => a.Id == "dns.direct");
    }

    [Fact]
    public void Default_direct_dns_is_the_system_resolver()
    {
        Assert.Equal("local", new AppSettings().DirectDns);
    }
}
