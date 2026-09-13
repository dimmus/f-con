using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.App.Services;
using FCon.Core.Storage;

namespace FCon.App.ViewModels;

public sealed partial class SubscriptionsViewModel(
    AppServices services,
    IDialogService dialogs,
    Action refreshServers) : ObservableObject
{
    [ObservableProperty] private ObservableCollection<SubscriptionRowViewModel> _items = [];
    [ObservableProperty] private SubscriptionRowViewModel? _selected;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string? _progressText;

    public void Refresh()
    {
        var selectedId = Selected?.Model.Id;
        Items = new ObservableCollection<SubscriptionRowViewModel>(
            services.Profiles.Subscriptions.Select(s => new SubscriptionRowViewModel(s, SetEnabled)));
        Selected = Items.FirstOrDefault(i => i.Model.Id == selectedId) ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    private void Add()
    {
        var url = dialogs.PromptText(
            "Add subscription",
            "Paste the subscription URL. It will be fetched and its servers imported.");

        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            dialogs.ShowError("Invalid URL", "A subscription URL must start with http:// or https://.");
            return;
        }

        var normalised = uri.ToString();

        // Adding the same feed twice imports every server twice. The rows look identical,
        // both refresh on the same schedule, and the duplicate servers are indistinguishable
        // in the list - so select the existing one and refresh it instead of adding a second.
        var existing = services.Profiles.Subscriptions
            .FirstOrDefault(s => string.Equals(s.Url, normalised, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Refresh();
            Selected = Items.FirstOrDefault(i => i.Model.Id == existing.Id);
            _ = UpdateOneAsync(Selected);
            return;
        }

        var subscription = new Subscription
        {
            Url = normalised,
            Name = uri.Host,
        };

        services.Profiles.UpsertSubscription(subscription);
        Refresh();
        _ = UpdateOneAsync(Items.FirstOrDefault(i => i.Model.Id == subscription.Id));
    }

    [RelayCommand]
    private void Rename(SubscriptionRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        var name = dialogs.PromptText("Rename subscription", "Display name", row.Model.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        row.Model.Name = name.Trim();
        services.Profiles.UpsertSubscription(row.Model);
        Refresh();
        refreshServers();
    }

    [RelayCommand]
    private void EditUrl(SubscriptionRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        var url = dialogs.PromptText("Subscription URL", "Update the fetch URL", row.Model.Url);
        if (string.IsNullOrWhiteSpace(url)) return;

        row.Model.Url = url.Trim();
        services.Profiles.UpsertSubscription(row.Model);
        Refresh();
    }

    [RelayCommand]
    private void Remove(SubscriptionRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        if (!dialogs.Confirm("Remove subscription",
                $"Remove \"{row.Model.Name}\" and the {row.Model.NodeCount} server(s) it provided?"))
            return;

        services.Profiles.RemoveSubscription(row.Model.Id, removeNodes: true);
        Refresh();
        refreshServers();
    }

    /// <summary>
    /// Turn a whole server list on or off.
    /// </summary>
    /// <remarks>
    /// A deactivated subscription keeps its servers and their measured history, but they
    /// leave the server list and stop being candidates for best-server selection and
    /// failover. A running connection is deliberately left alone: pulling a working
    /// tunnel out from under someone is a worse surprise than one server outliving the
    /// list it came from, and the next reconnect will not choose it again.
    /// </remarks>
    private void SetEnabled(SubscriptionRowViewModel row, bool enabled)
    {
        var affected = services.Profiles.SetSubscriptionEnabled(row.Model.Id, enabled);

        var note = enabled
            ? $"Activated \"{row.Model.Name}\": {affected} server(s) back in the list."
            : $"Deactivated \"{row.Model.Name}\": {affected} server(s) excluded from selection.";

        if (!enabled && services.Engine.ActiveNode is { } active && active.SubscriptionId == row.Model.Id)
            note += $" The active connection through {active.DisplayName} is left running.";

        services.Log.Add(new FCon.Core.Engine.EngineLogLine(DateTimeOffset.Now, note, false));

        row.Reload();
        refreshServers();
    }

    [RelayCommand]
    private void ToggleEnabled(SubscriptionRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        row.IsActive = !row.IsActive;
    }

    [RelayCommand]
    private void ToggleProxyFetch(SubscriptionRowViewModel? row)
    {
        row ??= Selected;
        if (row is null) return;

        row.Model.UpdateThroughProxy = !row.Model.UpdateThroughProxy;
        services.Profiles.UpsertSubscription(row.Model);
        row.Reload();
    }

    [RelayCommand]
    private Task UpdateSelectedAsync() => UpdateOneAsync(Selected);

    private async Task UpdateOneAsync(SubscriptionRowViewModel? row)
    {
        if (row is null || IsUpdating) return;

        IsUpdating = true;
        ProgressText = $"Updating {row.Model.Name}...";
        try
        {
            var result = await services.Subscriptions.UpdateAsync(row.Model);
            Refresh();
            refreshServers();

            if (!result.Succeeded)
                dialogs.ShowError("Subscription update failed", result.FatalError!);
        }
        finally
        {
            IsUpdating = false;
            ProgressText = null;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (IsUpdating) return;

        IsUpdating = true;
        try
        {
            var progress = new Progress<Subscription>(s => ProgressText = $"Updating {s.Name}...");
            var results = await services.Subscriptions.UpdateAllAsync(progress);

            Refresh();
            refreshServers();

            var failed = results.Where(r => !r.Succeeded).ToList();
            if (failed.Count > 0)
            {
                dialogs.ShowError("Some subscriptions failed", string.Join(
                    Environment.NewLine,
                    failed.Select(f => $"{f.Subscription.Name}: {f.FatalError}")));
            }
        }
        finally
        {
            IsUpdating = false;
            ProgressText = null;
        }
    }
}

public sealed partial class SubscriptionRowViewModel(
    Subscription model,
    Action<SubscriptionRowViewModel, bool>? setEnabled = null) : ObservableObject
{
    public Subscription Model { get; } = model;

    /// <summary>
    /// Whether this list's servers are in play. Named IsActive rather than IsEnabled so
    /// binding it never collides with the control's own IsEnabled.
    /// </summary>
    public bool IsActive
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            setEnabled?.Invoke(this, value);
            OnPropertyChanged();
        }
    }

    public string Name => Model.Name;
    public string Url => Model.Url;
    public int NodeCount => Model.NodeCount;
    public bool ThroughProxy => Model.UpdateThroughProxy;

    public string LastUpdatedText => Model.LastUpdated is { } t
        ? t.LocalDateTime.ToString("g")
        : "never";

    public string StatusText => Model.LastError ?? (Model.Enabled ? "OK" : "Deactivated");
    public bool HasError => Model.LastError is not null;

    /// <summary>"12.4 GB of 100 GB" when the provider reports quota, otherwise blank.</summary>
    public string QuotaText
    {
        get
        {
            if (Model.TotalBytes is not > 0 || Model.UsedBytes is null) return "";
            return $"{Bytes(Model.UsedBytes.Value)} of {Bytes(Model.TotalBytes.Value)}";
        }
    }

    public double QuotaFraction => Model.UsedFraction ?? 0;
    public bool HasQuota => Model.UsedFraction is not null;

    public string ExpiryText => Model.ExpiresAt is { } e
        ? $"expires {e.LocalDateTime:d}"
        : "";

    public void Reload()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(QuotaFraction));
        OnPropertyChanged(nameof(HasQuota));
        OnPropertyChanged(nameof(ExpiryText));
        OnPropertyChanged(nameof(ThroughProxy));
        OnPropertyChanged(nameof(IsActive));
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
