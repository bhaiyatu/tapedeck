// Usage: dotnet run -- [home|explore|search <q>|album <id>|playlist <id>|artist <id>|queue <videoId>|lyrics <videoId>|all]
using Tapedeck.Models;
using Tapedeck.Services.Api;

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

try
{
    switch (cmd)
    {
        case "home": await Home(); break;
        case "explore": await Explore(); break;
        case "search": await Search(arg ?? "daft punk"); break;
        case "album": case "playlist": await Collection(arg!); break;
        case "artist": await Artist(arg!); break;
        case "queue": await Queue(arg!); break;
        case "lyrics": await LyricsFor(arg!); break;
        default:
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
