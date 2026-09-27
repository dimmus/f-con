using FCon.Core.Engine;
using FCon.Core.Health;

namespace FCon.Core.Tests;

/// <summary>
/// Behaviour pinned after reading a real session log: the selector restored from
/// sing-box's cache, a stale exit shown against the wrong server, and a route flap
/// noticed only on the next timer tick.
/// </summary>
public sealed class SupervisorRegressionTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeEngine Engine { get; } = new();
        public FakeProbe Probe { get; } = new();
        public Core.Config.AppSettings Settings { get; } = TestKit.Settings();
        public QualityStore Quality { get; } = TestKit.TempQuality();
        public List<Abstractions.Model.ProxyNode> Candidates { get; } = [];
        public List<string> Log { get; } = [];
        public SupervisorTiming Timing { get; set; } = TestKit.FastTiming();
        public ConnectionSupervisor Supervisor { get; private set; } = null!;

        public Harness Build()
        {
            Supervisor = new ConnectionSupervisor(
                Engine, Quality, () => Settings, () => Candidates,
                (m, _) => { lock (Log) Log.Add(m); },
                Probe, null, Timing);
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisconnectAsync();
            await Supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_selector_restored_from_cache_is_pinned_back_to_the_chosen_server()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);

        // The cache file left the selector on the automatic group from last time.
        var api = new FakeApi();
        api.Now["proxy"] = "auto";
        api.Now["auto"] = "node-b";
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Equal([("proxy", "node-a")], api.Selections);
        Assert.Equal(a.Id, h.Supervisor.Current.Node!.Id);
        Assert.Contains(h.Log, l => l.Contains("Selector was on \"auto\""));
    }

    [Fact]
    public async Task Auto_start_pins_the_selector_to_the_automatic_group()
    {
        await using var h = new Harness().Build();
        h.Settings.PreferBestServer = true;
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);

        var api = new FakeApi { AutoPick = "node-b" };
        api.Now["proxy"] = "node-a";   // cache says A, config says auto
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b) with { StartsOnAuto = true };

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);

        Assert.Equal([("proxy", "auto")], api.Selections);
        Assert.Equal(b.Id, h.Supervisor.Current.Node!.Id);
    }

    [Fact]
    public async Task A_new_connection_does_not_inherit_the_previous_exit()
    {
        await using var h = new Harness().Build();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);
        h.Probe.Exit = new ExitInfo("203.0.113.9", "NL");

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.Exit is not null);

        // The lookup through the next server fails; the old answer must not survive.
        h.Probe.Exit = null;
        await h.Supervisor.ConnectAsync(b);
        await TestKit.WaitForAsync(() => h.Supervisor.Current is { State: LinkState.Healthy } c && c.Node?.Id == b.Id);
        await Task.Delay(100);

        Assert.Null(h.Supervisor.Current.Exit);
    }

    [Fact]
    public async Task A_group_move_clears_the_exit_until_the_lookup_answers_again()
    {
        await using var h = new Harness().Build();
        h.Settings.PreferBestServer = true;
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        h.Candidates.AddRange([a, b]);
        h.Probe.Exit = new ExitInfo("203.0.113.9", "FI");

        var api = new FakeApi();
        api.Now["proxy"] = "auto";
        api.Now["auto"] = "node-a";
        h.Engine.Api = api;
        h.Engine.ActiveConfig = TestKit.GroupedConfig(a, b) with { StartsOnAuto = true };

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.Exit?.CountryCode == "FI");

        // The core moves to B and the new exit is a different country.
        h.Probe.Exit = new ExitInfo("198.51.100.4", "NL");
        api.Now["auto"] = "node-b";

        await TestKit.WaitForAsync(() => h.Supervisor.Current.Node?.Id == b.Id);
        // Immediately after the move the old exit is gone...
        Assert.True(h.Supervisor.Current.Exit is null || h.Supervisor.Current.Exit.CountryCode == "NL");
        // ...and the fresh lookup lands after the settle delay.
        await TestKit.WaitForAsync(() => h.Supervisor.Current.Exit?.CountryCode == "NL");
    }

    [Fact]
    public async Task A_nudge_probes_immediately_instead_of_waiting_for_the_interval()
    {
        await using var h = new Harness { Timing = TestKit.FastTiming() with { MonitorInterval = TimeSpan.FromSeconds(30) } }.Build();
        var a = TestKit.Node("A");
        h.Candidates.Add(a);

        await h.Supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => h.Supervisor.Current.State == LinkState.Healthy);
        Assert.Equal(1, h.Probe.Calls);

        h.Supervisor.Nudge("Network address changed");
        await TestKit.WaitForAsync(() => h.Probe.Calls >= 2, 2000);

        Assert.Contains(h.Log, l => l.Contains("checking the tunnel now"));
    }

    [Fact]
    public async Task A_nudge_while_idle_is_ignored()
    {
        await using var h = new Harness().Build();
        h.Supervisor.Nudge("Network address changed");
        await Task.Delay(50);

        Assert.Empty(h.Log);
        Assert.Equal(0, h.Probe.Calls);
    }

    [Fact]
    public async Task Network_watcher_coalesces_a_burst_into_one_nudge()
    {
        var reasons = new List<string>();
        using var watcher = new NetworkWatcher(r => { lock (reasons) reasons.Add(r); }, TimeSpan.FromMilliseconds(60));

        var schedule = typeof(NetworkWatcher).GetMethod("Schedule",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 5; i++) schedule.Invoke(watcher, ["Network address changed"]);

        await Task.Delay(250);
        Assert.Single(reasons);
    }

    [Theory]
    [InlineData("+0500 2026-09-27 08:59:04 ERROR [1956854249 57.64s] connection: connection download closed: http2: client connection force closed via ClientConn.Close", true)]
    [InlineData("+0500 2026-09-27 09:27:59 ERROR [188111499 11.41s] connection: connection upload closed: raw read: An existing connection was forcibly closed", true)]
    [InlineData("+0500 2026-09-27 08:50:10 ERROR network: missing default interface", false)]
    [InlineData("FATAL[0000] start service: initialize cache-file: open cache.db: no such file", false)]
    public void Routine_connection_closes_are_not_errors(string line, bool routine)
    {
        Assert.Equal(routine, EngineController.IsRoutineCoreChatter(line));
    }
}
