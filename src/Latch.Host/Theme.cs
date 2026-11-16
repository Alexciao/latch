using Latch.Core.Contract;
using Microsoft.Win32;

namespace Latch.Host;

/// <summary>System theme and window chrome (dark title bar, Mica on Windows 11, rounded corners).</summary>
internal static class Theme
{
    /// <summary>Mica needs DWMWA_SYSTEMBACKDROP_TYPE, available from Windows 11 22H2 (build 22621).</summary>
    public static bool MicaSupported => Environment.OSVersion.Version.Build >= 22621;

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsDark(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,
        _ => SystemIsDark(),
    };

    public static void ApplyChrome(nint hwnd, bool dark, bool mica, bool round)
    {
        var d = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
        if (mica && MicaSupported)
        {
            var backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        if (round)
        {
            var corner = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
    }

    /// <summary>Lets the tray menu follow dark mode (undocumented uxtheme ordinals; harmless if missing).</summary>
    public static void AllowDarkMenus()
    {
        try
        {
            Native.SetPreferredAppMode(1); // AllowDark
            Native.FlushMenuThemes();
        }
        catch (Exception)
        {
        }
    }
}
