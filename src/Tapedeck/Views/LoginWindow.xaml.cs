using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Tapedeck.Services;

namespace Tapedeck.Views;

/// <summary>
/// Hosts Google's own sign-in page in a WebView2 that shares the player's profile, so the session
/// cookies it sets are the ones the player and API client use. Tapedeck injects nothing into this page,
/// never reads what is typed, and always shows which site the page is on.
/// </summary>
public sealed partial class LoginWindow : Window
{
    public const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly string _startUrl;
    private readonly bool _isInterstitial;
    // Non-Google sign-in pages (work/school SSO) the user explicitly allowed in this window.
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _pendingUri;
    private CoreWebView2? _core;
    private bool _completing;

    public LoginWindow(string? startUrl = null)
    {
        InitializeComponent();
        _isInterstitial = startUrl is not null;
        // The interstitial path only ever opens Google / YouTube consent or sign-in pages.
        _startUrl = startUrl is not null && Uri.TryCreate(startUrl, UriKind.Absolute, out var u) && WebViewHost.IsPlayerHost(u)
            ? startUrl
            : SignInUrl;
        if (_isInterstitial)
        {
            Title = "YouTube Music";
            Banner.Title = "YouTube needs a choice from you";
            Banner.Message = "Finish this page and Tapedeck will carry on playing.";
        }

        Root.RequestedTheme = App.Settings.Current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 780));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tapedeck.ico"));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMinimizable = false;
            p.IsMaximizable = false;
        }
        Closed += (_, _) => _result.TrySetResult(false);
        _ = InitializeAsync();
    }

    /// <summary>Completes with true once a session exists, false if the window was closed.</summary>
    public Task<bool> Result => _result.Task;

    private async Task InitializeAsync()
    {
        var env = await WebViewHost.GetEnvironmentAsync();
        var options = WebViewHost.ControllerOptions(env);
        if (options is null) await Browser.EnsureCoreWebView2Async(env);
        else await Browser.EnsureCoreWebView2Async(env, options);
        var core = _core = Browser.CoreWebView2;

        // No scripts, no messages, no host objects, no password/autofill storage, no DevTools.
        WebViewHost.Harden(core, allowWebMessages: false);

        core.NavigationStarting += OnNavigationStarting;
        core.SourceChanged += (_, _) => ShowAddress();
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)) return;
            if (WebViewHost.IsTrustedHost(target)) core.Navigate(e.Uri);
            // Help / privacy links that leave Google open in the user's normal browser instead.
            else if (target.Scheme == Uri.UriSchemeHttps) await Windows.System.Launcher.LaunchUriAsync(target);
        };
        core.Navigate(_startUrl);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            e.Cancel = true;
            return;
        }
        if (WebViewHost.IsTrustedHost(target) || (target.Scheme == Uri.UriSchemeHttps && _allowedHosts.Contains(target.Host)))
        {
            BlockedBar.IsOpen = false;
            LoadingRing.IsActive = true;
            return;
        }

        // Anything else stops here, in plain view, before the page can load.
        e.Cancel = true;
        LoadingRing.IsActive = false;
        Banner.IsOpen = false;
        BlockedBar.IsOpen = true;
        if (target.Scheme != Uri.UriSchemeHttps)
        {
            _pendingUri = null;
            BlockedBar.Severity = InfoBarSeverity.Error;
            BlockedBar.Title = "Blocked an insecure page";
            BlockedBar.Message = $"{target.Host} isn't using a secure connection, so Tapedeck won't open it here.";
            AllowButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _pendingUri = target;
            BlockedBar.Severity = InfoBarSeverity.Warning;
            BlockedBar.Title = "This page isn't Google";
            BlockedBar.Message = $"Sign-in wants to continue on {target.Host}. Only continue if that is your school's or company's own sign-in page.";
            AllowButton.Visibility = Visibility.Visible;
        }
    }

    private void OnAllowSite(object sender, RoutedEventArgs e)
    {
        if (_pendingUri is not { } uri || _core is null) return;
        _allowedHosts.Add(uri.Host);
        BlockedBar.IsOpen = false;
        _core.Navigate(uri.ToString());
    }

    private void ShowAddress()
    {
        if (_core is null || !Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri)) return;
        var google = WebViewHost.IsTrustedHost(uri);
        HostText.Text = uri.Host;
        TrustText.Text = google ? "· Google" : uri.Scheme == Uri.UriSchemeHttps ? "· not Google" : "· not secure";
        var color = google ? Windows.UI.Color.FromArgb(255, 34, 197, 94) : Windows.UI.Color.FromArgb(255, 245, 158, 11);
        LockIcon.Glyph = uri.Scheme == Uri.UriSchemeHttps ? "" : "";
        LockIcon.Foreground = new SolidColorBrush(color);
        AddressPill.BorderBrush = google ? (Brush)Tapedeck.ResourceExtensions.ThemeLookup(Root.Resources, "SurfaceStroke") : new SolidColorBrush(color);
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        ShowAddress();
        if (_completing || !Uri.TryCreate(sender.Source, UriKind.Absolute, out var uri)) return;
        if (uri.Host != "music.youtube.com" || uri.Scheme != Uri.UriSchemeHttps) return;

        _completing = true;
        if (_isInterstitial)
        {
            App.Bridge.Reload();
            _result.TrySetResult(true);
            Close();
            return;
        }

        if (await App.Auth.CompleteSignInAsync())
        {
            _result.TrySetResult(true);
            Close();
        }
        else
        {
            // Landed on YouTube Music without a session (e.g. the user backed out); stay put.
            _completing = false;
        }
    }
}
