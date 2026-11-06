using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Tapedeck.Services;
using Tapedeck.Services.Api;

namespace Tapedeck;

public partial class App : Application
{
    private MainWindow? _window;

    public static SettingsService Settings { get; private set; } = null!;
    public static InnerTubeClient Api { get; private set; } = null!;
    public static PlayerBridge Bridge { get; private set; } = null!;
    public static AuthService Auth { get; private set; } = null!;
    public static PlayerService Player { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = null!;
    public static MediaControlsService? MediaControls { get; private set; }
    public static DiscordPresenceService Discord { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        // Before any WebView2 exists: ignore environment overrides that could open a debugging port
        // or swap the browser runtime / profile underneath the signed-in session.
        WebViewHost.ScrubEnvironment();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the music going: log and swallow UI-thread exceptions from async void handlers.
            System.Diagnostics.Debug.WriteLine(e.Exception);
            LogError(e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = new SettingsService();
        Api = new InnerTubeClient();
        Bridge = new PlayerBridge();
        Auth = new AuthService(Dispatcher, Bridge);
        Api.CookieProvider = Auth.GetCookieHeaderAsync;
        Player = new PlayerService(Bridge, Api, Settings);
        Lyrics = new LyricsService(Api);
        Discord = new DiscordPresenceService(Player, Settings, Dispatcher);

        Bridge.Ready += (_, ready) =>
        {
            if (!string.IsNullOrEmpty(ready.ClientVersion)) Api.ClientVersion = ready.ClientVersion;
            if (!string.IsNullOrEmpty(ready.VisitorData)) Api.VisitorData = ready.VisitorData;
            if (!string.IsNullOrEmpty(ready.Language)) Api.Language = ready.Language;
            if (!string.IsNullOrEmpty(ready.Region)) Api.Region = ready.Region;
        };

        try
        {
            MediaControls = new MediaControlsService(Player, Dispatcher);
        }
        catch (Exception ex)
        {
            // Media keys are a nicety; never block startup on them.
            LogError(ex);
        }

        _window = MainWindow = new MainWindow();
        _window.Closed += (_, _) =>
        {
            Settings.SaveNow();
            MediaControls?.Dispose();
            Discord.Dispose();
        };
        _window.Activate();
    }

    /// <summary>Restarts the app (used when switching between the on-disk and in-memory profiles).</summary>
    public static void Restart()
    {
        try
        {
            Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        }
        catch (Exception)
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) System.Diagnostics.Process.Start(exe);
            Environment.Exit(0);
        }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(SettingsService.DataFolder, "errors.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Nowhere left to report to.
        }
    }
}
