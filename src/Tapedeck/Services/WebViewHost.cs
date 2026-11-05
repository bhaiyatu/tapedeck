using Microsoft.Web.WebView2.Core;

namespace Tapedeck.Services;

/// <summary>
/// Owns the single WebView2 environment shared by the hidden player and the sign-in window
/// (so both see the same Google session), and applies Tapedeck's security policy to every WebView.
/// </summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>Profile used when the user chose not to stay signed in: kept in memory only.</summary>
    public const string PrivateProfileName = "TapedeckPrivate";

    public static string UserDataFolder { get; } = Path.Combine(SettingsService.DataFolder, "WebView2");

    /// <summary>
    /// WebView2 honours these environment variables over anything the app asks for. Another program
    /// could use them to open a remote-debugging port (and read every cookie) or to swap in a different
    /// browser runtime or profile, so they are cleared for this process before any WebView starts.
    /// </summary>
    public static void ScrubEnvironment()
    {
        foreach (var name in new[]
                 {
                     "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                     "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
                     "WEBVIEW2_USER_DATA_FOLDER",
                     "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
                     "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER",
                 })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

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

    /// <summary>
    /// Controller options for every Tapedeck WebView. With "stay signed in" off, both WebViews share an
    /// InPrivate profile, so the Google session lives only in memory and is gone when the app closes.
    /// </summary>
    public static CoreWebView2ControllerOptions? ControllerOptions(CoreWebView2Environment env)
    {
        if (App.Settings.Current.StaySignedIn) return null;
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = PrivateProfileName;
        options.IsInPrivateModeEnabled = true;
        return options;
    }

    /// <summary>Hosts a Tapedeck WebView may show at the top level: Google sign-in and YouTube.</summary>
    public static bool IsTrustedHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "accounts.youtube.com" or "myaccount.google.com"
                   or "consent.google.com" or "consent.youtube.com" or "www.google.com" or "support.google.com"
                   or "music.youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtube.com"
               // accounts.google.com and Google's own country sign-in domains (accounts.google.co.uk, …)
               || GoogleDomains.IsAccountsHost(host);
    }

    /// <summary>The narrower set the hidden player is allowed to be on.</summary>
    public static bool IsPlayerHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "music.youtube.com" or "www.youtube.com" or "youtube.com"
                   or "consent.youtube.com" or "consent.google.com" or "accounts.youtube.com"
               // Sign-out passes through Google's country domains too.
               || GoogleDomains.IsAccountsHost(host);
    }

    /// <summary>Security policy shared by the player and the sign-in window.</summary>
    public static void Harden(CoreWebView2 core, bool allowWebMessages)
    {
        var s = core.Settings;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        // DevTools on a signed-in page exposes every session cookie; never in release builds.
        s.AreDevToolsEnabled = false;
#endif
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = allowWebMessages;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;

        // Nothing in Tapedeck needs the camera, mic, location, notifications, clipboard, …
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        // …or to download files, or to hand links to other apps (mailto:, ms-settings:, …).
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        // Basic-auth prompts would appear as unexplained dialogs; refuse them.
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
    }

    /// <summary>Wipes every cookie and all site data (storage, cache, service workers) from a profile.</summary>
    public static async Task ClearProfileAsync(CoreWebView2 core)
    {
        core.CookieManager.DeleteAllCookies();
        await core.Profile.ClearBrowsingDataAsync();
    }
}
