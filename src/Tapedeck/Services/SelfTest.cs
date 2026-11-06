using System.Diagnostics;
using System.Text;
using Tapedeck.Models;

namespace Tapedeck.Services;

/// <summary>
/// End-to-end playback check run with <c>Tapedeck.exe --selftest</c>: drives the real hidden player
/// (muted) through play, next, seek-to-end auto-advance, pause/resume and album playback, then
/// writes a report to %LOCALAPPDATA%\Tapedeck\selftest.log and exits with 0 (pass) or 1 (fail).
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static bool IsRequested => Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync()
    {
        var player = App.Player;
        var bridgeEvents = new List<string>();
        App.Bridge.Advanced += (_, id) => bridgeEvents.Add($"advanced:{id}");
        App.Bridge.Error += (_, msg) => bridgeEvents.Add($"error:{msg}");

        try
        {
            Write("Waiting for the player page…");
            var ready = new TaskCompletionSource<BridgeReady>();
            App.Bridge.Ready += (_, r) => ready.TrySetResult(r);
            var done = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            Check(done == ready.Task, "player page became ready");
            if (done != ready.Task)
            {
                var diag = await App.Bridge.EvalAsync("""
                    (() => {
                      const out = {};
                      const app = document.querySelector('ytmusic-app');
                      const names = new Set();
                      let o = app;
                      while (o && o !== HTMLElement.prototype) { Object.getOwnPropertyNames(o).forEach(n => names.add(n)); o = Object.getPrototypeOf(o); }
                      out.appMethods = [...names].filter(n => /command|navigat|resolve|endpoint|handle|play|queue/i.test(n)).slice(0, 80);
                      out.appCtor = app && app.constructor && app.constructor.name;
                      out.polymer = !!window.Polymer;
                      const p = document.querySelector('ytmusic-player');
                      out.playerKeys = p ? Object.keys(p).slice(0, 40) : null;
                      out.globals = Object.keys(window).filter(k => /^(yt|ytcfg|ytmusic|_yt|Polymer)/i.test(k)).slice(0, 40);
                      return JSON.stringify(out);
                    })()
                    """);
                Write($"  diag: {diag}");
                return;
            }
            var info = ready.Task.Result;
            Write($"  clientVersion={info.ClientVersion} loggedIn={info.LoggedIn} hl={info.Language} gl={info.Region}");
            player.IsMuted = true;

            // 1. Play a song (radio).
            var search = await App.Api.SearchAsync("daft punk harder better faster stronger", null);
            var song = search.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Song && i.VideoId is not null);
            Check(song is not null, "found a song to play");
            if (song is null) return;
            Write($"Playing {song.Title} [{song.VideoId}]");
            await player.PlaySongAsync(song);
            // Signed-out playback can open with an ad (up to ~30s, sometimes two); wait through it.
            var sawAd = false;
            Check(await WaitFor(() =>
                {
                    sawAd |= player.IsAd;
                    return player.IsPlaying && !player.IsAd && player.Position > 1.5 && player.Current?.VideoId == song.VideoId;
                }, 90),
                $"song is playing (pos={player.Position:F1}, playing={player.IsPlaying}, ad={player.IsAd}, sawAd={sawAd})");
            Check(player.CurrentIndex == 0, $"an ad didn't skip the song (index={player.CurrentIndex})");
            Check(player.Duration > 30, $"duration reported ({player.Duration:F0}s)");
            Check(await WaitFor(() => player.Queue.Count > 5, 10), $"radio queue filled ({player.Queue.Count} items)");

            // 2. Next.
            var second = player.Queue[1];
            player.Next();
            Check(await WaitFor(() => player.Current == second && player.IsPlaying && player.Position > 1.5 && !player.IsAd, 90),
                $"next track plays: {second.Title} (pos={player.Position:F1})");

            // 3. Seek near the end and let it auto-advance.
            var third = player.Queue[2];
            await WaitFor(() => player.Duration > 10, 5);
            player.Seek(player.Duration - 4);
            // Signed out, YouTube Music may slot an ad (up to ~30s) between songs, so allow for one.
            Check(await WaitFor(() => player.Current == third && player.CurrentIndex == 2, 90),
                $"auto-advanced at end of track to {third.Title} (index={player.CurrentIndex})");
            Check(await WaitFor(() => player.IsPlaying && player.Position > 1 && !player.IsAd, 90),
                $"auto-advanced track is playing (pos={player.Position:F1})");

            // 3b. YouTube Music changes track by itself, with no 'ended' event (its crossfade does this
            //     a few seconds before a song ends). Simulated by navigating the page behind the app's back.
            //     Position > 1 on the expected track proves the page really plays it: reports for any
            //     other track are ignored.
            // Wait past the bridge's guard window: changes right after the app picks a song are treated
            // as interference and undone; later ones (like crossfade near a song's end) are followed.
            await Task.Delay(16000);
            Check(player.CurrentIndex == 2 && player.IsPlaying, $"no extra skip after the natural end (still index {player.CurrentIndex})");
            var before = player.CurrentIndex;
            var ourNext = player.Queue[before + 1];
            await App.Bridge.EvalAsync(PageNavigateScript(ourNext.VideoId!));
            Check(await WaitFor(() => player.Current == ourNext && player.CurrentIndex == before + 1 && player.IsPlaying && player.Position > 1 && !player.IsAd, 60),
                $"page moved to our next song by itself: app followed to {ourNext.Title} (index={player.CurrentIndex}, pos={player.Position:F1})");

            await Task.Delay(16000);
            before = player.CurrentIndex;
            ourNext = player.Queue[before + 1];
            var stray = new[] { "dQw4w9WgXcQ", "kJQP7kiw5Fk", "9bZkp7q19f0" }.First(id => player.Queue.All(q => q.VideoId != id));
            await App.Bridge.EvalAsync(PageNavigateScript(stray));
            Check(await WaitFor(() => player.Current == ourNext && player.CurrentIndex == before + 1 && player.IsPlaying && player.Position > 1 && !player.IsAd, 60),
                $"page wandered to an unrelated song: app switched to its own next, {ourNext.Title} (index={player.CurrentIndex}, pos={player.Position:F1})");

            // (iii) Something else taking over right after the app picked a song is undone.
            await Task.Delay(6000);
            before = player.CurrentIndex;
            var chosen = player.Current;
            var stray2 = new[] { "9bZkp7q19f0", "kJQP7kiw5Fk", "dQw4w9WgXcQ" }.First(id => id != stray && player.Queue.All(q => q.VideoId != id));
            await App.Bridge.EvalAsync(PageNavigateScript(stray2));
            await Task.Delay(4000);
            Check(await WaitFor(() => player.Current == chosen && player.CurrentIndex == before && player.IsPlaying && player.Position > 1 && !player.IsAd, 30),
                $"interference right after a switch was undone: still {chosen?.Title} (index={player.CurrentIndex}, pos={player.Position:F1})");

            // 4. Pause / resume.
            player.Pause();
            await Task.Delay(1500);
            var pausedAt = player.Position;
            await Task.Delay(1500);
            Check(!player.IsPlaying && Math.Abs(player.Position - pausedAt) < 0.5, $"pause holds position ({pausedAt:F1} → {player.Position:F1})");
            player.Play();
            Check(await WaitFor(() => player.IsPlaying && player.Position > pausedAt + 1, 10), "resume continues playback");

            // 5. Previous (restart then go back).
            player.Previous();
            Check(await WaitFor(() => player.Position < 3, 6), "previous after 3s restarts the track");

            // 6. Album playback from a collection page.
            var album = search.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Album && i.BrowseId is not null);
            if (album is not null)
            {
                await player.PlayCollectionAsync(album.BrowseId!);
                var first = player.Queue.FirstOrDefault();
                Check(first is not null && player.Queue.Count > 3, $"album queue loaded: {album.Title} ({player.Queue.Count} tracks)");
                Check(await WaitFor(() => player.Current == first && player.IsPlaying && player.Position > 1 && !player.IsAd, 90),
                    $"album track 1 plays: {first?.Title}");
            }

            // 6b. A playback error schedules a skip of that track; picking another song yourself before
            //     it fires must not get skipped instead.
            if (player.Queue.Count > 3)
            {
                await App.Bridge.EvalAsync("window.chrome.webview.postMessage({ type: 'error', code: -1, message: 'self-test error' })");
                await Task.Delay(300);
                var picked = player.Queue[2];
                player.PlayIndex(2);
                await Task.Delay(4000);
                Check(player.Current == picked && player.CurrentIndex == 2,
                    $"an error's pending skip didn't skip the song picked after it (now {player.Current?.Title}, index {player.CurrentIndex})");
            }

            // 7. Lyrics lookup for whatever is playing.
            await WaitFor(() => player.TrackInfoLoaded, 10);
            var lyrics = await App.Lyrics.GetAsync(player.Current!, player.LyricsBrowseId, player.Duration, CancellationToken.None);
            Write($"  lyrics: {(lyrics is null ? "none" : $"{lyrics.Source}, synced={lyrics.IsSynced}, lines={lyrics.Lines.Count}")}");

            Write($"Bridge events: {string.Join(", ", bridgeEvents)}");
        }
        catch (Exception ex)
        {
            Check(false, $"exception: {ex}");
        }
        finally
        {
            player.Pause();
            Write(_failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({_failures})");
            File.WriteAllText(Path.Combine(SettingsService.DataFolder, "selftest.log"), Log.ToString());
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
    }

    /// <summary>Makes YouTube Music's page switch tracks on its own, bypassing the bridge.</summary>
    private static string PageNavigateScript(string videoId) =>
        "(() => { const a = document.querySelector('ytmusic-app'); " +
        "a.dispatchEvent(new CustomEvent('yt-navigate', { bubbles: true, composed: true, " +
        $"detail: {{ endpoint: {{ watchEndpoint: {{ videoId: '{videoId}' }} }} }} }})); return 'ok'; }})()";

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Write($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private static void Write(string line) => Log.AppendLine($"[{Clock.Elapsed.TotalSeconds,6:F1}s] {line}");
}
