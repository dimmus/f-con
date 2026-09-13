namespace FCon.Core.Config;

public enum RuleAction
{
    Proxy,
    Direct,
    Block,
}

/// <summary>
/// One routing rule. Fields are OR-ed within a list and AND-ed across lists, matching
/// how both engines evaluate rules, so a rule reads the same whichever core runs it.
/// </summary>
public sealed record RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public RuleAction Action { get; set; } = RuleAction.Direct;

    /// <summary>Domains: plain suffix, <c>keyword:</c>, <c>regexp:</c>, <c>full:</c> or <c>geosite:</c>.</summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>CIDRs, bare IPs, or <c>geoip:</c> entries.</summary>
    public List<string> Ips { get; set; } = [];

    /// <summary>Port or range list, e.g. "443", "1000-2000".</summary>
    public List<string> Ports { get; set; } = [];

    /// <summary>Windows process names, e.g. "chrome.exe".</summary>
    public List<string> Processes { get; set; } = [];

    /// <summary>Sniffed protocols: http, tls, quic, bittorrent, dns.</summary>
    public List<string> Protocols { get; set; } = [];

    public string? Network { get; set; }

    public bool IsEmpty =>
        Domains.Count == 0 && Ips.Count == 0 && Ports.Count == 0
        && Processes.Count == 0 && Protocols.Count == 0 && Network is null;
}

/// <summary>The user's ordered rule set, plus the presets we ship enabled by default.</summary>
public sealed record RoutingProfile
{
    public List<RoutingRule> Rules { get; set; } = [];

    /// <summary>Keep LAN and loopback off the tunnel. Almost always wanted.</summary>
    public bool BypassPrivateNetworks { get; set; } = true;

    /// <summary>Send Windows and Microsoft telemetry endpoints direct rather than through the exit node.</summary>
    public bool BypassMicrosoftServices { get; set; }

    /// <summary>Drop QUIC so browsers fall back to TLS, which sniffs and routes reliably.</summary>
    public bool BlockQuic { get; set; }

    public bool BlockAds { get; set; }

    /// <summary>Resolve domains before matching IP rules. Costs a lookup, improves accuracy.</summary>
    public bool ResolveDomainsForIpRules { get; set; }

    /// <summary>
    /// Private networks are covered by <see cref="BypassPrivateNetworks"/> rather than by a
    /// rule, so the shipped list stays empty apart from one disabled example showing the shape.
    /// </summary>
    public static RoutingProfile CreateDefault() => new()
    {
        Rules =
        [
            new RoutingRule
            {
                Enabled = false,
                Name = "Example: keep banking direct",
                Action = RuleAction.Direct,
                Domains = ["keyword:bank", "full:example.org"],
            },
        ],
    };
}
