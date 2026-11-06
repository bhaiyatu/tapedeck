// Usage: dotnet run -- [home|explore|search <q>|album <id>|playlist <id>|artist <id>|queue <videoId>|lyrics <videoId>|discord [videoId] [seconds]|all]
using System.Text.Json.Nodes;
using Tapedeck.Models;
using Tapedeck.Services.Api;
using Tapedeck.Services.Discord;

var api = new InnerTubeClient();
var cmd = args.Length > 0 ? args[0] : "all";
var arg = args.Length > 1 ? string.Join(' ', args.Skip(1)) : null;
var failures = 0;

void Check(bool ok, string what)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}");
    Console.ResetColor();
    if (!ok) failures++;
}

void PrintShelves(IEnumerable<Shelf> shelves, int perShelf = 3)
{
    foreach (var s in shelves)
    {
        Console.WriteLine($"  {s}");
        foreach (var i in s.Items.Take(perShelf))
            Console.WriteLine($"     - {i}  [video={i.VideoId} browse={i.BrowseId} pl={i.PlaylistId} dur={i.DurationText} artists={i.ArtistText} album={i.Album?.Name} thumb={(i.ThumbnailUrl is null ? "none" : "yes")}]");
    }
}

async Task Home()
{
    Console.WriteLine("== HOME");
    var page = await api.GetShelvesAsync("FEmusic_home");
    PrintShelves(page.Shelves);
    Check(page.Shelves.Count > 0, "home has shelves");
    Check(page.Shelves.SelectMany(s => s.Items).All(i => i.Title.Length > 0), "all items have titles");
    Check(page.Shelves.SelectMany(s => s.Items).All(i => i.VideoId is not null || i.BrowseId is not null), "all items are actionable");
    Console.WriteLine($"  chips: {string.Join(", ", page.Chips.Select(c => c.Text))}");
    if (page.Continuation is not null)
    {
        var more = await api.ContinueShelvesAsync(page.Continuation);
        Console.WriteLine($"  continuation → {more.Shelves.Count} more shelves");
        PrintShelves(more.Shelves, 1);
        Check(more.Shelves.Count > 0, "home continuation returns shelves");
    }
}

async Task Explore()
{
    Console.WriteLine("== EXPLORE");
    var page = await api.GetShelvesAsync("FEmusic_explore");
    PrintShelves(page.Shelves, 2);
    Check(page.Shelves.Count > 0, "explore has shelves");
    var mood = page.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Mood);
    if (mood is not null)
    {
        var moodPage = await api.GetShelvesAsync(mood.BrowseId!, mood.BrowseParams);
        Console.WriteLine($"  mood '{mood.Title}':");
        PrintShelves(moodPage.Shelves, 2);
        Check(moodPage.Shelves.Count > 0, "mood page has shelves");
    }
    var moodsAndGenres = await api.GetShelvesAsync("FEmusic_moods_and_genres");
    Check(moodsAndGenres.Shelves.Any(s => s.Style == ShelfStyle.Chips), "moods & genres page has chip shelves");
}

async Task<SearchResults> Search(string q)
{
    Console.WriteLine($"== SEARCH '{q}'");
    var r = await api.SearchAsync(q);
    Console.WriteLine($"  top: {r.TopResult}  (browse={r.TopResult?.BrowseId} video={r.TopResult?.VideoId})");
    PrintShelves(r.Shelves, 3);
    Console.WriteLine($"  filters: {string.Join(", ", r.Filters.Select(f => f.Text))}");
    Check(r.Shelves.Count > 0, "search has shelves");
    Check(r.Filters.Count > 0, "search has filter chips");
    var songs = r.Filters.FirstOrDefault(f => f.Text == "Songs");
    if (songs?.Params is not null)
    {
        var filtered = await api.SearchAsync(q, songs.Params);
        var items = filtered.Shelves.SelectMany(s => s.Items).ToList();
        Console.WriteLine($"  songs filter → {items.Count} items, continuation={(filtered.Continuation is null ? "no" : "yes")}");
        PrintShelves(filtered.Shelves, 3);
        Check(items.Count > 0 && items.All(i => i.VideoId is not null), "songs filter returns playable songs");
        Check(items.All(i => i.Artists.Count > 0), "songs have artists");
        Check(items.All(i => i.DurationText is not null), "songs have durations");
        if (filtered.Continuation is not null)
        {
            var more = await api.ContinueSearchAsync(filtered.Continuation);
            Check(more.Shelves.SelectMany(s => s.Items).Any(), "search continuation returns items");
        }
    }
    var suggestions = await api.GetSuggestionsAsync(q[..Math.Min(q.Length, 5)]);
    Console.WriteLine($"  suggestions: {string.Join(" | ", suggestions)}");
    Check(suggestions.Count > 0, "suggestions returned");
    return r;
}

