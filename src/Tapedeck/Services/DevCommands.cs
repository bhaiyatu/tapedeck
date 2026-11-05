#if DEBUG
// Debug builds only. Release builds contain none of this: no file-driven commands, no script channel.
using Tapedeck.Models;
using Tapedeck.Views;

namespace Tapedeck.Services;

/// <summary>
/// Debug only (TAPEDECK_DEBUG=1): watches %LOCALAPPDATA%\Tapedeck\command.txt and runs one app
/// command per write, e.g. "search daft punk", "album MPREb_…", "artist UC…", "play &lt;videoId&gt;",
/// "panel lyrics", "home", "explore", "library", "settings", "back", "mute". Used to drive the UI
/// for screenshots and manual testing without synthetic input.
/// </summary>
public static class DevCommands
{
    private static FileSystemWatcher? _watcher;

    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("TAPEDECK_DEBUG") != "1") return;
        var dispatcher = App.Dispatcher;
        _watcher = new FileSystemWatcher(SettingsService.DataFolder, "command.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        var last = DateTime.MinValue;
        void Handle()
        {
            dispatcher.TryEnqueue(async () =>
            {
                // FileSystemWatcher fires twice per write; debounce.
                if (DateTime.UtcNow - last < TimeSpan.FromMilliseconds(400)) return;
                last = DateTime.UtcNow;
                await Task.Delay(100);
                string line;
                try
                {
                    line = (await File.ReadAllTextAsync(Path.Combine(SettingsService.DataFolder, "command.txt"))).Trim();
                }
                catch (IOException)
                {
                    return;
                }
                try
                {
                    await RunAsync(line);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            });
        }
        _watcher.Changed += (_, _) => Handle();
        _watcher.Created += (_, _) => Handle();
    }

    private static async Task RunAsync(string line)
    {
        var space = line.IndexOf(' ');
        var cmd = space < 0 ? line : line[..space];
        var arg = space < 0 ? "" : line[(space + 1)..].Trim();
        switch (cmd.ToLowerInvariant())
        {
            case "home": Nav.ToBrowse("FEmusic_home", null, null); break;
            case "explore": Nav.ToBrowse("FEmusic_explore", null, null); break;
            case "browse": Nav.ToBrowse(arg, null, null); break;
            case "library": Nav.To(typeof(LibraryPage)); break;
            case "settings": Nav.To(typeof(SettingsPage)); break;
            case "search": Nav.ToSearch(arg); break;
            case "album":
            case "playlist": Nav.ToCollection(arg); break;
            case "artist": Nav.ToArtist(arg); break;
            case "back": if (Nav.Frame?.CanGoBack == true) Nav.Frame.GoBack(); break;
            case "play":
                await App.Player.PlaySongAsync(new MusicItem { Kind = ItemKind.Song, VideoId = arg, Title = arg });
                break;
            case "playsearch":
                var results = await App.Api.SearchAsync(arg);
                var song = results.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Song);
                if (song is not null) await App.Player.PlaySongAsync(song);
                break;
            case "pause": App.Player.Pause(); break;
            case "resume": App.Player.Play(); break;
            case "next": App.Player.Next(); break;
            case "mute": App.Player.IsMuted = true; break;
            case "unmute": App.Player.IsMuted = false; break;
            case "panel": App.MainWindow.ShowPanelFromDev(arg is "none" or "" ? null : arg); break;
            case "theme": App.MainWindow.ApplyTheme(arg); break;
            case "signin": _ = App.MainWindow.SignInAsync(); break;
            case "toast": App.MainWindow.ShowToast(arg); break;
            case "privatecheck": await PrivateProfileCheckAsync(); break;
        }
    }

    /// <summary>
    /// Verifies the "don't stay signed in" design: two InPrivate controllers with the same profile name
    /// share cookies (so sign-in reaches the player), and the normal on-disk profile never sees them.
    /// Writes the result to privatecheck.out.
    /// </summary>
    private static async Task PrivateProfileCheckAsync()
    {
        var env = await WebViewHost.GetEnvironmentAsync();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var windowRef = Microsoft.Web.WebView2.Core.CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd);

        Microsoft.Web.WebView2.Core.CoreWebView2ControllerOptions PrivateOptions()
        {
            var o = env.CreateCoreWebView2ControllerOptions();
            o.ProfileName = WebViewHost.PrivateProfileName;
            o.IsInPrivateModeEnabled = true;
            return o;
        }

        var a = await env.CreateCoreWebView2ControllerAsync(windowRef, PrivateOptions());
        var b = await env.CreateCoreWebView2ControllerAsync(windowRef, PrivateOptions());
        var disk = await env.CreateCoreWebView2ControllerAsync(windowRef);
        try
        {
            var probe = a.CoreWebView2.CookieManager.CreateCookie("TD_PRIVATE_PROBE", "1", ".google.com", "/");
            probe.IsSecure = true;
            a.CoreWebView2.CookieManager.AddOrUpdateCookie(probe);
            await Task.Delay(300);
            var shared = (await b.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com")).Any(c => c.Name == "TD_PRIVATE_PROBE");
            var leaked = (await disk.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com")).Any(c => c.Name == "TD_PRIVATE_PROBE");
            await File.WriteAllTextAsync(Path.Combine(SettingsService.DataFolder, "privatecheck.out"),
                $"sharedBetweenPrivateWebViews={shared} visibleInDiskProfile={leaked} inPrivate={a.CoreWebView2.Profile.IsInPrivateModeEnabled}");
        }
        finally
        {
            a.Close();
            b.Close();
            disk.Close();
        }
    }
}
#endif
