using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Tapedeck.Models;
using Tapedeck.Services.Api;

namespace Tapedeck.Services;

/// <summary>Where the Google session stands.</summary>
public enum SessionState
{
    /// <summary>Startup, before the stored cookies have been checked.</summary>
    Unknown,
    SignedOut,
    SignedIn,
    /// <summary>Ending the session with Google and wiping the profile; no cookies are handed out.</summary>
    SigningOut,
    /// <summary>Google rejected the stored session. Treated as signed out until the user signs in again.</summary>
    Expired,
}

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
    // Bumped on every state change, so a cookie read that started before the change isn't cached after it.
    private int _generation;
    private bool _verifying;

    public AuthService(DispatcherQueue dispatcher, PlayerBridge bridge)
    {
        _dispatcher = dispatcher;
        _bridge = bridge;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; private set; }

    public bool IsSignedOut => !IsSignedIn;

    public SessionState State { get; private set; } = SessionState.Unknown;

    [ObservableProperty]
    public partial AccountInfo? Account { get; set; }

    /// <summary>Raised after sign-in, sign-out or expiry so pages can reload personalised content.</summary>
    public event EventHandler? SessionChanged;

    /// <summary>Raised when Google rejects the stored session.</summary>
    public event EventHandler? SessionExpired;

    private void SetState(SessionState state)
    {
        State = state;
        _generation++;
        _cachedHeader = null;
        IsSignedIn = state == SessionState.SignedIn;
    }

    /// <summary>Whether API requests may carry the session cookies in this state.</summary>
    private bool MayUseCookies => State is SessionState.Unknown or SessionState.SignedIn or SessionState.SignedOut;

    /// <summary>Checks the stored cookies at startup.</summary>
    public async Task InitializeAsync()
    {
        SetState(await HasSessionAsync() ? SessionState.SignedIn : SessionState.SignedOut);
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
        if (_cachedHeader is { } cached && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2))
            return Task.FromResult<string?>(cached);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (!MayUseCookies)
                {
                    tcs.TrySetResult(null);
                    return;
                }
                var generation = _generation;
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
                // Signing out (or an expiry) may have started while the cookies were being read.
                if (!MayUseCookies || !cookies.Any(c => SessionCookieNames.Contains(c.Name)))
                {
                    tcs.TrySetResult(null);
                    return;
                }
                var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                if (generation == _generation)
                {
                    _cachedHeader = header;
                    _cachedAt = DateTime.UtcNow;
                }
                tcs.TrySetResult(header);
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
            Expire();
        }
        catch (Exception)
        {
            // Account details are cosmetic; keep the session.
        }
    }

    /// <summary>
    /// The API client got a 401/403 on a request that carried the session (any thread). One rejected
    /// call can be specific to that endpoint, so ask for the account before treating the session as gone.
    /// </summary>
    public void HandleSessionRejected() => _dispatcher.TryEnqueue(async () =>
    {
        if (State != SessionState.SignedIn || _verifying) return;
        _verifying = true;
        try
        {
            await RefreshAccountAsync();
        }
        finally
        {
            _verifying = false;
        }
    });

    private void Expire()
    {
        if (State != SessionState.SignedIn) return;
        Account = null;
        SetState(SessionState.Expired);
        SessionExpired?.Invoke(this, EventArgs.Empty);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the sign-in window once Google redirects back to YouTube Music.</summary>
    public async Task<bool> CompleteSignInAsync()
    {
        if (!await HasSessionAsync()) return false;
        SetState(SessionState.SignedIn);
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
        if (State == SessionState.SigningOut) return;
        var core = await _bridge.CoreAsync;
        var hadSession = State is SessionState.SignedIn or SessionState.Expired;
        // From here on no request carries the session, and nothing re-caches it.
        SetState(SessionState.SigningOut);
        Account = null;
        try
        {
            if (hadSession) await _bridge.NavigateAndWaitAsync("https://www.youtube.com/logout", TimeSpan.FromSeconds(10));
            await WebViewHost.ClearProfileAsync(core);
            await _bridge.RestoreConsentAsync();
            _bridge.Reload();
        }
        finally
        {
            SetState(SessionState.SignedOut);
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Wipes the on-disk session when the user turns "stay signed in" off.</summary>
    public async Task ForgetStoredSessionAsync()
    {
        await SignOutAsync();
    }

    public static bool IsSessionCookie(CoreWebView2Cookie c) => SessionCookieNames.Contains(c.Name);
}