async Task Collection(string id)
{
    Console.WriteLine($"== COLLECTION {id}");
    var c = await api.GetCollectionAsync(id);
    Console.WriteLine($"  {c.Title} | {c.Subtitle} | {c.SecondSubtitle} | artists={string.Join(",", c.Artists.Select(a => a.Name + ":" + a.Id))} pl={c.PlaylistId} cont={(c.Continuation is null ? "no" : "yes")}");
    foreach (var t in c.Tracks.Take(4)) Console.WriteLine($"     {t.Index}. {t}  [{t.VideoId}] {t.DurationText} by {t.ArtistText}");
    PrintShelves(c.Related, 2);
    Check(c.Title.Length > 0, "collection has title");
    Check(c.Tracks.Count > 0, "collection has tracks");
    Check(c.PlaylistId is not null, "collection has playlist id");
    Check(c.Tracks.All(t => t.DurationText is not null), "tracks have durations");
    if (c.Continuation is not null)
    {
        var (more, next) = await api.ContinueCollectionAsync(c.Continuation);
        Console.WriteLine($"  continuation → {more.Count} more tracks (next={(next is null ? "no" : "yes")})");
        Check(more.Count > 0, "collection continuation returns tracks");
    }
}

async Task Artist(string id)
{
    Console.WriteLine($"== ARTIST {id}");
    var a = await api.GetArtistAsync(id);
    Console.WriteLine($"  {a.Name} | {a.AudienceText} | shuffle={(a.ShuffleEndpoint is null ? "no" : "yes")} radio={(a.RadioEndpoint is null ? "no" : "yes")}");
    PrintShelves(a.Shelves, 2);
    Check(a.Name.Length > 0, "artist has name");
    Check(a.Shelves.Count > 0, "artist has shelves");
}

async Task Queue(string videoId)
{
    Console.WriteLine($"== QUEUE {videoId}");
    var q = await api.GetQueueAsync(videoId, "RDAMVM" + videoId);
    Console.WriteLine($"  {q.Items.Count} items, selected={q.SelectedIndex}, pl={q.PlaylistId}, radio={q.IsRadioContinuation}");
    foreach (var t in q.Items.Take(4)) Console.WriteLine($"     {t}  [{t.VideoId}] {t.DurationText} by {t.ArtistText} album={t.Album?.Name}");
    Check(q.Items.Count > 1, "queue has items");
    Check(q.Items.All(i => i.Artists.Count > 0), "queue items have artists");
    if (q.Continuation is not null)
    {
        var more = await api.ContinueQueueAsync(q.Continuation, q.PlaylistId);
        Console.WriteLine($"  continuation → {more.Items.Count} more");
        Check(more.Items.Count > 0, "queue continuation returns items");
    }
}

async Task LyricsFor(string videoId)
{
    Console.WriteLine($"== WATCH/LYRICS {videoId}");
    var w = await api.GetWatchInfoAsync(videoId);
    Console.WriteLine($"  lyrics={w.LyricsBrowseId} related={w.RelatedBrowseId} like={w.LikeStatus} track={w.Track}");
    Check(w.LyricsBrowseId is not null, "watch info has lyrics tab");
    if (w.LyricsBrowseId is not null)
    {
        var l = await api.GetLyricsAsync(w.LyricsBrowseId);
        Console.WriteLine($"  {l?.Source}: {l?.PlainText?.Split('\n').FirstOrDefault()}");
        Check(l?.PlainText is not null, "lyrics text returned");
    }
}

