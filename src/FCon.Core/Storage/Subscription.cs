namespace FCon.Core.Storage;

public sealed record Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shipped with the app. Cannot be removed or re-pointed, only deactivated or
    /// renamed, so every install keeps a known-good source of servers.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Fetch through the running tunnel rather than direct. Needed when the URL is itself blocked.</summary>
    public bool UpdateThroughProxy { get; set; }

    /// <summary>Override the User-Agent; some providers key their response format off it.</summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset? LastUpdated { get; set; }
    public string? LastError { get; set; }
    public int NodeCount { get; set; }

    /// <summary>Traffic counters parsed from the provider's Subscription-Userinfo header.</summary>
    public long? UsedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public double? UsedFraction =>
        TotalBytes is > 0 && UsedBytes is not null
            ? Math.Clamp((double)UsedBytes.Value / TotalBytes.Value, 0, 1)
            : null;
}

public sealed record ProfileState
{
    public List<Abstractions.Model.ProxyNode> Nodes { get; set; } = [];
    public List<Subscription> Subscriptions { get; set; } = [];
}
