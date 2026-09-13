using System.Text.RegularExpressions;
using FCon.Abstractions.Plugins;
using FCon.Core;
using FCon.Core.Engine;

namespace FCon.App.ViewModels;

/// <summary>
/// One row in the engine list. Carries a grade rather than a sentence so the UI can
/// show at a glance whether a core is usable, without the user parsing prose.
/// </summary>
public sealed partial class EngineRowViewModel
{
    private EngineRowViewModel(EngineKind kind, EngineInfo? info, bool isSelected)
    {
        Kind = kind;
        IsSelected = isSelected;
        IsInstalled = info is not null;
        Location = info?.ExecutablePath
                   ?? Path.Combine(AppPaths.EnginesDirectory, EngineLocator.DirectoryName(kind));

        Version = info is null ? null : ExtractVersion(info.Version);
        IsPreRelease = Version is not null && PreReleaseMarker().IsMatch(Version);
    }

    public EngineKind Kind { get; }
    public bool IsInstalled { get; }
    public bool IsSelected { get; }
    public bool IsPreRelease { get; }
    public string? Version { get; }
    public string Location { get; }

    public string DisplayName => Kind == EngineKind.SingBox ? "sing-box" : "Xray";

    /// <summary>Drives the status dot colour: green usable, amber usable-but-risky, red missing.</summary>
    public string Grade => !IsInstalled ? "bad" : IsPreRelease ? "warn" : "good";

    public string StatusLabel => !IsInstalled
        ? "Not installed"
        : IsPreRelease ? "Pre-release" : "Ready";

    /// <summary>The one line that tells the user what, if anything, to do.</summary>
    public string Detail => !IsInstalled
        ? $"Put {EngineLocator.ExecutableName(Kind)} in {Location}"
        : IsPreRelease
            ? $"{Version} is a pre-release; a stable build is recommended. {Location}"
            : $"{Version} - {Location}";

    public bool CanInstall => !IsInstalled;

    /// <summary>Parameter for the Get-engine command.</summary>
    public string InstallKey => Kind == EngineKind.Xray ? "xray" : "singbox";

    public static EngineRowViewModel Create(EngineKind kind, bool isSelected) =>
        new(kind, EngineLocator.Describe(kind), isSelected);

    /// <summary>
    /// Pull the version token out of what the core prints. sing-box says
    /// "sing-box version 1.11.0"; Xray says "Xray 25.1.30 (Xray, Penetrates ...)".
    /// </summary>
    private static string? ExtractVersion(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return null;
        var match = VersionToken().Match(banner);
        return match.Success ? match.Value : banner.Trim();
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?(-[0-9A-Za-z.]+)?")]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"(alpha|beta|rc|dev|pre)", RegexOptions.IgnoreCase)]
    private static partial Regex PreReleaseMarker();
}
