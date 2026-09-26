using CommunityToolkit.Mvvm.ComponentModel;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;
using FCon.Core;
using FCon.Core.Engine;

namespace FCon.App.ViewModels;

/// <summary>
/// One row in the engine list. Carries a grade rather than a sentence so the UI can
/// show at a glance whether a core is usable, without the user parsing prose.
/// </summary>
public sealed partial class EngineRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _progress;

    private EngineRowViewModel(EngineKind kind, EngineInfo? info, bool isSelected)
    {
        Kind = kind;
        IsSelected = isSelected;
        IsInstalled = info is not null;
        Location = info?.ExecutablePath
                   ?? Path.Combine(AppPaths.EnginesDirectory, EngineLocator.DirectoryName(kind));

        Version = info is null ? null : EngineVersionKit.Parse(info.Version)?.ToString(3) ?? info.Version;
        IsPreRelease = EngineVersionKit.IsPreRelease(info?.Version);
        IsTooOld = kind == EngineKind.SingBox
                   && EngineVersionKit.Parse(info?.Version) is { } v
                   && v < SingBoxConfigBuilder.MinimumVersion;
    }

    public EngineKind Kind { get; }
    public bool IsInstalled { get; }
    public bool IsSelected { get; }
    public bool IsPreRelease { get; }
    public bool IsTooOld { get; }
    public string? Version { get; }
    public string Location { get; }

    public string DisplayName => Kind == EngineKind.SingBox ? "sing-box" : "Xray";

    /// <summary>Drives the status dot colour: green usable, amber usable-but-risky, red missing.</summary>
    public string Grade => !IsInstalled || IsTooOld ? "bad" : IsPreRelease ? "warn" : "good";

    public string StatusLabel => !IsInstalled
        ? "Not installed"
        : IsTooOld ? "Too old"
        : IsPreRelease ? "Pre-release" : "Ready";

    /// <summary>The one line that tells the user what, if anything, to do.</summary>
    public string Detail => !IsInstalled
        ? $"Download the latest stable release, or put {EngineLocator.ExecutableName(Kind)} in {Location}"
        : IsTooOld
            ? $"{Version} is older than {SingBoxConfigBuilder.MinimumVersion}; update it. {Location}"
            : IsPreRelease
                ? $"{Version} is a pre-release; a stable build is recommended. {Location}"
                : $"{Version} - {Location}";

    /// <summary>Label for the download button: a fresh install or a replacement.</summary>
    public string InstallLabel => IsInstalled ? "Update" : "Download";

    /// <summary>Parameter for the manual Get-engine command.</summary>
    public string InstallKey => Kind == EngineKind.Xray ? "xray" : "singbox";

    public static EngineRowViewModel Create(EngineKind kind, bool isSelected) =>
        new(kind, EngineLocator.Describe(kind), isSelected);
}
