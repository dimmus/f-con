using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.App.Services;
using FCon.Core.Config;

namespace FCon.App.ViewModels;

public sealed partial class RoutingViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private ObservableCollection<RuleRowViewModel> _rules = [];
    [ObservableProperty] private RuleRowViewModel? _selected;

    public RoutingViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        Reload();
    }

    public RoutingProfile Profile => _services.Routing;

    public bool BypassPrivateNetworks
    {
        get => Profile.BypassPrivateNetworks;
        set => SetPreset(v => Profile.BypassPrivateNetworks = v, value);
    }

    public bool BypassMicrosoftServices
    {
        get => Profile.BypassMicrosoftServices;
        set => SetPreset(v => Profile.BypassMicrosoftServices = v, value);
    }

    public bool BlockQuic
    {
        get => Profile.BlockQuic;
        set => SetPreset(v => Profile.BlockQuic = v, value);
    }

    public bool BlockAds
    {
        get => Profile.BlockAds;
        set => SetPreset(v => Profile.BlockAds = v, value);
    }

    public bool ResolveDomainsForIpRules
    {
        get => Profile.ResolveDomainsForIpRules;
        set => SetPreset(v => Profile.ResolveDomainsForIpRules = v, value);
    }

    public RoutingMode Mode
    {
        get => _services.Settings.RoutingMode;
        set
        {
            if (_services.Settings.RoutingMode == value) return;
            _services.Settings.RoutingMode = value;
            _ = _services.SaveSettingsAsync();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<RoutingMode> Modes { get; } = Enum.GetValues<RoutingMode>();

    public void Reload()
    {
        Rules = new ObservableCollection<RuleRowViewModel>(
            Profile.Rules.Select(r => new RuleRowViewModel(r, Persist)));
        Selected = Rules.FirstOrDefault();
    }

    private void SetPreset(Action<bool> assign, bool value)
    {
        assign(value);
        Persist();
        OnPropertyChanged(string.Empty);
    }

    private void Persist() => _ = _services.SaveRoutingAsync();

    [RelayCommand]
    private void AddRule()
    {
        var rule = new RoutingRule { Name = "New rule", Action = RuleAction.Direct };
        Profile.Rules.Add(rule);
        Persist();
        Reload();
        Selected = Rules.LastOrDefault();
    }

    [RelayCommand]
    private void RemoveRule(RuleRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        Profile.Rules.Remove(row.Model);
        Persist();
        Reload();
    }

    [RelayCommand]
    private void MoveUp(RuleRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        var index = Profile.Rules.IndexOf(row.Model);
        if (index <= 0) return;

        (Profile.Rules[index - 1], Profile.Rules[index]) = (Profile.Rules[index], Profile.Rules[index - 1]);
        Persist();
        Reload();
        Selected = Rules.FirstOrDefault(r => r.Model == row.Model);
    }

    [RelayCommand]
    private void MoveDown(RuleRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        var index = Profile.Rules.IndexOf(row.Model);
        if (index < 0 || index >= Profile.Rules.Count - 1) return;

        (Profile.Rules[index + 1], Profile.Rules[index]) = (Profile.Rules[index], Profile.Rules[index + 1]);
        Persist();
        Reload();
        Selected = Rules.FirstOrDefault(r => r.Model == row.Model);
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (!_dialogs.Confirm("Reset routing", "Discard all rules and restore the defaults?")) return;

        var defaults = RoutingProfile.CreateDefault();
        Profile.Rules.Clear();
        Profile.Rules.AddRange(defaults.Rules);
        Profile.BypassPrivateNetworks = defaults.BypassPrivateNetworks;
        Profile.BypassMicrosoftServices = defaults.BypassMicrosoftServices;
        Profile.BlockQuic = defaults.BlockQuic;
        Profile.BlockAds = defaults.BlockAds;
        Profile.ResolveDomainsForIpRules = defaults.ResolveDomainsForIpRules;

        Persist();
        Reload();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Editable projection of one rule. The multi-value fields are edited as newline-separated
/// text, which is far quicker for a list of domains than a grid of single-value rows.
/// </summary>
public sealed partial class RuleRowViewModel(RoutingRule model, Action persist) : ObservableObject
{
    public RoutingRule Model { get; } = model;

    public IReadOnlyList<RuleAction> Actions { get; } = Enum.GetValues<RuleAction>();

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; Changed(); }
    }

    public string Name
    {
        get => Model.Name;
        set { Model.Name = value; Changed(); }
    }

    public RuleAction Action
    {
        get => Model.Action;
        set { Model.Action = value; Changed(); }
    }

    public string Domains
    {
        get => string.Join(Environment.NewLine, Model.Domains);
        set { Model.Domains = SplitLines(value); Changed(); }
    }

    public string Ips
    {
        get => string.Join(Environment.NewLine, Model.Ips);
        set { Model.Ips = SplitLines(value); Changed(); }
    }

    public string Ports
    {
        get => string.Join(", ", Model.Ports);
        set { Model.Ports = SplitCommas(value); Changed(); }
    }

    public string Processes
    {
        get => string.Join(", ", Model.Processes);
        set { Model.Processes = SplitCommas(value); Changed(); }
    }

    public string Protocols
    {
        get => string.Join(", ", Model.Protocols);
        set { Model.Protocols = SplitCommas(value); Changed(); }
    }

    /// <summary>One-line summary shown in the rule list.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Model.Domains.Count > 0) parts.Add($"{Model.Domains.Count} domain(s)");
            if (Model.Ips.Count > 0) parts.Add($"{Model.Ips.Count} IP rule(s)");
            if (Model.Ports.Count > 0) parts.Add($"ports {string.Join(",", Model.Ports)}");
            if (Model.Processes.Count > 0) parts.Add($"{Model.Processes.Count} process(es)");
            if (Model.Protocols.Count > 0) parts.Add(string.Join(",", Model.Protocols));
            return parts.Count == 0 ? "matches nothing yet" : string.Join(" · ", parts);
        }
    }

    private void Changed()
    {
        persist();
        OnPropertyChanged(string.Empty);
    }

    private static List<string> SplitLines(string value) =>
        [.. value.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> SplitCommas(string value) =>
        [.. value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
