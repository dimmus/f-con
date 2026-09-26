using System.Text.RegularExpressions;

namespace FCon.Abstractions.Util;

/// <summary>
/// Pulls a comparable version out of what a core prints for <c>version</c>.
/// sing-box says "sing-box version 1.12.4"; Xray says "Xray 25.1.30 (Xray, Penetrates
/// Everything.)". Pre-release suffixes ("1.15.0-alpha.2") are dropped for comparison.
/// </summary>
public static partial class EngineVersionKit
{
    public static Version? Parse(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return null;

        var match = VersionToken().Match(banner);
        if (!match.Success) return null;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new Version(major, minor, patch);
    }

    /// <summary>True when the banner names a pre-release build.</summary>
    public static bool IsPreRelease(string? banner) =>
        !string.IsNullOrWhiteSpace(banner) && PreReleaseMarker().IsMatch(banner);

    public static bool AtLeast(Version? actual, int major, int minor, int patch = 0) =>
        actual is null || actual >= new Version(major, minor, patch);

    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?")]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?-(alpha|beta|rc|dev|pre)", RegexOptions.IgnoreCase)]
    private static partial Regex PreReleaseMarker();
}
