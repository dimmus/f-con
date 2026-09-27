using FCon.Core.Engine;
using FCon.Core.Health;

namespace FCon.Core.Tests;

/// <summary>
/// A machine with no core must say so and stop, rather than cycling through servers
/// and blaming each of them. Found on a first start of a fresh install.
/// </summary>
public sealed class MissingCoreTests
{
    [Fact]
    public async Task A_missing_core_stops_the_loop_and_names_the_cause()
    {
        var engine = new FakeEngine();
        var quality = TestKit.TempQuality();
        var settings = TestKit.Settings();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");
        var log = new List<string>();
        var snapshots = new SnapshotLog();

        engine.OnConnect = (n, _) => new ConnectionStatus(ConnectionState.Faulted, n,
            "sing-box.exe is not installed. Open Settings to get it.", []) { Fault = FaultKind.CoreMissing };

        await using var supervisor = new ConnectionSupervisor(
            engine, quality, () => settings, () => [a, b], (m, _) => { lock (log) log.Add(m); },
            new FakeProbe(), null, TestKit.FastTiming());
        supervisor.Changed += snapshots.Add;

        await supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => supervisor.Current.State == LinkState.Failed);
        await Task.Delay(200);

        // One attempt, no failover, no retry round.
        Assert.Single(engine.Connects);
        Assert.Equal(FaultKind.CoreMissing, supervisor.Current.Fault);
        Assert.Contains("not installed", supervisor.Current.Message);
        Assert.DoesNotContain(snapshots.Items, s => s.Message?.Contains("Retrying") == true);

        // The servers are not blamed.
        Assert.Equal(0, quality.Get(a.Id).Failures);
        Assert.Equal(0, quality.Get(b.Id).Failures);
        Assert.Contains(log, l => l.Contains("not installed"));
    }

    [Fact]
    public async Task A_port_conflict_is_also_final_but_a_server_fault_still_fails_over()
    {
        var engine = new FakeEngine();
        var quality = TestKit.TempQuality();
        var settings = TestKit.Settings();
        var a = TestKit.Node("A");
        var b = TestKit.Node("B");

        engine.OnConnect = (n, _) => n.Id == a.Id
            ? new ConnectionStatus(ConnectionState.Faulted, n, "refused", [])
            : null;

        await using var supervisor = new ConnectionSupervisor(
            engine, quality, () => settings, () => [a, b], (_, _) => { }, new FakeProbe(), null, TestKit.FastTiming());

        await supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => supervisor.Current.State == LinkState.Healthy);
        Assert.Equal(b.Id, supervisor.Current.Node!.Id);
        Assert.Equal(FaultKind.None, supervisor.Current.Fault);
        await supervisor.DisconnectAsync();

        engine.Connects.Clear();
        engine.OnConnect = (n, _) => new ConnectionStatus(ConnectionState.Faulted, n, "SOCKS port 10808 is already in use", [])
        {
            Fault = FaultKind.PortConflict,
        };
        await supervisor.ConnectAsync(a);
        await TestKit.WaitForAsync(() => supervisor.Current.State == LinkState.Failed);
        await Task.Delay(150);
        Assert.Single(engine.Connects);
        Assert.Equal(FaultKind.PortConflict, supervisor.Current.Fault);
    }

    [Fact]
    public void Environment_faults_are_classified()
    {
        Assert.True(new ConnectionStatus(ConnectionState.Faulted, null, "", []) { Fault = FaultKind.CoreMissing }.IsEnvironmentFault);
        Assert.True(new ConnectionStatus(ConnectionState.Faulted, null, "", []) { Fault = FaultKind.CoreTooOld }.IsEnvironmentFault);
        Assert.True(new ConnectionStatus(ConnectionState.Faulted, null, "", []) { Fault = FaultKind.PortConflict }.IsEnvironmentFault);
        Assert.False(new ConnectionStatus(ConnectionState.Faulted, null, "", []) { Fault = FaultKind.Server }.IsEnvironmentFault);
        Assert.False(new ConnectionStatus(ConnectionState.Faulted, null, "", []).IsEnvironmentFault);
    }
}
