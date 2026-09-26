using FCon.Abstractions.Model;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;

namespace FCon.Core.Tests;

/// <summary>
/// The connect / verify / monitor / recover state machine, driven by scripted probes
/// and a fake core. Timings are milliseconds; nothing here touches the network.
/// </summary>
public sealed class ConnectionSupervisorTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeEngine Engine { get; } = new();
        public FakeProbe Probe { get; } = new();
        public AppSettings Settings { get; } = TestKit.Settings();
        public QualityStore Quality { get; } = TestKit.TempQuality();
        public List<ProxyNode> Candidates { get; } = [];
        public SnapshotLog Snapshots { get; } = new();
        public List<string> Log { get; } = [];
        public Func<TrafficSample?>? Traffic { get; set; }
        public ConnectionSupervisor Supervisor { get; private set; } = null!;

        public Harness Build()
        {
            Supervisor = new ConnectionSupervisor(
                Engine,
                Quality,
                () => Settings,
                () => Candidates,
                (m, _) => { lock (Log) Log.Add(m); },
                Probe,
                Traffic,
                TestKit.FastTiming());
            Supervisor.Changed += Snapshots.Add;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisconnectAsync();
            await Supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_verifies_traffic_then_reports_healthy()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        h.Candidates.Add(a);
        h.Probe.Then(FakeProbe.Healthy(42));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Equal(a.Id, h.Supervisor.Current.Node!.Id);
        Assert.Equal(42, h.Supervisor.Current.LatencyMs);
        Assert.True(h.Snapshots.Any(s => s.State == LinkState.Verifying));
        Assert.Single(h.Engine.Connects);
        Assert.False(h.Engine.Connects[0].AutoSelect);
        Assert.True(h.Quality.Get(a.Id).Verified);
    }

    [Fact]
    public async Task Verification_failure_without_a_group_fails_over_to_the_next_server()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);
        h.Probe.Then(FakeProbe.Down(), FakeProbe.Healthy(30));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Equal(b.Id, h.Supervisor.Current.Node!.Id);
        Assert.Equal([a.Id, b.Id], h.Engine.Connects.Select(c => c.Node.Id));
        // Second attempt lets the core choose from the start.
        Assert.True(h.Engine.Connects[1].AutoSelect);
        Assert.True(h.Engine.Disconnects >= 1, "the dead server must be torn down before the next one");
        Assert.Equal(1, h.Quality.Get(a.Id).Failures);
        Assert.True(h.Snapshots.Any(s => s.State == LinkState.Failed && s.Node?.Id == a.Id));
    }

    [Fact]
    public async Task Verification_failure_with_a_group_switches_inside_the_core_without_restarting()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);

        var api = new FakeApi { AutoPick = "node-b" };
        api.Now["proxy"] = "node-a";
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b);

        h.Probe.Then(FakeProbe.Down(), FakeProbe.Healthy(25));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Equal(b.Id, h.Supervisor.Current.Node!.Id);
        Assert.Single(h.Engine.Connects);
        Assert.Equal(0, h.Engine.Disconnects);
        Assert.Equal([("proxy", "auto")], api.Selections);
        Assert.True(h.Snapshots.Any(s => s.State == LinkState.Recovering));
        Assert.True(h.Quality.Get(b.Id).Verified);
    }

    [Fact]
    public async Task Monitor_failures_below_threshold_stay_degraded_and_recover_in_place()
    {
        await using var h = new Harness().Build();
        h.Settings.UnhealthyThreshold = 3;
        var a = TestKit.Node("A");
        h.Candidates.Add(a);
        h.Probe.Then(FakeProbe.Healthy(10), FakeProbe.Down(), FakeProbe.Down(), FakeProbe.Healthy(12));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Snapshots.Any(s => s.State == LinkState.Degraded && s.ConsecutiveFailures == 2));
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy && h.Probe.Calls >= 4);

        Assert.Single(h.Engine.Connects);
        Assert.Contains(h.Log, l => l.Contains("recovered"));
    }

    [Fact]
    public async Task Monitor_failures_at_threshold_restart_when_there_is_no_group()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        h.Candidates.Add(a);
        h.Probe.Then(FakeProbe.Healthy(10), FakeProbe.Down(), FakeProbe.Down(), FakeProbe.Healthy(11));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Engine.ConnectCount >= 2);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy && h.Probe.Calls >= 4);

        Assert.True(h.Snapshots.Any(s => s.State == LinkState.Recovering && s.Message!.Contains("Restarting")));
        Assert.True(h.Quality.Get(a.Id).Failures >= 1);
    }

    [Fact]
    public async Task Monitor_failures_at_threshold_switch_inside_the_core_when_grouped()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);

        var api = new FakeApi { AutoPick = "node-b" };
        api.Now["proxy"] = "node-a";
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b);

        h.Probe.Then(FakeProbe.Healthy(10), FakeProbe.Down(), FakeProbe.Down(), FakeProbe.Healthy(20));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => api.Selections.Count == 1);
        await TestKit.WaitForAsync(() => h.Supervisor.Current is { State: LinkState.Healthy } c && c.Node?.Id == b.Id);

        Assert.Single(h.Engine.Connects);
        Assert.Equal(0, h.Engine.Disconnects);
        Assert.Contains(h.Log, l => l.Contains("automatic group"));
    }

    [Fact]
    public async Task Real_traffic_counts_as_healthy_and_skips_the_probe()
    {
        await using var h = new Harness();
        long total = 0;
        h.Traffic = () => new TrafficSample(0, total += 1024 * 1024, 0, 0, 1);
        h.Build();

        var a = TestKit.Node("A");
        h.Candidates.Add(a);
        // Verify, then one probe on the first tick (it only sets the passive mark), then
        // the probe would fail forever - but traffic is flowing, so it must not be asked.
        h.Probe.Then(FakeProbe.Healthy(10), FakeProbe.Healthy(10), FakeProbe.Down());

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Probe.Calls >= 2);
        await Task.Delay(300);

        Assert.Equal(LinkState.Healthy, h.Supervisor.Current.State);
        Assert.Equal(2, h.Probe.Calls);
        Assert.Single(h.Engine.Connects);
    }

    [Fact]
    public async Task Idle_link_still_gets_probed_when_no_traffic_flows()
    {
        await using var h = new Harness();
        h.Traffic = () => new TrafficSample(0, 1000, 0, 0, 0);
        h.Build();

        var a = TestKit.Node("A");
        h.Candidates.Add(a);
        h.Probe.Then(FakeProbe.Healthy(10));

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Probe.Calls >= 4);

        Assert.Equal(LinkState.Healthy, h.Supervisor.Current.State);
    }

    [Fact]
    public async Task A_core_that_dies_is_reconnected_while_wanted()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        h.Candidates.Add(a);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        h.Engine.RaiseFault("exit code 2");
        await TestKit.WaitForAsync(() => h.Engine.ConnectCount >= 2);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Contains(h.Log, l => l.Contains("dropped"));
        Assert.True(h.Quality.Get(a.Id).Failures >= 1);
    }

    [Fact]
    public async Task A_core_that_dies_after_disconnect_is_left_alone()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        h.Candidates.Add(a);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);
        await h.Supervisor.DisconnectAsync();

        h.Engine.RaiseFault("exit code 2");
        await Task.Delay(150);

        Assert.Equal(LinkState.Idle, h.Supervisor.Current.State);
        Assert.Single(h.Engine.Connects);
    }

    [Fact]
    public async Task Disconnect_ends_an_unbounded_retry_loop()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);
        h.Engine.OnConnect = (n, _) => new ConnectionStatus(ConnectionState.Faulted, n, "refused", []);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Engine.ConnectCount >= 3, 6000);

        Assert.True(h.Snapshots.Any(s => s.State == LinkState.Recovering && s.Message!.Contains("Retrying in")));

        await h.Supervisor.DisconnectAsync();
        var after = h.Engine.ConnectCount;
        await Task.Delay(200);

        Assert.Equal(LinkState.Idle, h.Supervisor.Current.State);
        Assert.Equal(after, h.Engine.ConnectCount);
    }

    [Fact]
    public async Task Auto_failover_off_means_no_other_server_is_tried()
    {
        await using var h = new Harness().Build();
        h.Settings.AutoFailover = false;
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);
        h.Engine.OnConnect = (n, _) => new ConnectionStatus(ConnectionState.Faulted, n, "refused", []);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Engine.ConnectCount >= 2, 6000);

        Assert.All(h.Engine.Connects, c => Assert.Equal(a.Id, c.Node.Id));
    }

    [Fact]
    public async Task The_reported_server_follows_the_automatic_group()
    {
        await using var h = new Harness().Build();
        h.Settings.PreferBestServer = true;
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);

        var api = new FakeApi();
        api.Now["proxy"] = "auto";
        api.Now["auto"] = "node-b";
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.True(h.Engine.Connects[0].AutoSelect);
        Assert.Equal(b.Id, h.Supervisor.Current.Node!.Id);
        Assert.True(h.Quality.Get(b.Id).Verified);

        // The core moves again; the next tick must notice.
        api.Now["auto"] = "node-a";
        await TestKit.WaitForAsync(() => h.Supervisor.Current.Node?.Id == a.Id);
        Assert.Contains(h.Log, l => l.Contains("moved traffic"));
    }

    [Fact]
    public async Task Exit_information_is_attached_once_known()
    {
        await using var h = new Harness().Build();
        h.Probe.Exit = new ExitInfo("203.0.113.9", "NL");
        var a = TestKit.Node("A");
        h.Candidates.Add(a);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.Exit is not null);

        Assert.Equal("Netherlands", h.Supervisor.Current.Exit!.CountryName);
        Assert.Contains(h.Log, l => l.StartsWith("Exit:"));
    }

    [Fact]
    public async Task Unverified_connect_is_reported_as_healthy_but_says_so()
    {
        await using var h = new Harness().Build();
        h.Settings.VerifyOnConnect = false;
        var a = TestKit.Node("A");
        h.Candidates.Add(a);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Contains("not verified", h.Supervisor.Current.Message);
        Assert.Equal(0, h.Probe.Calls);
    }
}
