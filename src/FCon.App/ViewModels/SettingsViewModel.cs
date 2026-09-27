using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.Abstractions.Plugins;
using FCon.App.Services;
using FCon.Core;
using FCon.Core.Config;
using FCon.Core.Engine;
using FCon.Core.Health;
using FCon.Core.Net;
using Microsoft.Win32;
using FCon.Core.Localization;

namespace FCon.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "KVN";
    private const string LegacyRunValueName = "FCon";

    private readonly AppServices _services;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private ObservableCollection<PluginRowViewModel> _plugins = [];
    [ObservableProperty] private ObservableCollection<EngineRowViewModel> _engineRows = [];
    [ObservableProperty] private ObservableCollection<Advice> _advice = [];

    public SettingsViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        RefreshPlugins();
        RefreshEngineStatus();
        RefreshAdvice();
    }

    private AppSettings S => _services.Settings;

    public IReadOnlyList<EngineKind> Engines { get; } = Enum.GetValues<EngineKind>();
    public IReadOnlyList<TrafficMode> TrafficModes { get; } = Enum.GetValues<TrafficMode>();
    public IReadOnlyList<string> LogLevels { get; } = ["debug", "info", "warning", "error", "none"];
    public IReadOnlyList<string> Themes { get; } = ["system", "dark", "light"];

    /// <summary>Language names are shown in their own language, never translated.</summary>
    public IReadOnlyList<LanguageOption> Languages =>
    [
        new("system", L.T("Lang_System")),
        new("en", "English"),
        new("ru", "Русский"),
    ];

    public LanguageOption Language
    {
        get => Languages.FirstOrDefault(l => l.Value == S.Language) ?? Languages[0];
        set
        {
            if (value is null || S.Language == value.Value) return;
            S.Language = value.Value;
            Localizer.Instance.Apply(value.Value);
            _ = _services.SaveSettingsAsync();
            OnPropertyChanged();
        }
    }

    /// <summary>Re-read every computed string after the language changed.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        RefreshEngineStatus();
        RefreshPlugins();
        RefreshAdvice();
    }
    public IReadOnlyList<string> TunStacks { get; } = ["system", "gvisor", "mixed"];

    public EngineKind Engine
    {
        get => S.Engine;
        set => Set(() => S.Engine = value, S.Engine != value, alsoRefreshEngine: true);
    }

    public TrafficMode TrafficMode
    {
        get => S.TrafficMode;
        set => Set(() => S.TrafficMode = value, S.TrafficMode != value);
    }

    public int SocksPort
    {
        get => S.SocksPort;
        set => Set(() => S.SocksPort = value, S.SocksPort != value);
    }

    public int HttpPort
    {
        get => S.HttpPort;
        set => Set(() => S.HttpPort = value, S.HttpPort != value);
    }

    public int ApiPort
    {
        get => S.ApiPort;
        set => Set(() => S.ApiPort = value, S.ApiPort != value);
    }

    public bool AllowLan
    {
        get => S.AllowLan;
        set => Set(() => S.AllowLan = value, S.AllowLan != value);
    }

    public bool EnableSniffing
    {
        get => S.EnableSniffing;
        set => Set(() => S.EnableSniffing = value, S.EnableSniffing != value);
    }

    public bool RouteByDomain
    {
        get => S.RouteByDomain;
        set => Set(() => S.RouteByDomain = value, S.RouteByDomain != value);
    }

    public string LogLevel
    {
        get => S.LogLevel;
        set => Set(() => S.LogLevel = value, S.LogLevel != value);
    }

    public string RemoteDns
    {
        get => S.RemoteDns;
        set => Set(() => S.RemoteDns = value, S.RemoteDns != value);
    }

    public string DirectDns
    {
        get => S.DirectDns;
        set => Set(() => S.DirectDns = value, S.DirectDns != value);
    }

    public string BootstrapDns
    {
        get => S.BootstrapDns;
        set => Set(() => S.BootstrapDns = value, S.BootstrapDns != value);
    }

    public bool FakeIp
    {
        get => S.FakeIp;
        set => Set(() => S.FakeIp = value, S.FakeIp != value);
    }

    public string TunInterfaceName
    {
        get => S.TunInterfaceName;
        set => Set(() => S.TunInterfaceName = value, S.TunInterfaceName != value);
    }

    public string TunStack
    {
        get => S.TunStack;
        set => Set(() => S.TunStack = value, S.TunStack != value);
    }

    public int TunMtu
    {
        get => S.TunMtu;
        set => Set(() => S.TunMtu = value, S.TunMtu != value);
    }

    // --- health and resilience ---

    public bool VerifyOnConnect
    {
        get => S.VerifyOnConnect;
        set => Set(() => S.VerifyOnConnect = value, S.VerifyOnConnect != value, alsoRefreshAdvice: true);
    }

    public bool ContinuousHealthCheck
    {
        get => S.ContinuousHealthCheck;
        set => Set(() => S.ContinuousHealthCheck = value, S.ContinuousHealthCheck != value, alsoRefreshAdvice: true);
    }

    public int HealthCheckIntervalSeconds
    {
        get => S.HealthCheckIntervalSeconds;
        set => Set(() => S.HealthCheckIntervalSeconds = value, S.HealthCheckIntervalSeconds != value);
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set => Set(() => S.AutoReconnect = value, S.AutoReconnect != value);
    }

    public bool AutoFailover
    {
        get => S.AutoFailover;
        set => Set(() => S.AutoFailover = value, S.AutoFailover != value);
    }

    public bool PreferBestServer
    {
        get => S.PreferBestServer;
        set => Set(() => S.PreferBestServer = value, S.PreferBestServer != value);
    }

    public bool InCoreFailover
    {
        get => S.InCoreFailover;
        set => Set(() => S.InCoreFailover = value, S.InCoreFailover != value, alsoRefreshAdvice: true);
    }

    public int FailoverPoolSize
    {
        get => S.FailoverPoolSize;
        set => Set(() => S.FailoverPoolSize = Math.Clamp(value, 1, 256), S.FailoverPoolSize != value);
    }

    public bool AutoStartLastServer
    {
        get => S.AutoStartLastServer;
        set => Set(() => S.AutoStartLastServer = value, S.AutoStartLastServer != value);
    }

    public bool StartMinimized
    {
        get => S.StartMinimized;
        set => Set(() => S.StartMinimized = value, S.StartMinimized != value);
    }

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set => Set(() => S.CloseToTray = value, S.CloseToTray != value);
    }

    public bool AutoUpdateSubscriptions
    {
        get => S.AutoUpdateSubscriptions;
        set => Set(() => S.AutoUpdateSubscriptions = value, S.AutoUpdateSubscriptions != value);
    }

    public int SubscriptionUpdateHours
    {
        get => S.SubscriptionUpdateHours;
        set => Set(() => S.SubscriptionUpdateHours = value, S.SubscriptionUpdateHours != value);
    }

    public string LatencyTestUrl
    {
        get => S.LatencyTestUrl;
        set => Set(() => S.LatencyTestUrl = value, S.LatencyTestUrl != value);
    }

    public string Theme
    {
        get => S.Theme;
        set
        {
            if (S.Theme == value) return;
            S.Theme = value;
            ThemeManager.Apply(value);
            _ = _services.SaveSettingsAsync();
            OnPropertyChanged();
        }
    }

    public bool LaunchAtLogin
    {
        get => S.LaunchAtLogin;
        set
        {
            if (S.LaunchAtLogin == value) return;
            S.LaunchAtLogin = value;
            ApplyLaunchAtLogin(value);
            _ = _services.SaveSettingsAsync();
            OnPropertyChanged();
        }
    }

    public string DataDirectory => AppPaths.DataDirectory;

    // ------------------------------------------------------------- commands

    /// <summary>Re-run the configuration review against the current settings and server.</summary>
    [RelayCommand]
    private void RefreshAdvice()
    {
        var node = _services.Settings.ActiveNodeId is { } id
            ? _services.Profiles.FindNode(id)
            : null;

        Advice = new ObservableCollection<Advice>(
            ConfigAdvisor.Inspect(_services.Settings, _services.Routing, node));

        OnPropertyChanged(nameof(AdviceSummary));
        OnPropertyChanged(nameof(AdviceGrade));
        OnPropertyChanged(nameof(HasAdvice));
    }

    public bool HasAdvice => Advice.Count > 0;

    public string AdviceSummary
    {
        get
        {
            if (Advice.Count == 0) return L.T("NoProblems");
            var critical = Advice.Count(a => a.Severity == AdviceSeverity.Critical);
            var warnings = Advice.Count(a => a.Severity == AdviceSeverity.Warning);

            return critical > 0
                ? L.F("SeriousAndWarnings", critical, warnings)
                : warnings > 0
                    ? L.F("WarningsCount", warnings)
                    : L.F("Suggestions", Advice.Count);
        }
    }

    public string AdviceGrade =>
        Advice.Any(a => a.Severity == AdviceSeverity.Critical) ? "bad"
        : Advice.Any(a => a.Severity == AdviceSeverity.Warning) ? "warn"
        : "good";

    /// <summary>Apply every finding that can be corrected without a judgement call.</summary>
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var applied = ConfigAdvisor.ApplyFixes(_services.Settings, _services.Routing);

        if (applied.Count == 0)
        {
            _dialogs.ShowInfo(L.T("Dlg_Optimise"), L.T("NothingLeftToFix"));
            return;
        }

        await _services.SaveSettingsAsync();
        await _services.SaveRoutingAsync();

        RefreshAdvice();
        OnPropertyChanged(string.Empty);

        _dialogs.ShowInfo(L.T("Dlg_Optimise"),
            L.T("Applied") + Environment.NewLine + string.Join(Environment.NewLine,
                applied.Select(a => "  - " + a)));
    }

    [RelayCommand]
    private void RefreshEngineStatus() =>
        EngineRows = new ObservableCollection<EngineRowViewModel>(
            Engines.Select(kind => EngineRowViewModel.Create(kind, kind == S.Engine)));

    [RelayCommand]
    private void RefreshPlugins()
    {
        var rows = _services.Registry.Plugins
            .Select(p => new PluginRowViewModel(
                p.Descriptor.DisplayName,
                p.Descriptor.Id,
                string.Join(", ", p.Descriptor.Schemes.Select(s => s + "://")),
                DescribeEngines(p.Descriptor.Engines),
                p.IsBuiltin ? L.T("built_in") : p.Source,
                null))
            .ToList();

        rows.AddRange(_services.Registry.Failures.Select(f =>
            new PluginRowViewModel(Path.GetFileName(f.Path), "", "", "", f.Path, f.Reason)));

        Plugins = new ObservableCollection<PluginRowViewModel>(rows);
    }

    /// <summary>
    /// Fetch and install the latest stable core. The archive is verified against the
    /// checksum the release publishes before anything is written next to the app.
    /// </summary>
    [RelayCommand]
    private Task InstallEngineAsync(EngineRowViewModel? row) => InstallRowAsync(row);

    /// <summary>Install a core by kind; used by the first-run prompt and the Servers banner.</summary>
    public Task<bool> InstallEngineAsync(EngineKind kind)
    {
        var row = EngineRows.FirstOrDefault(r => r.Kind == kind) ?? EngineRowViewModel.Create(kind, kind == S.Engine);
        return InstallRowAsync(row);
    }

    private async Task<bool> InstallRowAsync(EngineRowViewModel? row)
    {
        if (row is null || row.IsBusy) return false;

        // A running core holds its executable locked, and swapping it under a live
        // tunnel is not something to do silently.
        if (_services.Supervisor.Current.State != LinkState.Idle && row.Kind == S.Engine)
        {
            if (!_dialogs.Confirm(L.T("Dlg_DisconnectToUpdate"), L.F("DisconnectToUpdateBody", row.DisplayName)))
                return false;
            await _services.Supervisor.DisconnectAsync();
        }

        row.IsBusy = true;
        row.Progress = L.T("Starting");
        CoreInstallProgress = L.T("Starting");
        try
        {
            var progress = new Progress<string>(text => row.Progress = CoreInstallProgress = text);
            var result = await _services.Downloader.InstallAsync(row.Kind, progress);

            if (result.Succeeded) _dialogs.ShowInfo(L.T("Dlg_CoreInstalled"), result.Message);
            else _dialogs.ShowError(L.T("Dlg_CoreInstallFailed"), result.Message);
            return result.Succeeded;
        }
        finally
        {
            row.IsBusy = false;
            row.Progress = null;
            CoreInstallProgress = null;
            RefreshEngineStatus();
        }
    }

    /// <summary>Progress line shared with the Servers page banner while a core downloads.</summary>
    [ObservableProperty] private string? _coreInstallProgress;

    /// <summary>
    /// Open the upstream releases page for a manual install — the way to go when GitHub
    /// is unreachable from here, or when a specific version is wanted.
    /// </summary>
    [RelayCommand]
    private void GetEngine(string? which)
    {
        var url = which == "xray"
            ? "https://github.com/XTLS/Xray-core/releases"
            : "https://github.com/SagerNet/sing-box/releases";

        var folder = Path.Combine(
            AppPaths.EnginesDirectory,
            which == "xray" ? "xray" : "sing-box");
        Directory.CreateDirectory(folder);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowInfo(L.T("Dlg_DownloadCore"), url);
            return;
        }

        _dialogs.ShowInfo(L.T("Dlg_InstallCore"), L.F("InstallCoreBody", folder).ReplaceLineEndings());

        OpenInExplorer(folder);
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(AppPaths.PluginsDirectory);
        OpenInExplorer(AppPaths.PluginsDirectory);
    }

    [RelayCommand]
    private void OpenEnginesFolder()
    {
        Directory.CreateDirectory(AppPaths.EnginesDirectory);
        OpenInExplorer(AppPaths.EnginesDirectory);
    }

    [RelayCommand]
    private void PickFreePorts()
    {
        SocksPort = PortProbe.FindFree(S.SocksPort);
        HttpPort = PortProbe.FindFree(SocksPort + 1);
        ApiPort = PortProbe.FindFree(HttpPort + 1);
        _dialogs.ShowInfo(L.T("Dlg_PortsUpdated"), L.F("PortsUpdatedBody", SocksPort, HttpPort, ApiPort));
    }

    [RelayCommand]
    private void ClearSystemProxy()
    {
        SystemProxy.Disable();
        _dialogs.ShowInfo(L.T("Dlg_SystemProxy"), L.T("ProxyRestored"));
    }

    // -------------------------------------------------------------- helpers

    private void Set(
        Action assign,
        bool changed,
        bool alsoRefreshEngine = false,
        bool alsoRefreshAdvice = false,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!changed) return;
        assign();
        _ = _services.SaveSettingsAsync();
        OnPropertyChanged(name);
        if (alsoRefreshEngine) RefreshEngineStatus();
        if (alsoRefreshAdvice) RefreshAdvice();
    }

    private static string DescribeEngines(EngineSupport support) => support switch
    {
        EngineSupport.Both => "sing-box, Xray",
        EngineSupport.SingBox => "sing-box",
        EngineSupport.Xray => "Xray",
        _ => "none",
    };

    private static void ApplyLaunchAtLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            // The entry from before the rename would launch an executable that no longer exists.
            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Group policy can lock the Run key; the preference simply will not stick.
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing actionable — the folder path is also shown in the UI.
        }
    }
}

public sealed record PluginRowViewModel(
    string Name,
    string Id,
    string Schemes,
    string Engines,
    string Source,
    string? Error)
{
    public bool HasError => Error is not null;
    public string StatusText => Error ?? L.T("Plugin_Loaded");
}

public sealed record LanguageOption(string Value, string Label);
