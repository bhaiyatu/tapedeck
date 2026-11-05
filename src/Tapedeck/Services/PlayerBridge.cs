using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Tapedeck.Services;

public sealed record BridgeState(string? VideoId, bool Playing, bool Buffering, double Position, double Duration, bool Ad);

public sealed record BridgeReady(string? ClientVersion, string? VisitorData, bool LoggedIn, string? Language, string? Region);

/// <summary>
/// Hosts music.youtube.com in a hidden WebView2 and drives its player through the injected
/// player-bridge.js. This is how DRM-protected / Premium audio plays: the official web player does it.
/// </summary>
public sealed class PlayerBridge
{
    private const string PlayerUrl = "https://music.youtube.com/";

    private WebView2? _view;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CoreWebView2> _core = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<BridgeState>? StateChanged;
    public event EventHandler<string>? Advanced;
    public event EventHandler? Ended;
    public event EventHandler<string>? Error;
    public event EventHandler<BridgeReady>? Ready;
    public event EventHandler<Uri>? BlockedByInterstitial;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>Completes once the WebView2 core exists (for cookie access), before the page is ready.</summary>
    public Task<CoreWebView2> CoreAsync => _core.Task;

    public async Task InitializeAsync(WebView2 view)
    {
        _view = view;
        var env = await WebViewHost.GetEnvironmentAsync();
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;

        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        await EnsureConsentCookieAsync(core);

        var script = LoadScript("player-bridge.js");
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script);

        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += (_, e) =>
        {
            // A real navigation (sign-in change, reload) throws away the page; wait for "ready" again.
            if (e.IsRedirected || !e.Uri.StartsWith(PlayerUrl, StringComparison.Ordinal) || IsReady)
                ResetReady();
        };
        core.NavigationCompleted += (_, e) =>
        {
            DebugLog($"navigation completed: {core.Source} success={e.IsSuccess} status={e.HttpStatusCode} error={e.WebErrorStatus}");
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host != "music.youtube.com")
                BlockedByInterstitial?.Invoke(this, uri);
        };
        // Don't let the page open popups (share dialogs, help links) in new windows.
        core.NewWindowRequested += (_, e) => e.Handled = true;

        _core.TrySetResult(core);
        core.Navigate(PlayerUrl);
        if (DebugEnabled) StartEvalChannel(core);
    }

    private FileSystemWatcher? _evalWatcher;

    /// <summary>
    /// Debug only: when %LOCALAPPDATA%\Tapedeck\eval.js is written, run it in the player page and
    /// write the result to eval.out. Lets you poke at YouTube Music's page without rebuilding.
    /// </summary>
    private void StartEvalChannel(CoreWebView2 core)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _evalWatcher = new FileSystemWatcher(SettingsService.DataFolder, "eval.js")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        var busy = 0;
        void Run()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            dispatcher.TryEnqueue(async () =>
            {
                var path = Path.Combine(SettingsService.DataFolder, "eval.js");
                string output;
                try
                {
                    await Task.Delay(150);
                    var js = await File.ReadAllTextAsync(path);
                    output = await core.ExecuteScriptAsync(js);
                }
                catch (Exception ex)
                {
                    output = "ERROR " + ex.Message;
                }
                await File.WriteAllTextAsync(Path.Combine(SettingsService.DataFolder, "eval.out"), output);
                Interlocked.Exchange(ref busy, 0);
            });
        }
        _evalWatcher.Changed += (_, _) => Run();
        _evalWatcher.Created += (_, _) => Run();
    }

    private void ResetReady()
    {
        if (_ready.Task.IsCompleted) _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Without a consent choice, visitors in some regions are redirected to consent.youtube.com,
    /// which would stall playback. Pre-select "reject all" (SOCS=CAI), the privacy-preserving option,
    /// unless a choice already exists.
    /// </summary>
    private static async Task EnsureConsentCookieAsync(CoreWebView2 core)
    {
        var existing = await core.CookieManager.GetCookiesAsync("https://www.youtube.com");
        if (existing.Any(c => c.Name == "SOCS")) return;
        var cookie = core.CookieManager.CreateCookie("SOCS", "CAI", ".youtube.com", "/");
        cookie.IsSecure = true;
        cookie.Expires = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        core.CookieManager.AddOrUpdateCookie(cookie);
    }

    /// <summary>Set TAPEDECK_DEBUG=1 (or run --selftest) to log bridge traffic to %LOCALAPPDATA%\Tapedeck\bridge.log.</summary>
    private static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("TAPEDECK_DEBUG") == "1" || SelfTest.IsRequested;

    public static void DebugLog(string line)
    {
        if (!DebugEnabled) return;
        try
        {
            File.AppendAllText(Path.Combine(SettingsService.DataFolder, "bridge.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    public static string LoadScript(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().First(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Reload()
    {
        ResetReady();
        _view?.CoreWebView2?.Navigate(PlayerUrl);
    }

    public bool IsMuted
    {
        get => _view?.CoreWebView2?.IsMuted ?? false;
        set
        {
            if (_view?.CoreWebView2 is { } core) core.IsMuted = value;
        }
    }

    public void OpenDevTools() => _view?.CoreWebView2?.OpenDevToolsWindow();

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try
        {
            msg = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (msg is null) return;

        var type = (string?)msg["type"];
        if (type != "state") DebugLog($"js: {e.WebMessageAsJson}");
        switch (type)
        {
            case "state":
                StateChanged?.Invoke(this, ParseState(msg));
                break;
            case "advanced" when (string?)msg["videoId"] is { } id:
                Advanced?.Invoke(this, id);
                break;
            case "ended":
                StateChanged?.Invoke(this, ParseState(msg));
                Ended?.Invoke(this, EventArgs.Empty);
                break;
            case "error":
                Error?.Invoke(this, (string?)msg["message"] is { Length: > 0 } m ? m : $"Playback error ({msg["code"]})");
                break;
            case "ready":
                _ready.TrySetResult();
                Ready?.Invoke(this, new BridgeReady(
                    (string?)msg["clientVersion"],
                    (string?)msg["visitorData"],
                    msg["loggedIn"]?.GetValue<bool>() ?? false,
                    (string?)msg["hl"],
                    (string?)msg["gl"]));
                break;
        }
    }

    private static BridgeState ParseState(JsonNode m) => new(
        (string?)m["videoId"],
        m["playing"]?.GetValue<bool>() ?? false,
        m["buffering"]?.GetValue<bool>() ?? false,
        m["position"]?.GetValue<double>() ?? 0,
        m["duration"]?.GetValue<double>() ?? 0,
        m["ad"]?.GetValue<bool>() ?? false);

    private async Task<string?> RunAsync(string js, bool waitForReady = true)
    {
        if (_view is null) return null;
        if (waitForReady)
        {
            var done = await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (done != _ready.Task) return null;
        }
        if (_view.CoreWebView2 is not { } core) return null;
        try
        {
            var result = await core.ExecuteScriptAsync(js);
            DebugLog($"exec {js} → {result}");
            return result;
        }
        catch (Exception)
        {
            // The page may be mid-navigation; the next command will retry.
            return null;
        }
    }

    /// <summary>Runs arbitrary script without waiting for readiness (diagnostics).</summary>
    public Task<string?> EvalAsync(string js) => RunAsync(js, waitForReady: false);

    private static string Lit(string? s) => s is null ? "null" : JsonSerializer.Serialize(s);

    public async Task LoadAsync(string videoId, string? nextVideoId)
    {
        var result = await RunAsync($"window.__td && __td.load({Lit(videoId)}, {Lit(nextVideoId)})");
        if (result == "\"not-ready\"")
        {
            // The app element can briefly lack its router after a navigation; try once more.
            await Task.Delay(750);
            await RunAsync($"window.__td && __td.load({Lit(videoId)}, {Lit(nextVideoId)})");
        }
    }

    public Task SetNextAsync(string? nextVideoId, bool repeatOne) =>
        RunAsync($"window.__td && __td.setNext({Lit(nextVideoId)}, {(repeatOne ? "true" : "false")})");

    public Task PlayAsync() => RunAsync("window.__td && __td.play()");

    public Task PauseAsync() => RunAsync("window.__td && __td.pause()");

    public Task StopAsync() => RunAsync("window.__td && __td.stop()");

    public Task SeekAsync(double seconds) =>
        RunAsync($"window.__td && __td.seek({seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    public Task SetVolumeAsync(double volume) =>
        RunAsync($"window.__td && __td.setVolume({volume.ToString(System.Globalization.CultureInfo.InvariantCulture)})", waitForReady: false);
}
