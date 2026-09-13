using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FCon.Core.Net;

/// <summary>
/// Drives the per-user WinINet proxy settings that Windows, Edge, Chrome and most
/// desktop apps read. The previous configuration is captured on the first enable and
/// restored on disable, including after a crash, via a saved snapshot on disk.
/// </summary>
public static partial class SystemProxy
{
    private const string RegistryKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>Hosts that must never traverse the tunnel, or the app deadlocks against itself.</summary>
    private const string DefaultBypass =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;"
        + "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;"
        + "192.168.*;<local>";

    private static string SnapshotFile => Path.Combine(AppPaths.DataDirectory, "proxy-snapshot.json");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue("ProxyEnable") is int value && value != 0;
        }
    }

    public static string? CurrentServer
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue("ProxyServer") as string;
        }
    }

    /// <summary>Point the system at our HTTP listener. Captures the prior settings once.</summary>
    public static void Enable(int httpPort, string? extraBypass = null)
    {
        CaptureSnapshot(httpPort);

        var bypass = string.IsNullOrWhiteSpace(extraBypass)
            ? DefaultBypass
            : $"{extraBypass.TrimEnd(';')};{DefaultBypass}";

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open the Internet Settings key.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{httpPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        // A stale auto-config URL would silently win over the manual setting.
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

        Notify();
    }

    /// <summary>Restore whatever was configured before we touched anything.</summary>
    public static void Disable()
    {
        var snapshot = ReadSnapshot();

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
        if (key is null) return;

        if (snapshot is null)
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }
        else
        {
            // Do not hand the machine back to a proxy that is no longer listening. A
            // snapshot can outlive the program it described, and a dead proxy setting
            // looks to the user like the internet is broken.
            var enable = snapshot.Enabled && !IsDeadLoopbackProxy(snapshot.Server);

            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);

            if (snapshot.Server is null) key.DeleteValue("ProxyServer", false);
            else key.SetValue("ProxyServer", snapshot.Server, RegistryValueKind.String);

            if (snapshot.Override is null) key.DeleteValue("ProxyOverride", false);
            else key.SetValue("ProxyOverride", snapshot.Override, RegistryValueKind.String);

            if (snapshot.AutoConfigUrl is not null)
                key.SetValue("AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        }

        Notify();
        DeleteSnapshot();
    }

    /// <summary>
    /// Called at startup: if a snapshot survives from a previous run, the app exited without
    /// cleaning up and the machine is still pointed at a dead listener.
    /// </summary>
    public static bool HasStaleSnapshot() => File.Exists(SnapshotFile);

    private static void CaptureSnapshot(int ourPort)
    {
        if (File.Exists(SnapshotFile)) return; // Never overwrite the pristine state with our own.

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        var server = key?.GetValue("ProxyServer") as string;
        var enabled = key?.GetValue("ProxyEnable") is int e && e != 0;

        // A previous run that was killed rather than closed leaves the machine pointed at
        // our own listener. Recording that as "the user's setting" would make a later
        // restore aim at a dead port, so treat it as no proxy at all.
        if (PointsAtUs(server, ourPort))
        {
            enabled = false;
            server = null;
        }

        var snapshot = new ProxySnapshot(
            enabled,
            server,
            key?.GetValue("ProxyOverride") as string,
            key?.GetValue("AutoConfigURL") as string);

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(SnapshotFile, System.Text.Json.JsonSerializer.Serialize(snapshot));
        }
        catch (IOException)
        {
            // Losing the snapshot only costs us the restore; enabling still has to proceed.
        }
    }

    /// <summary>
    /// True when a ProxyServer value is the listener we are about to start. Deliberately
    /// matched on our exact port and nothing wider: other proxy clients legitimately sit
    /// on loopback too, and their setting is the user's to get back.
    /// </summary>
    private static bool PointsAtUs(string? server, int ourPort)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;

        var value = server.Trim();
        return value.Contains($"127.0.0.1:{ourPort}", StringComparison.Ordinal)
               || value.Contains($"localhost:{ourPort}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a loopback proxy setting with nothing listening behind it.</summary>
    private static bool IsDeadLoopbackProxy(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;

        var colon = server.LastIndexOf(':');
        if (colon < 0) return false;

        var host = server[..colon].Trim();
        if (!host.EndsWith("127.0.0.1", StringComparison.Ordinal)
            && !host.EndsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(server[(colon + 1)..].Trim(), out var port)
               && !PortProbe.IsListening(port);
    }

    private static ProxySnapshot? ReadSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotFile)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<ProxySnapshot>(
                File.ReadAllText(SnapshotFile));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void DeleteSnapshot()
    {
        try
        {
            if (File.Exists(SnapshotFile)) File.Delete(SnapshotFile);
        }
        catch (IOException)
        {
            // Harmless: a leftover snapshot is only read when one exists at startup.
        }
    }

    /// <summary>Registry writes alone do not take effect; WinINet has to be told.</summary>
    private static void Notify()
    {
        InternetSetOption(nint.Zero, InternetOptionSettingsChanged, nint.Zero, 0);
        InternetSetOption(nint.Zero, InternetOptionRefresh, nint.Zero, 0);
    }

    // LibraryImport uses exact spelling, so the W suffix has to be explicit: unlike
    // DllImport it never probes for the A/W variants and would fail at first call.
    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(nint hInternet, int option, nint buffer, int bufferLength);

    private sealed record ProxySnapshot(bool Enabled, string? Server, string? Override, string? AutoConfigUrl);
}
