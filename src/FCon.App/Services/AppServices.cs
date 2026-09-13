using FCon.Core;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Import;
using FCon.Core.Net;
using FCon.Core.Plugins;
using FCon.Core.Storage;
using FCon.Core.Subscriptions;

namespace FCon.App.Services;

/// <summary>
/// Composition root. Small enough that a container would add ceremony without buying
/// anything: one instance is created at startup and handed to the view models.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly JsonStore<AppSettings> _settingsStore;
    private readonly JsonStore<RoutingProfile> _routingStore;

    public AppServices()
    {
        AppPaths.EnsureCreated();

        _settingsStore = new JsonStore<AppSettings>(AppPaths.SettingsFile, () => new AppSettings());
        _routingStore = new JsonStore<RoutingProfile>(AppPaths.RoutingFile, RoutingProfile.CreateDefault);

        Settings = _settingsStore.Load();
        Routing = _routingStore.Load();

        Registry = PluginRegistry.CreateDefault();
        Profiles = new ProfileStore();
        Importer = new LinkImporter(Registry);
        Subscriptions = new SubscriptionService(Profiles, Importer, () => Settings);
        Engine = new EngineController(Registry, () => Settings, () => Routing);
        Log = new LogBuffer();
        Quality = new QualityStore();

        Supervisor = new ConnectionSupervisor(
            Engine,
            Quality,
            () => Settings,
            // ActiveNodes, not Nodes: a deactivated subscription must not be picked as
            // "best" or failed over to, which is the whole point of deactivating it.
            () => Profiles.ActiveNodes,
            (message, isError) => Log.Add(new EngineLogLine(DateTimeOffset.Now, message, isError)));

        Traffic = new TrafficMeter(() => Settings);

        // Counters only mean anything while a tunnel is up.
        Supervisor.Changed += snapshot =>
        {
            if (snapshot.IsUsable) Traffic.Start();
            else Traffic.Stop();
        };

        Engine.LogReceived += line => Log.Add(line);

        EnsureFreePorts();
    }

    /// <summary>
    /// On first run, move off any listener port another proxy client already owns. Done
    /// once and then recorded, so a port the user chose deliberately is never overridden.
    /// </summary>
    private void EnsureFreePorts()
    {
        if (Settings.PortsInitialised) return;

        var socks = PortProbe.FindFree(Settings.SocksPort);
        var http = PortProbe.FindFree(socks + 1);
        var api = Settings.ApiPort > 0 ? PortProbe.FindFree(http + 1) : 0;

        if (socks != Settings.SocksPort || http != Settings.HttpPort)
        {
            Log.Add(new EngineLogLine(
                DateTimeOffset.Now,
                $"Ports {Settings.SocksPort}/{Settings.HttpPort} were taken by another program; "
                + $"using {socks}/{http} instead. Change them in Settings if you prefer.",
                false));
        }

        Settings.SocksPort = socks;
        Settings.HttpPort = http;
        Settings.ApiPort = api;
        Settings.PortsInitialised = true;
        _ = SaveSettingsAsync();
    }

    public AppSettings Settings { get; }
    public RoutingProfile Routing { get; }
    public PluginRegistry Registry { get; }
    public ProfileStore Profiles { get; }
    public LinkImporter Importer { get; }
    public SubscriptionService Subscriptions { get; }
    public EngineController Engine { get; }
    public LogBuffer Log { get; }
    public QualityStore Quality { get; }
    public ConnectionSupervisor Supervisor { get; }
    public TrafficMeter Traffic { get; }

    public Task SaveSettingsAsync() => _settingsStore.SaveAsync(Settings);

    public Task SaveRoutingAsync() => _routingStore.SaveAsync(Routing);

    public async ValueTask DisposeAsync()
    {
        await Traffic.DisposeAsync().ConfigureAwait(false);
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
        await SaveSettingsAsync().ConfigureAwait(false);
        await SaveRoutingAsync().ConfigureAwait(false);
        // Profile writes are queued in the background; do not exit before they land.
        await Profiles.FlushAsync().ConfigureAwait(false);
        await Quality.FlushAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Bounded in-memory log ring. The engine can be chatty at debug level, so the buffer
/// is capped and old lines are dropped rather than growing without limit.
/// </summary>
public sealed class LogBuffer(int capacity = 3000)
{
    private readonly Queue<EngineLogLine> _lines = new();
    private readonly Lock _gate = new();

    public event Action<EngineLogLine>? LineAdded;

    public void Add(EngineLogLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
        }
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<EngineLogLine> Snapshot()
    {
        lock (_gate) return [.. _lines];
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}
