namespace FCon.Core.Subscriptions;

/// <summary>
/// Subscriptions every install carries. They are added on first run, adopted if the
/// user had already added the same URL by hand, and cannot be removed - only
/// deactivated - so a fresh install always has servers to try.
/// </summary>
public static class DefaultSubscriptions
{
    public sealed record Entry(string Name, string Url);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("Russia · VLESS (igareck)",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_VLESS_RUS.txt"),
        new("Russia · Shadowsocks + others (igareck)",
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_SS%2BAll_RUS.txt"),
    ];

    /// <summary>
    /// URL equality that survives the ways a user might have typed the same address:
    /// case, and percent-encoding of characters such as "+".
    /// </summary>
    public static bool SameUrl(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string url) => Uri.UnescapeDataString(url.Trim()).TrimEnd('/');
}
