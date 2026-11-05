using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Tapedeck.Services;

namespace Tapedeck.Views;

/// <summary>
/// Hosts Google's sign-in page in a WebView2 that shares the player's profile, so the session
/// cookies it sets are the ones the player and API client use.
/// </summary>
public sealed partial class LoginWindow : Window
{
    public const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly string _startUrl;
    private readonly bool _isInterstitial;
    private bool _completing;

    public LoginWindow(string? startUrl = null)
    {
        InitializeComponent();
        _startUrl = startUrl ?? SignInUrl;
        _isInterstitial = startUrl is not null;
        if (_isInterstitial)
        {
            Title = "YouTube Music";
            Banner.Title = "YouTube needs a choice from you";
            Banner.Message = "Finish this page and Tapedeck will carry on playing.";
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 720));
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
        await Browser.EnsureCoreWebView2Async(env);
        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += (_, e) =>
        {
            // Keep Google's "help" and "create account" links inside this window.
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.Navigate(_startUrl);
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        if (_completing || !Uri.TryCreate(sender.Source, UriKind.Absolute, out var uri)) return;
        if (uri.Host != "music.youtube.com") return;

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
