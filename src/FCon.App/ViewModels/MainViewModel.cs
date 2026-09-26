using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.Abstractions.Model;
using FCon.App.Services;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Net;

namespace FCon.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private List<ServerRowViewModel> _allRows = [];
    private CancellationTokenSource? _testCts;

    [ObservableProperty] private string _selectedPage = "servers";
    [ObservableProperty] private ObservableCollection<ServerRowViewModel> _servers = [];
    [ObservableProperty] private ServerRowViewModel? _selectedServer;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private LinkState _state = LinkState.Idle;
    [ObservableProperty] private int? _linkLatencyMs;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    public MainViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;

        Subscriptions = new SubscriptionsViewModel(services, dialogs, Refresh);
        Routing = new RoutingViewModel(services, dialogs);
        Settings = new SettingsViewModel(services, dialogs);
        Logs = new LogsViewModel(services);

        // Connection state comes from the supervisor, which knows whether traffic is
        // actually flowing. The engine is only consulted for its config warnings.
        _services.Supervisor.Changed += OnLinkChanged;
        _services.Engine.StatusChanged += OnEngineWarnings;
        _services.Profiles.Changed += () => Application.Current.Dispatcher.Invoke(Refresh);
        _services.Traffic.Sampled += _ => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(TrafficRateText));
            OnPropertyChanged(nameof(TrafficTotalText));
        });

        Refresh();
    }

    public SubscriptionsViewModel Subscriptions { get; }
    public RoutingViewModel Routing { get; }
    public SettingsViewModel Settings { get; }
    public LogsViewModel Logs { get; }

    public AppSettings AppSettings => _services.Settings;

    public bool IsConnected => State is LinkState.Healthy or LinkState.Degraded;

    public bool IsTransitioning =>
        State is LinkState.Connecting or LinkState.Verifying or LinkState.Recovering;

    /// <summary>Drives the pulsing indicator: only animate while something is in flight.</summary>
    public bool IsWorking => IsTransitioning;

    /// <summary>
    /// While an attempt is in flight the button stops it rather than describing it: the
    /// supervisor now retries until told otherwise, so this has to stay the way out.
    /// </summary>
    public string ConnectButtonText => State switch
    {
        LinkState.Healthy or LinkState.Degraded => "Disconnect",
        LinkState.Connecting => "Stop connecting",
        LinkState.Verifying => "Stop verifying",
        LinkState.Recovering => "Stop reconnecting",
        _ => "Connect",
    };

    public string LinkQualityText => State switch
    {
        LinkState.Healthy => LinkLatencyMs is { } ms ? $"Healthy · {ms} ms" : "Healthy",
        LinkState.Degraded => "Unstable",
        LinkState.Verifying => "Verifying",
        LinkState.Recovering => "Recovering",
        LinkState.Failed => "Failed",
        LinkState.Connecting => "Connecting",
        _ => "Not connected",
    };

    public string LinkGrade => State switch
    {
        LinkState.Healthy => "good",
        LinkState.Degraded or LinkState.Recovering or LinkState.Verifying => "warn",
        LinkState.Failed => "bad",
        _ => "idle",
    };

    public string ActiveServerName => _services.Engine.ActiveNode?.DisplayName ?? "No server selected";

    /// <summary>Country and address the traffic actually exits from, once verified.</summary>
    public string ExitText
    {
        get
        {
            if (!IsConnected) return "";
            var exit = _services.Supervisor.Current.Exit;
            return exit is null ? "exit unknown" : exit.Describe();
        }
    }

    public bool HasExit => IsConnected && _services.Supervisor.Current.Exit is not null;

    /// <summary>Live rates, so the status bar shows the tunnel doing something.</summary>
    public string TrafficRateText
    {
        get
        {
            if (!IsConnected) return "";
            if (!_services.Traffic.Available) return "counters need sing-box";

            var s = _services.Traffic.Current;
            return $"↓ {TrafficSample.FormatRate(s.DownloadBytesPerSecond)}"
                   + $"   ↑ {TrafficSample.FormatRate(s.UploadBytesPerSecond)}";
        }
    }

    /// <summary>Session totals, which is the "size" half of the readout.</summary>
    public string TrafficTotalText
    {
        get
        {
            if (!IsConnected || !_services.Traffic.Available) return "";
            var s = _services.Traffic.Current;
            return $"{TrafficSample.FormatBytes(s.DownloadTotal + s.UploadTotal)} this session"
                   + (s.Connections > 0 ? $" · {s.Connections} conn" : "");
        }
    }

    public string ListenerSummary =>
        $"SOCKS {_services.Settings.SocksPort} · HTTP {_services.Settings.HttpPort}";

    // ------------------------------------------------------------- listing

    public void Refresh()
    {
        var subscriptionNames = _services.Profiles.Subscriptions
            .ToDictionary(s => s.Id, s => s.Name);

        var activeId = _services.Engine.ActiveNode?.Id;

        _allRows = _services.Profiles.ActiveNodes
            .Select(node =>
            {
                var descriptor = _services.Registry.ById(node.Protocol)?.Descriptor;
                var row = new ServerRowViewModel(node, descriptor) { IsActive = node.Id == activeId };
                if (node.SubscriptionId is { } sid && subscriptionNames.TryGetValue(sid, out var name))
                    row.SubscriptionName = name;
                return row;
            })
            .ToList();

        RefreshQualityColumns();
        ApplyFilter();
        Subscriptions.Refresh();
        OnPropertyChanged(nameof(ActiveServerName));
    }

    private void RefreshQualityColumns()
    {
        foreach (var row in _allRows) row.Quality = _services.Quality.Get(row.Id);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selectedId = SelectedServer?.Id;
        var query = SearchText.Trim();

        var filtered = query.Length == 0
            ? _allRows
            : _allRows.Where(r => r.Matches(query)).ToList();

        Servers = new ObservableCollection<ServerRowViewModel>(filtered);
        SelectedServer = filtered.FirstOrDefault(r => r.Id == selectedId) ?? filtered.FirstOrDefault();
    }

    // ---------------------------------------------------------- connection

    private void OnLinkChanged(LinkSnapshot snapshot)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            State = snapshot.State;
            LinkLatencyMs = snapshot.LatencyMs;
            StatusText = snapshot.State switch
            {
                LinkState.Healthy => "Connected",
                LinkState.Degraded => "Unstable",
                LinkState.Connecting => "Connecting",
                LinkState.Verifying => "Verifying",
                LinkState.Recovering => "Recovering",
                LinkState.Failed => "Failed",
                _ => "Disconnected",
            };
            StatusDetail = snapshot.Message;

            foreach (var row in _allRows) row.IsActive = row.Id == snapshot.Node?.Id;
            RefreshQualityColumns();

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsTransitioning));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(ActiveServerName));
            OnPropertyChanged(nameof(LinkQualityText));
            OnPropertyChanged(nameof(LinkGrade));
            OnPropertyChanged(nameof(ExitText));
            OnPropertyChanged(nameof(HasExit));
            OnPropertyChanged(nameof(TrafficRateText));
            OnPropertyChanged(nameof(TrafficTotalText));
            ToggleConnectionCommand.NotifyCanExecuteChanged();

            // No dialog on failure. The supervisor keeps retrying until the user stops it,
            // so a lost connection is a passing state, not something to interrupt for -
            // and a modal box on every drop is exactly what makes a flaky link unusable.
            // The status card, its colour and the log carry the same information.
        });
    }

    /// <summary>Engine-level config warnings still belong in the log.</summary>
    private void OnEngineWarnings(ConnectionStatus status)
    {
        foreach (var warning in status.Warnings)
            _services.Log.Add(new EngineLogLine(DateTimeOffset.Now, "config: " + warning, true));
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        // Anything already in flight is stopped, not restarted. Retrying is unbounded, so
        // this button is the only way out and must work in every non-idle state.
        if (IsConnected || IsTransitioning || State == LinkState.Failed)
        {
            await _services.Supervisor.DisconnectAsync();
            return;
        }

        // "Prefer best server" lets the ranking decide rather than whatever was last used.
        if (_services.Settings.PreferBestServer && SelectedServer is null)
        {
            await ConnectBestAsync();
            return;
        }

        var target = SelectedServer?.Node
                     ?? _allRows.FirstOrDefault(r => r.Id == _services.Settings.ActiveNodeId)?.Node
                     ?? _allRows.FirstOrDefault()?.Node;

        if (target is null)
        {
            _dialogs.ShowInfo("No servers", "Add a server or import a subscription first.");
            return;
        }

        await ConnectNodeAsync(target);
    }

    /// <summary>
    /// Always available. It used to be disabled while transitioning, which was harmless
    /// when an attempt gave up on its own; now that retrying never stops by itself, that
    /// would trap the user in a reconnect loop with no way to cancel.
    /// </summary>
    private bool CanToggleConnection() => true;

    /// <summary>Connect to whichever server currently ranks best on measured quality.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ConnectBestAsync()
    {
        if (_allRows.Count == 0)
        {
            _dialogs.ShowInfo("No servers", "Add a server or import a subscription first.");
            return;
        }

        if (IsConnected) await _services.Supervisor.DisconnectAsync();
        await _services.Supervisor.ConnectBestAsync();
        PersistActiveNode();
    }

    private async Task ConnectNodeAsync(ProxyNode target)
    {
        var issues = _services.Importer.Validate(target);
        if (issues.Count > 0)
        {
            _dialogs.ShowError("This server is not usable",
                string.Join(Environment.NewLine, issues));
            return;
        }

        await _services.Supervisor.ConnectAsync(target);
        PersistActiveNode();
    }

    /// <summary>
    /// Remember whichever server we ended up on, which after a failover is not
    /// necessarily the one the user picked.
    /// </summary>
    private void PersistActiveNode()
    {
        var actual = _services.Supervisor.Current.Node;
        if (actual is null) return;

        _services.Settings.ActiveNodeId = actual.Id;
        _ = _services.SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task ConnectToAsync(ServerRowViewModel? row)
    {
        if (row is null) return;
        SelectedServer = row;
        if (IsConnected) await _services.Supervisor.DisconnectAsync();
        await ConnectNodeAsync(row.Node);
    }

    // ------------------------------------------------------------- editing

    [RelayCommand]
    private void AddServer()
    {
        var template = new ProxyNode
        {
            Protocol = "vless",
            Server = "",
            Port = 443,
            Remark = "New server",
        };

        if (_dialogs.EditNode(template, isNew: true) is { } created)
            _services.Profiles.UpsertNode(created);
    }

    [RelayCommand]
    private void EditServer(ServerRowViewModel? row)
    {
        row ??= SelectedServer;
        if (row is null) return;

        if (_dialogs.EditNode(row.Node, isNew: false) is { } edited)
            _services.Profiles.UpsertNode(edited);
    }

    [RelayCommand]
    private void DuplicateServer(ServerRowViewModel? row)
    {
        row ??= SelectedServer;
        if (row is null) return;

        _services.Profiles.UpsertNode(row.Node with
        {
            Id = Guid.NewGuid(),
            Remark = row.Node.DisplayName + " (copy)",
            SubscriptionId = null,
            LatencyMs = null,
        });
    }

    [RelayCommand]
    private void DeleteServers(IList<object>? selection)
    {
        var ids = ResolveSelection(selection);
        if (ids.Count == 0) return;

        var label = ids.Count == 1 ? "this server" : $"these {ids.Count} servers";
        if (!_dialogs.Confirm("Delete servers", $"Remove {label} from the list?")) return;

        _services.Profiles.RemoveNodes(ids);
    }

    [RelayCommand]
    private void ImportFromClipboard()
    {
        var text = SafeClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            _dialogs.ShowInfo("Nothing to import", "The clipboard does not contain any text.");
            return;
        }
        ImportText(text);
    }

    [RelayCommand]
    private void ImportFromText()
    {
        var text = _dialogs.PromptText(
            "Import servers",
            "Paste one or more share links, or a base64 subscription payload.",
            multiline: true);

        if (!string.IsNullOrWhiteSpace(text)) ImportText(text);
    }

    private void ImportText(string text)
    {
        var result = _services.Importer.Import(text);

        if (!result.AnySucceeded)
        {
            _dialogs.ShowError("Import failed", result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors.Take(10))
                : "No recognisable server links were found.");
            return;
        }

        _services.Profiles.AddNodes(result.Nodes);

        var message = $"Imported {result.Nodes.Count} server(s).";
        if (result.Errors.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}Skipped {result.Errors.Count}:"
                       + Environment.NewLine
                       + string.Join(Environment.NewLine, result.Errors.Take(8));

        _dialogs.ShowInfo("Import complete", message);
    }

    [RelayCommand]
    private void CopyLink(ServerRowViewModel? row)
    {
        row ??= SelectedServer;
        if (row is null) return;

        var plugin = _services.Registry.ById(row.Node.Protocol);
        if (plugin is null) return;

        TrySetClipboard(plugin.BuildLink(row.Node));
    }

    [RelayCommand]
    private void ShowGeneratedConfig(ServerRowViewModel? row)
    {
        row ??= SelectedServer;
        if (row is null) return;

        try
        {
            var config = _services.Engine.Generate(row.Node);
            _dialogs.ShowConfig($"{_services.Settings.Engine} config — {row.Name}", config.ToJson());
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.ShowError("Cannot generate configuration", ex.Message);
        }
    }

    [RelayCommand]
    private void Deduplicate()
    {
        var removed = _services.Profiles.Deduplicate();
        _dialogs.ShowInfo("Remove duplicates", removed == 0
            ? "No duplicate servers were found."
            : $"Removed {removed} duplicate server(s).");
    }

    // ------------------------------------------------------------- testing

    [RelayCommand]
    private async Task TestAllAsync()
    {
        await TestAsync(_allRows.Select(r => r.Node).ToList());
    }

    [RelayCommand]
    private async Task TestSelectedAsync(IList<object>? selection)
    {
        var ids = ResolveSelection(selection);
        var nodes = _allRows.Where(r => ids.Contains(r.Id)).Select(r => r.Node).ToList();
        if (nodes.Count == 0) return;
        await TestAsync(nodes);
    }

    private async Task TestAsync(IReadOnlyList<ProxyNode> nodes)
    {
        if (nodes.Count == 0) return;

        if (_testCts is not null)
        {
            await _testCts.CancelAsync();
            _testCts.Dispose();
        }
        _testCts = new CancellationTokenSource();

        IsBusy = true;
        var done = 0;
        BusyText = $"Testing 0/{nodes.Count}";

        var progress = new Progress<LatencyResult>(result =>
        {
            done++;
            BusyText = $"Testing {done}/{nodes.Count}"
                       + (result.Method == LatencyTester.MethodUrl ? " (real requests)" : " (handshake)");

            var row = _allRows.FirstOrDefault(r => r.Id == result.NodeId);
            if (row is not null) row.Node = row.Node with { LatencyMs = result.Milliseconds };
        });

        try
        {
            var results = await _services.Latency.TestAsync(nodes, progress, _testCts.Token);

            foreach (var result in results)
            {
                _services.Profiles.SetLatency(result.NodeId, result.Milliseconds);

                if (result.Method == LatencyTester.MethodUrl)
                {
                    // A real request through the tunnel is the same evidence the
                    // supervisor collects: it proves the server works, not just answers.
                    if (result.Reachable)
                    {
                        _services.Quality.RecordSuccess(result.NodeId);
                        _services.Quality.RecordLatency(result.NodeId, result.Milliseconds);
                    }
                    else
                    {
                        _services.Quality.RecordFailure(result.NodeId, result.Error);
                    }
                }
                else
                {
                    // A handshake proves reachability, not that the tunnel works, so an
                    // unreachable server counts against it while a fast one only informs
                    // the latency estimate.
                    if (result.Reachable) _services.Quality.RecordLatency(result.NodeId, result.Milliseconds);
                    else _services.Quality.RecordFailure(result.NodeId, result.Error);
                }
            }

            RefreshQualityColumns();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer test run; the newer one owns the UI from here.
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    [RelayCommand]
    private async Task TestThroughTunnelAsync()
    {
        if (!IsConnected || _services.Engine.ActiveNode is not { } active)
        {
            _dialogs.ShowInfo("Not connected", "Connect first to measure latency through the tunnel.");
            return;
        }

        IsBusy = true;
        BusyText = "Measuring real delay...";
        try
        {
            var result = await HealthProbe.CheckAsync(
                _services.Settings.HttpPort,
                _services.Settings.LatencyTestUrl,
                _services.Settings.LatencyTimeoutMs);

            if (result.Ok) _services.Quality.RecordLatency(active.Id, result.LatencyMs);
            RefreshQualityColumns();

            _dialogs.ShowInfo("Real delay", result.Ok
                ? $"{active.DisplayName}: {result.LatencyMs} ms through the tunnel."
                : $"{active.DisplayName}: {result.Describe()}.");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>
    /// Download throughput through the active tunnel. Latency and speed are different
    /// questions — a server can answer instantly and still trickle data.
    /// </summary>
    [RelayCommand]
    private async Task SpeedTestAsync()
    {
        if (!IsConnected || _services.Engine.ActiveNode is not { } active)
        {
            _dialogs.ShowInfo("Not connected", "Connect first to measure speed.");
            return;
        }

        IsBusy = true;
        BusyText = "Measuring throughput...";
        try
        {
            var bytesPerSecond = await HealthProbe.MeasureThroughputAsync(
                _services.Settings.HttpPort, _services.Settings.SpeedTestUrl);

            var mbits = bytesPerSecond * 8 / 1_000_000;
            _dialogs.ShowInfo("Speed test", bytesPerSecond <= 0
                ? $"{active.DisplayName}: no data came through."
                : $"{active.DisplayName}: {mbits:0.0} Mbit/s down "
                  + $"({bytesPerSecond / 1_048_576:0.0} MB/s).");
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>Rank by everything we have learned, not just the last latency reading.</summary>
    [RelayCommand]
    private void SortByQuality()
    {
        var ranked = _services.Quality.Rank(_allRows.Select(r => r.Node)).ToList();
        var order = ranked.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);
        _allRows = [.. _allRows.OrderBy(r => order.TryGetValue(r.Id, out var i) ? i : int.MaxValue)];
        ApplyFilter();
    }

    [RelayCommand]
    private async Task SortByLatencyAsync()
    {
        await Task.CompletedTask;
        var ordered = _allRows
            .OrderBy(r => r.LatencyMs is null or < 0 ? int.MaxValue : r.LatencyMs.Value)
            .ToList();
        _allRows = ordered;
        ApplyFilter();
    }

    // -------------------------------------------------------------- helpers

    private List<Guid> ResolveSelection(IList<object>? selection)
    {
        if (selection is { Count: > 0 })
            return selection.OfType<ServerRowViewModel>().Select(r => r.Id).ToList();
        return SelectedServer is null ? [] : [SelectedServer.Id];
    }

    /// <summary>The clipboard is shared state and can be locked by another process.</summary>
    private static string? SafeClipboardText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
        return null;
    }

    private static void TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
    }
}