void Hosts()
{
    Console.WriteLine("== SIGN-IN HOST ALLOWLIST");
    foreach (var h in new[] { "accounts.google.com", "accounts.google.co.uk", "accounts.google.de", "accounts.google.com.au", "ACCOUNTS.GOOGLE.FR" })
        Check(Tapedeck.Services.GoogleDomains.IsAccountsHost(h), $"Google sign-in host trusted: {h}");
    foreach (var h in new[] { "accounts.google.co.uk.evil.com", "accounts.google.evil", "accounts.googIe.com", "accounts.google.com.evil.co",
                              "evil.accounts.google.com.example", "accounts-google.com", "accounts.google", "xaccounts.google.com", "mail.google.com" })
        Check(!Tapedeck.Services.GoogleDomains.IsAccountsHost(h), $"look-alike rejected: {h}");
}

void DiscordOffline()
{
    Console.WriteLine("== DISCORD ACTIVITY (offline)");
    Check(DiscordActivity.Field("A") is { Length: 2 } a && a[0] == 'A', "one-character text is padded to Discord's 2-character minimum");
    Check(DiscordActivity.Field(new string('x', 300)).Length <= 128, "long text is cut to 128 characters");
    var cut = DiscordActivity.Field(string.Concat(Enumerable.Repeat("\U0001F3B5", 100)));
    Check(cut.Length <= 128 && !char.IsHighSurrogate(cut[^2]), "cutting never splits an emoji in half");
    Check(DiscordActivity.FromItem(new MusicItem { Title = "x", VideoId = "not a video id" }, 100) is null, "invalid video id is not shared");
    Check(DiscordActivity.FromItem(new MusicItem { Title = " ", VideoId = "dQw4w9WgXcQ" }, 100) is null, "track without a title is not shared");

    var channel = "UC" + new string('A', 22);
    var item = new MusicItem
    {
        Title = "Line one\nline\ttwo",
        VideoId = "dQw4w9WgXcQ",
        Artists = [new Link("Some Artist", channel), new Link("Guest", null)],
        Album = new Link("An Album", "MPREb_abcdefghijk"),
        ThumbnailUrl = "https://lh3.googleusercontent.com/abc=w60-h60-l90-rj",
    };
    var track = DiscordActivity.FromItem(item, 212.6)!;
    Check(track.Title == "Line one line two", $"titles are flattened to one line ({track.Title})");
    Check(track.Artist == "Some Artist, Guest" && track.ArtistId == channel, "artists joined, first channel linked");
    Check(track.ArtUrl == "https://lh3.googleusercontent.com/abc=w512-h512-l90-rj", $"artwork asked for at 512px ({track.ArtUrl})");
    var start = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    var act = DiscordActivity.Build(track, start, DiscordStatusDisplay.Song);
    Console.WriteLine("  " + act.ToJsonString());
    Check((int)act["type"]! == 2, "type is Listening");
    Check((int)act["status_display_type"]! == 2, "status shows the song");
    Check((long)act["timestamps"]!["end"]! - (long)act["timestamps"]!["start"]! == 213_000, "progress bar spans the track length");
    Check((string?)act["state_url"] == "https://music.youtube.com/channel/" + channel, "artist line links to the artist");
    Check((string?)act["assets"]!["large_url"] == "https://music.youtube.com/browse/MPREb_abcdefghijk", "artwork links to the album");
    var buttons = act["buttons"]!.AsArray();
    Check(buttons.Count == 2 && buttons.All(b => ((string)b!["label"]!).Length <= 32 && ((string)b["url"]!).StartsWith("https://")), "two buttons within Discord's limits");

    var lone = DiscordActivity.FromItem(new MusicItem { Title = "Untitled", VideoId = "dQw4w9WgXcQ" }, 0)!;
    var loneAct = DiscordActivity.Build(lone, start, DiscordStatusDisplay.Artist);
    Check((int)loneAct["status_display_type"]! == 0, "status falls back to the app name when there is no artist");
    Check(loneAct["timestamps"]!["end"] is null, "no end time (so no bar) when the length is unknown");
    Check(DiscordActivity.ArtworkUrl("https://evil.example/x.png", "dQw4w9WgXcQ") == "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", "artwork from other hosts is replaced");
    Check(DiscordActivity.ArtworkUrl("https://lh3.googleusercontent.com/" + new string('a', 300), "dQw4w9WgXcQ")!.StartsWith("https://i.ytimg.com/"), "over-long artwork URLs are replaced");
}

