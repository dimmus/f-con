using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FCon.App.Services;

/// <summary>
/// Swaps the palette dictionary and asks the window manager for a matching title bar,
/// so the chrome does not stay light while the app is dark.
/// </summary>
public static partial class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropMica = 2;

    public static bool IsDark { get; private set; } = true;

    /// <summary>Apply "dark", "light" or "system".</summary>
    public static void Apply(string theme)
    {
        IsDark = theme.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark(),
        };

        var palette = new ResourceDictionary
        {
            Source = new Uri(
                IsDark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml",
                UriKind.Relative),
        };

        // Slot 0 by contract with App.xaml; replacing it re-evaluates every DynamicResource.
        Application.Current.Resources.MergedDictionaries[0] = palette;

        foreach (Window window in Application.Current.Windows)
            ApplyToWindow(window);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Give a window a dark title bar and, on Windows 11, a Mica backdrop.</summary>
    public static void ApplyToWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // Mica exists from Windows 11 22H2; the call is simply ignored on older builds.
        var backdrop = BackdropMica;
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
