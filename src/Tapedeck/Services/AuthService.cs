using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Tapedeck.Models;
using Tapedeck.Services.Api;

namespace Tapedeck.Services;

/// <summary>
/// Tracks the Google session that lives in the shared WebView2 profile and exposes it to the
/// InnerTube client as a Cookie header.
/// </summary>
public sealed partial class AuthService : ObservableObject
{
    private static readonly string[] SessionCookieNames = ["SAPISID", "__Secure-3PAPISID"];

    private readonly DispatcherQueue _dispatcher;
    private readonly PlayerBridge _bridge;
    private string? _cachedHeader;
    private DateTime _cachedAt;

    public AuthService(DispatcherQueue dispatcher, PlayerBridge bridge)
    {
        _dispatcher = dispatcher;
        _bridge = bridge;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; set; }

    public bool IsSignedOut => !IsSignedIn;

    [ObservableProperty]
    public partial AccountInfo? Account { get; set; }

    /// <summary>Raised after sign-in or sign-out so pages can reload personalised content.</summary>
    public event EventHandler? SessionChanged;

    /// <summary>Checks the stored cookies at startup.</summary>
    public async Task InitializeAsync()
    {
        IsSignedIn = await HasSessionAsync();
        if (IsSignedIn) _ = RefreshAccountAsync();
    }

    private async Task<bool> HasSessionAsync()
    {
        var core = await _bridge.CoreAsync;
        var cookies = await core.CookieManager.GetCookiesAsync("https://music.youtube.com");
        return cookies.Any(c => SessionCookieNames.Contains(c.Name));
    }

    /// <summary>Cookie header for InnerTube requests. Callable from any thread.</summary>
    public Task<string?> GetCookieHeaderAsync()
    {
        if (_cachedHeader is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2))
            return Task.FromResult<string?>(_cachedHeader);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (!_bridge.CoreAsync.IsCompleted)
                {
                    var done = await Task.WhenAny(_bridge.CoreAsync, Task.Delay(TimeSpan.FromSeconds(10)));
                    if (done != _bridge.CoreAsync)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }
                }
                var core = await _bridge.CoreAsync;
                var cookies = await core.CookieManager.GetCookiesAsync("https://music.youtube.com");
                if (!cookies.Any(c => SessionCookieNames.Contains(c.Name)))
                {
                    _cachedHeader = null;
                    tcs.TrySetResult(null);
                    return;
                }
                _cachedHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                _cachedAt = DateTime.UtcNow;
                tcs.TrySetResult(_cachedHeader);
            }
            catch (Exception)
            {
                tcs.TrySetResult(null);
            }
        });
        if (!queued) tcs.TrySetResult(null);
        return tcs.Task;
    }

    public async Task RefreshAccountAsync()
    {
        try
        {
            Account = await App.Api.GetAccountAsync();
        }
        catch (AuthExpiredException)
        {
            Account = null;
        }
        catch (Exception)
        {
            // Account details are cosmetic; keep the session.
        }
    }

    /// <summary>Called by the sign-in window once Google redirects back to YouTube Music.</summary>
    public async Task<bool> CompleteSignInAsync()
    {
        _cachedHeader = null;
        if (!await HasSessionAsync()) return false;
        IsSignedIn = true;
        _bridge.Reload();
        await RefreshAccountAsync();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Signs out properly: ends the session on Google's side first (so a copied cookie stops working),
    /// then wipes every cookie and all site data (storage, cache, service workers) from the profile.
    /// </summary>
    public async Task SignOutAsync()
    {
        var core = await _bridge.CoreAsync;
        _cachedHeader = null;
        if (IsSignedIn) await _bridge.NavigateAndWaitAsync("https://www.youtube.com/logout", TimeSpan.FromSeconds(10));
        await WebViewHost.ClearProfileAsync(core);
        IsSignedIn = false;
        Account = null;
        await _bridge.RestoreConsentAsync();
        _bridge.Reload();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wipes the on-disk session when the user turns "stay signed in" off.</summary>
    public async Task ForgetStoredSessionAsync()
    {
        await SignOutAsync();
    }

    public static bool IsSessionCookie(CoreWebView2Cookie c) => SessionCookieNames.Contains(c.Name);
}
