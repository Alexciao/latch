using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Latch.Core;
using Latch.Core.Contract;
using Latch.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Latch.Host;

/// <summary>
/// The shared WebView2 environment and the pieces every Latch web view needs: private virtual hosts, a strict
/// navigation allowlist, the RPC bridge and transparent, Mica-backed windows.
/// </summary>
internal static class WebUi
{
    public const string AppHost = "app.latch.example";
    public const string IconHost = "icons.latch.example";

    private static Task<CoreWebView2Environment>? _env;

    /// <summary>Set LATCH_DEV_URL (e.g. http://localhost:5173) to load the Vite dev server instead of wwwroot.</summary>
    public static string? DevUrl { get; } = Environment.GetEnvironmentVariable("LATCH_DEV_URL")?.TrimEnd('/');

    public static Task<CoreWebView2Environment> Environment_(DataPaths paths) =>
        _env ??= CoreWebView2Environment.CreateAsync(null, paths.WebView, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection,CalculateNativeWinOcclusion"));

    public static string PageUrl(string page, bool mica = true) =>
        (DevUrl ?? $"https://{AppHost}") + $"/{page}?mica={(mica && Theme.MicaSupported ? 1 : 0)}";

    /// <summary>
    /// The prompt's window: a plain popup with a solid surface (the colour of the Windows Hello dialog), rounded
    /// corners and the light popup shadow, like Hello's own window. An extended DWM frame would allow Mica but brings
    /// the heavy full-window shadow, which looked wrong next to Hello.
    /// </summary>
    public static Window MakePopup() => new()
    {
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowActivated = true,
    };

    /// <summary>The popup surface colour: the same as the UI's popover token (and close to Hello's dialog).</summary>
    public static Color PopupColor(bool dark) => dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xFC, 0xFC, 0xFC);

    public static nint Prepare(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        return hwnd;
    }

    public static async Task<CoreWebView2Controller> CreateAsync(DataPaths paths, nint hwnd, Rpc rpc, ThemeChoice theme)
    {
        var env = await Environment_(paths);
        var controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        var web = controller.CoreWebView2;
        var s = web.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = DevUrl is not null;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = DevUrl is not null;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        SetTheme(web, theme);

        web.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        Directory.CreateDirectory(paths.Icons);
        web.SetVirtualHostNameToFolderMapping(IconHost, paths.Icons, CoreWebView2HostResourceAccessKind.Allow);

        web.NavigationStarting += (_, e) =>
        {
            if (!Allowed(e.Uri)) e.Cancel = true;
        };
        web.NewWindowRequested += (_, e) => e.Handled = true;
        web.WebMessageReceived += async (_, e) =>
        {
            if (!Allowed(e.Source)) return;
            string request;
            try
            {
                request = e.WebMessageAsJson;
            }
            catch (ArgumentException)
            {
                return;
            }
            var response = await rpc.HandleAsync(request);
            try
            {
                web.PostWebMessageAsJson(response);
            }
            catch (Exception)
            {
                // The view closed while the call ran.
            }
        };
        return controller;
    }

    public static void SetTheme(CoreWebView2 web, ThemeChoice theme) =>
        web.Profile.PreferredColorScheme = theme switch
        {
            ThemeChoice.Light => CoreWebView2PreferredColorScheme.Light,
            ThemeChoice.Dark => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };

    public static void FitToClient(CoreWebView2Controller controller, nint hwnd)
    {
        Native.GetClientRect(hwnd, out var rc);
        controller.Bounds = new System.Drawing.Rectangle(0, 0, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    private static bool Allowed(string uri) =>
        uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase)
        || (DevUrl is not null && uri.StartsWith(DevUrl + "/", StringComparison.OrdinalIgnoreCase));
}
