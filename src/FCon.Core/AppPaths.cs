namespace FCon.Core;

/// <summary>
/// Every path the app writes to. Config lives under %APPDATA% so the install directory
/// can stay read-only; engine binaries live beside the executable.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// Marker file that switches the app to portable mode. Shipped in the portable zip;
    /// absent from the installed build.
    /// </summary>
    public const string PortableMarkerFile = "portable.txt";

    /// <summary>
    /// True when a portable marker sits beside the executable. Portable installs keep
    /// their settings next to the binary so the whole thing travels on a USB stick and
    /// leaves nothing behind in the user profile.
    /// </summary>
    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFile));

    public static string DataDirectory { get; } = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KVN");

    /// <summary>Where the app kept its data before it was renamed from FCon.</summary>
    public static string LegacyDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCon");

    /// <summary>
    /// Carry an existing FCon profile over to the KVN folder, once. Only when the new
    /// folder does not exist yet, so a user who has already run KVN is never overwritten.
    /// Returns true when something was moved.
    /// </summary>
    public static bool MigrateLegacyData()
    {
        if (IsPortable) return false;
        if (Directory.Exists(DataDirectory) || !Directory.Exists(LegacyDataDirectory)) return false;

        try
        {
            Directory.Move(LegacyDataDirectory, DataDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file in the old folder; start fresh rather than fail to launch.
            return false;
        }
    }

    public static string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string SubscriptionsFile => Path.Combine(DataDirectory, "subscriptions.json");
    public static string RoutingFile => Path.Combine(DataDirectory, "routing.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");
    public static string AssetsDirectory => Path.Combine(DataDirectory, "assets");

    /// <summary>Side-loaded protocol plugins, one subdirectory per plugin package.</summary>
    public static string PluginsDirectory => Path.Combine(BaseDirectory, "plugins");

    /// <summary>Engine binaries shipped with, or downloaded next to, the app.</summary>
    public static string EnginesDirectory => Path.Combine(BaseDirectory, "engines");

    public static string GeneratedConfigFile(string engine) =>
        Path.Combine(RuntimeDirectory, $"{engine}.generated.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(AssetsDirectory);

        // Create the engine folders up front so "put the binary here" points somewhere
        // that already exists. A read-only install directory is not an error.
        TryCreate(Path.Combine(EnginesDirectory, "sing-box"));
        TryCreate(Path.Combine(EnginesDirectory, "xray"));
    }

    private static void TryCreate(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Portable installs may sit somewhere unwritable; engine lookup falls back to PATH.
        }
    }
}