// Sends a real card to the Discord app on this PC (needs TAPEDECK_DISCORD_APP_ID or the built-in id).
async Task DiscordLive(string videoId, int seconds)
{
    Console.WriteLine($"== DISCORD LIVE ({videoId})");
    var info = await api.GetWatchInfoAsync(videoId);
    Check(info.Track is not null, "watch info has the track");
    if (info.Track is null) return;
    var track = DiscordActivity.FromItem(info.Track, info.Track.DurationSeconds);
    Check(track is not null, $"track can be shared: {track}");
    if (track is null) return;
    var activity = DiscordActivity.Build(track, DateTimeOffset.UtcNow, DiscordStatusDisplay.App);
    Console.WriteLine("  " + activity.ToJsonString());

    var appId = Environment.GetEnvironmentVariable("TAPEDECK_DISCORD_APP_ID") is { Length: > 0 } id ? id : DiscordActivity.ApplicationId;
    if (appId.Length == 0)
    {
        Console.WriteLine("  (no Discord application id: set TAPEDECK_DISCORD_APP_ID to send it to Discord)");
        return;
    }
    await using var client = new DiscordIpcClient();
    var replies = new List<JsonNode>();
    var gotReply = new TaskCompletionSource();
    client.Reply += r => { replies.Add(r); gotReply.TrySetResult(); };
    try
    {
        await client.ConnectAsync(appId, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Check(false, $"connect to Discord: {ex.GetType().Name}: {ex.Message}");
        return;
    }
    Check(client.IsConnected, $"connected to Discord as {client.UserName}");
    await client.SetActivityAsync(activity, CancellationToken.None);
    await Task.WhenAny(gotReply.Task, Task.Delay(5000));
    var reply = replies.FirstOrDefault();
    Console.WriteLine($"  reply: {reply?.ToJsonString()}");
    Check(reply is not null && (string?)reply["evt"] != "ERROR", "Discord accepted the activity");
    if (seconds > 0)
    {
        Console.WriteLine($"  showing it on your profile for {seconds}s…");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }
    await client.SetActivityAsync(null, CancellationToken.None);
    await Task.Delay(500);
}

try
{
    switch (cmd)
    {
        case "hosts": Hosts(); break;
        case "discord":
            DiscordOffline();
            var dargs = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            await DiscordLive(dargs.ElementAtOrDefault(0) ?? "kJQP7kiw5Fk", int.TryParse(dargs.ElementAtOrDefault(1), out var secs) ? secs : 0);
            break;
        case "home": await Home(); break;
        case "explore": await Explore(); break;
        case "search": await Search(arg ?? "daft punk"); break;
        case "album": case "playlist": await Collection(arg!); break;
        case "artist": await Artist(arg!); break;
        case "queue": await Queue(arg!); break;
        case "lyrics": await LyricsFor(arg!); break;
        default:
            Hosts();
            DiscordOffline();
            await Home();
            await Explore();
            var r = await Search("daft punk");
            var artist = r.Shelves.SelectMany(s => s.Items).Prepend(r.TopResult).FirstOrDefault(i => i?.Kind == ItemKind.Artist);
            await Artist(artist?.BrowseId ?? "UC_kRDKYrUlrbtrSiyu5Tflg");
            var album = r.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Album);
            if (album?.BrowseId is not null) await Collection(album.BrowseId);
            var home = await api.GetShelvesAsync("FEmusic_home");
            var playlist = home.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Playlist);
            if (playlist?.BrowseId is not null) await Collection(playlist.BrowseId);
            var song = r.Shelves.SelectMany(s => s.Items).FirstOrDefault(i => i.Kind == ItemKind.Song);
            await Queue(song?.VideoId ?? "5NV6Rdv1a3I");
            await LyricsFor("kJQP7kiw5Fk"); // a track known to have lyrics (search hits can be instrumentals)
            break;
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(ex);
    Console.ResetColor();
    return 2;
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
