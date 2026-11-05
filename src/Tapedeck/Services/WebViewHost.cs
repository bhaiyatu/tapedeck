using Microsoft.Web.WebView2.Core;

namespace Tapedeck.Services;

/// <summary>
/// Owns the single WebView2 environment shared by the hidden player and the sign-in window,
/// so both see the same Google session cookies.
/// </summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _environment;

    public static string UserDataFolder { get; } = Path.Combine(SettingsService.DataFolder, "WebView2");

    public static Task<CoreWebView2Environment> GetEnvironmentAsync() => _environment ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(UserDataFolder);
        var options = new CoreWebView2EnvironmentOptions
        {
            // Playback starts from code, not a click inside the page.
            // Media keys and the Windows media flyout are handled natively (MediaControlsService),
            // so stop Chromium from registering its own session.
            AdditionalBrowserArguments =
                "--autoplay-policy=no-user-gesture-required " +
                "--disable-features=HardwareMediaKeyHandling,MediaSessionService,GlobalMediaControls",
        };
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, UserDataFolder, options);
    }
}
