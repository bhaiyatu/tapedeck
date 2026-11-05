using System.Text.Json.Nodes;
using Tapedeck.Models;

namespace Tapedeck.Services.Api;

/// <summary>Parses whole InnerTube responses (browse / search / next) into page models.</summary>
internal static class PageParser
{
    // ───────────────────────── Shelves (home, explore, moods, library) ─────────────────────────

    public static ShelvesPageData ParseShelvesPage(JsonNode root)
    {
        var page = new ShelvesPageData();
        var tab = root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer");
        var sectionList = tab.At("content", "sectionListRenderer") ?? root.FindFirst("sectionListRenderer");
        page.Title = root.At("header").FindFirst("title").Text() is { Length: > 0 } t ? t : null;

        foreach (var section in sectionList.Arr("contents"))
            page.Shelves.AddRange(ParseShelves(section));

        foreach (var chip in sectionList.Arr("header", "chipCloudRenderer", "chips"))
        {
            var c = chip.At("chipCloudChipRenderer");
            var browse = c.At("navigationEndpoint", "browseEndpoint");
            page.Chips.Add(new Chip
            {
                Text = c.At("text").Text(),
                BrowseId = browse.Str("browseId"),
                Params = browse.Str("params"),
                IsSelected = c.Str("isSelected") == "true",
            });
        }

        page.Continuation = ContinuationToken(sectionList);
        return page;
    }

    public static ShelvesPageData ParseShelvesContinuation(JsonNode root)
    {
        var page = new ShelvesPageData();
        var cont = root.At("continuationContents", "sectionListContinuation");
        foreach (var section in cont.Arr("contents"))
            page.Shelves.AddRange(ParseShelves(section));
        page.Continuation = ContinuationToken(cont);
        return page;
    }

    /// <summary>Old-style "nextContinuationData" token on a renderer, if any.</summary>
    public static string? ContinuationToken(JsonNode? renderer) =>
        renderer.Str("continuations", 0, "nextContinuationData", "continuation")
        ?? renderer.Str("continuations", 0, "nextRadioContinuationData", "continuation");

    /// <summary>A section can be a shelf, or a wrapper holding several shelves.</summary>
    public static IEnumerable<Shelf> ParseShelves(JsonNode? section)
    {
        var (key, r) = section.Single();
        switch (key)
        {
            case "itemSectionRenderer":
                foreach (var inner in r.Arr("contents"))
                    foreach (var s in ParseShelves(inner)) yield return s;
                yield break;
            case "musicCarouselShelfRenderer":
            case "musicImmersiveCarouselShelfRenderer":
            {
                var header = r.At("header").Single().Value;
                var items = ItemParser.ParseMany(r.Arr("contents"));
                if (items.Count == 0) yield break;
                var more = header.At("moreContentButton", "buttonRenderer", "navigationEndpoint", "browseEndpoint");
                yield return new Shelf
                {
                    Title = header.At("title").Text(),
                    Strapline = header.At("strapline").Text() is { Length: > 0 } s ? s : null,
                    Style = StyleFor(items, r.Arr("contents")),
                    Items = items,
                    MoreBrowseId = more.Str("browseId"),
                    MoreParams = more.Str("params"),
                };
                yield break;
            }
            case "musicShelfRenderer":
            case "musicPlaylistShelfRenderer":
            {
                var items = ItemParser.ParseMany(r.Arr("contents"));
                if (items.Count == 0) yield break;
                var bottom = r.At("bottomEndpoint");
                yield return new Shelf
                {
                    Title = r.At("title").Text(),
                    Style = ShelfStyle.List,
                    Items = items,
                    MoreBrowseId = bottom.Str("browseEndpoint", "browseId"),
                    MoreParams = bottom.Str("browseEndpoint", "params"),
                    MoreSearchParams = bottom.Str("searchEndpoint", "params"),
                };
                yield break;
            }
            case "gridRenderer":
            {
                var items = ItemParser.ParseMany(r.Arr("items"));
                if (items.Count == 0) yield break;
                var style = StyleFor(items, r.Arr("items"));
                yield return new Shelf
                {
                    Title = r.At("header", "gridHeaderRenderer", "title").Text(),
                    Style = style == ShelfStyle.Cards ? ShelfStyle.Grid : style,
                    Items = items,
                };
                yield break;
            }
            case "musicDescriptionShelfRenderer":
            {
                var text = r.At("description").Text();
                if (text.Length == 0) yield break;
                yield return new Shelf
                {
                    Title = r.At("header").Text() is { Length: > 0 } h ? h : "About",
                    Style = ShelfStyle.Text,
                    Text = text,
                };
                yield break;
            }
            case "musicCardShelfRenderer":
            {
                // Search "top result" card: surface it as a one-item card shelf plus its tracks.
                var top = ParseTopResult(r);
                if (top is not null)
                    yield return new Shelf { Title = "Top result", Style = ShelfStyle.Cards, Items = [top] };
                var related = ItemParser.ParseMany(r.Arr("contents"));
                // Songs under an artist card omit the artist; it's the card itself.
                if (top?.Kind == ItemKind.Artist)
                    foreach (var song in related.Where(s => s.Artists.Count == 0))
                    {
                        song.Artists.Add(new Link(top.Title, top.BrowseId));
                        song.Subtitle = string.IsNullOrEmpty(song.Subtitle) ? top.Title : $"{top.Title} • {song.Subtitle}";
                    }
                if (related.Count > 0)
                    yield return new Shelf { Title = "", Style = ShelfStyle.List, Items = related };
                yield break;
            }
        }
    }

    private static ShelfStyle StyleFor(List<MusicItem> items, JsonArray raw)
    {
        var firstKey = raw.FirstOrDefault().Single().Key;
        if (firstKey == "musicNavigationButtonRenderer") return ShelfStyle.Chips;
        if (firstKey == "musicResponsiveListItemRenderer") return ShelfStyle.List;
        return ShelfStyle.Cards;
    }

    public static MusicItem? ParseTopResult(JsonNode? r)
    {
        if (r is null) return null;
        var titleRun = r.At("title").Runs().FirstOrDefault();
        var nav = titleRun.At("navigationEndpoint") ?? r.At("onTap");
        var item = new MusicItem
        {
            Title = r.At("title").Text(),
            ThumbnailUrl = r.At("thumbnail").Thumbnail(),
        };
        // "Song • Daft Punk • 5:21" → subtitle "Daft Punk", duration "5:21".
        var segments = r.At("subtitle").Text().Split(" • ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        item.DurationText = segments.FirstOrDefault(s => Formatting.DurationRegex().IsMatch(s));
        if (segments.Count > 0 && segments[0] is "Song" or "Video") item.TypeLabel = segments[0];
        item.Subtitle = string.Join(" • ", segments.Where(s => s != "Song" && !Formatting.DurationRegex().IsMatch(s)));
        var browse = nav.At("browseEndpoint");
        var watch = nav.At("watchEndpoint");
        if (browse is not null)
        {
            item.Kind = ItemParser.KindFromPageType(browse.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType"));
            item.BrowseId = ItemParser.NormalizeBrowseId(browse.Str("browseId"));
        }
        else if (watch is not null)
        {
            item.VideoId = watch.Str("videoId");
            item.Kind = ItemParser.KindFromVideoType(watch.Str("watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig", "musicVideoType"));
            item.PlayEndpoint = nav.CloneObj();
        }
        foreach (var run in r.At("subtitle").Runs())
        {
            var b = run.At("navigationEndpoint", "browseEndpoint");
            var pt = b.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
            if (ItemParser.KindFromPageType(pt) == ItemKind.Artist)
                item.Artists.Add(new Link(run.Str("text") ?? "", b.Str("browseId")));
            else if (ItemParser.KindFromPageType(pt) == ItemKind.Album)
                item.Album = new Link(run.Str("text") ?? "", b.Str("browseId"));
        }
        // Prefer the card's own play button (radio for artists, album playlist for albums).
        var play = r.Arr("buttons")
            .Select(b => b.At("buttonRenderer", "command"))
            .FirstOrDefault(c => c?.At("watchPlaylistEndpoint") is not null || c?.At("watchEndpoint") is not null);
        item.PlayEndpoint ??= play.CloneObj();
        return item.Title.Length == 0 ? null : item;
    }

    // ───────────────────────── Search ─────────────────────────

    public static SearchResults ParseSearch(JsonNode root)
    {
        var results = new SearchResults();
        var tabs = root.Arr("contents", "tabbedSearchResultsRenderer", "tabs");
        var sectionList = tabs.FirstOrDefault().At("tabRenderer", "content", "sectionListRenderer") ?? root.FindFirst("sectionListRenderer");

        // The current layout is a flat, mixed list where every result sits in its own
        // itemSectionRenderer. Collect those and group them by type below.
        var loose = new List<MusicItem>();
        foreach (var section in sectionList.Arr("contents"))
        {
            if (section.At("musicCardShelfRenderer") is { } card)
                results.TopResult ??= ParseTopResult(card);
            if (section.At("itemSectionRenderer") is { } isr)
            {
                foreach (var inner in isr.Arr("contents"))
                {
                    if (ItemParser.Parse(inner) is { } item) loose.Add(item);
                    else results.Shelves.AddRange(ParseShelves(inner));
                }
                continue;
            }
            results.Shelves.AddRange(ParseShelves(section));
            results.Continuation ??= ContinuationToken(section.At("musicShelfRenderer"));
        }

        foreach (var group in loose.GroupBy(GroupName))
            results.Shelves.Add(new Shelf { Title = group.Key, Style = ShelfStyle.List, Items = group.ToList() });

        foreach (var chip in sectionList.Arr("header", "chipCloudRenderer", "chips"))
        {
            var c = chip.At("chipCloudChipRenderer");
            results.Filters.Add(new Chip
            {
                Text = c.At("text").Text(),
                Params = c.Str("navigationEndpoint", "searchEndpoint", "params"),
                IsSelected = c.Str("isSelected") == "true",
            });
        }
        return results;
    }

    private static string GroupName(MusicItem item) => item.TypeLabel switch
    {
        "Profile" => "Profiles",
        "Single" or "EP" => "Albums",
        _ => item.Kind switch
        {
            ItemKind.Song => "Songs",
            ItemKind.Video => "Videos",
            ItemKind.Album => "Albums",
            ItemKind.Artist => "Artists",
            ItemKind.Playlist => "Playlists",
            ItemKind.Episode => "Episodes",
            ItemKind.Podcast => "Podcasts",
            _ => "More results",
        },
    };

    public static SearchResults ParseSearchContinuation(JsonNode root)
    {
        var cont = root.At("continuationContents", "musicShelfContinuation");
        return new SearchResults
        {
            Shelves = [new Shelf { Style = ShelfStyle.List, Items = ItemParser.ParseMany(cont.Arr("contents")) }],
            Continuation = ContinuationToken(cont),
        };
    }

    public static List<string> ParseSuggestions(JsonNode root) =>
        root.Arr("contents")
            .SelectMany(s => s.Arr("searchSuggestionsSectionRenderer", "contents"))
            .Select(c => c.Str("searchSuggestionRenderer", "navigationEndpoint", "searchEndpoint", "query"))
            .OfType<string>()
            .ToList();

    // ───────────────────────── Album / playlist ─────────────────────────

    public static CollectionDetails ParseCollection(JsonNode root, string browseId)
    {
        var details = new CollectionDetails { IsAlbum = browseId.StartsWith("MPRE", StringComparison.Ordinal) };
        var header = root.FindFirst("musicResponsiveHeaderRenderer")
                     ?? root.FindFirst("musicDetailHeaderRenderer")
                     ?? root.FindFirst("musicImmersiveHeaderRenderer");
        details.Title = header.At("title").Text();
        details.Subtitle = header.At("subtitle").Text();
        details.SecondSubtitle = header.At("secondSubtitle").Text();
        details.ThumbnailUrl = header.At("thumbnail").Thumbnail();
        details.Description = header.At("description", "musicDescriptionShelfRenderer", "description").Text() is { Length: > 0 } d ? d : null;

        foreach (var run in header.At("straplineTextOne").Runs())
        {
            var id = run.Str("navigationEndpoint", "browseEndpoint", "browseId");
            details.Artists.Add(new Link(run.Str("text") ?? "", ItemParser.NormalizeBrowseId(id)));
        }

        var secondary = root.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer");
        var sections = secondary is not null
            ? secondary.Arr("contents")
            : root.FindFirst("sectionListRenderer").Arr("contents");

        var first = true;
        foreach (var section in sections)
        {
            var (key, shelf) = section.Single();
            if (first && key is "musicShelfRenderer" or "musicPlaylistShelfRenderer")
            {
                first = false;
                var contents = shelf.Arr("contents");
                details.Tracks = ItemParser.ParseMany(contents).Where(t => t.VideoId is not null).ToList();
                details.PlaylistId = shelf.Str("playlistId");
                details.Continuation = NewStyleContinuation(contents) ?? ContinuationToken(shelf);
                continue;
            }
            details.Related.AddRange(ParseShelves(section));
        }

        // Album tracks rarely carry their own artist links; inherit the album artist.
        foreach (var t in details.Tracks)
        {
            if (t.Artists.Count == 0) t.Artists.AddRange(details.Artists);
            if (details.IsAlbum)
            {
                t.Album ??= new Link(details.Title, browseId);
                t.ThumbnailUrl ??= details.ThumbnailUrl;
            }
            if (string.IsNullOrEmpty(t.Subtitle)) t.Subtitle = t.ArtistText;
        }

        details.PlaylistId ??= details.Tracks.Select(t => t.PlaylistId).FirstOrDefault(p => p is not null);
        if (details.PlaylistId is null && browseId.StartsWith("VL", StringComparison.Ordinal)) details.PlaylistId = browseId[2..];
        return details;
    }

    /// <summary>New-style continuation: a trailing continuationItemRenderer in a contents list.</summary>
    public static string? NewStyleContinuation(JsonArray contents) =>
        contents.LastOrDefault().Str("continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token");

    public static (List<MusicItem> Items, string? Continuation) ParseCollectionContinuation(JsonNode root)
    {
        // New style: onResponseReceivedActions[].appendContinuationItemsAction.continuationItems
        var appended = root.Arr("onResponseReceivedActions")
            .Select(a => a.At("appendContinuationItemsAction", "continuationItems") as JsonArray)
            .FirstOrDefault(a => a is not null);
        if (appended is not null)
            return (ItemParser.ParseMany(appended).Where(t => t.VideoId is not null).ToList(), NewStyleContinuation(appended));

        var cont = root.At("continuationContents", "musicPlaylistShelfContinuation")
                   ?? root.At("continuationContents", "musicShelfContinuation");
        return (ItemParser.ParseMany(cont.Arr("contents")).Where(t => t.VideoId is not null).ToList(), ContinuationToken(cont));
    }

    // ───────────────────────── Artist ─────────────────────────

    public static ArtistDetails ParseArtist(JsonNode root)
    {
        var header = root.At("header").Single().Value;
        var artist = new ArtistDetails
        {
            Name = header.At("title").Text(),
            Description = header.At("description").Text() is { Length: > 0 } d ? d : null,
            ThumbnailUrl = header.At("thumbnail").Thumbnail() ?? header.At("foregroundThumbnail").Thumbnail(),
            AudienceText = header.At("monthlyListenerCount").Text() is { Length: > 0 } m ? m : null,
            ShuffleEndpoint = header.At("playButton", "buttonRenderer", "navigationEndpoint").CloneObj(),
            RadioEndpoint = header.At("startRadioButton", "buttonRenderer", "navigationEndpoint").CloneObj(),
        };

        var sectionList = root.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer");
        foreach (var section in sectionList.Arr("contents"))
        {
            foreach (var shelf in ParseShelves(section))
            {
                // The bio duplicates the header description.
                if (shelf.Style == ShelfStyle.Text && artist.Description is not null) continue;
                artist.Shelves.Add(shelf);
            }
        }
        return artist;
    }

    // ───────────────────────── Queue / watch ─────────────────────────

    public static QueuePage ParseQueue(JsonNode root)
    {
        var page = new QueuePage();
        var panel = root.FindFirst("playlistPanelRenderer");
        if (panel is null) return page;
        page.PlaylistId = panel.Str("playlistId");

        var index = 0;
        foreach (var entry in panel.Arr("contents"))
        {
            var (key, r) = entry.Single();
            var inner = key == "playlistPanelVideoWrapperRenderer" ? r.At("primaryRenderer", "playlistPanelVideoRenderer") : r;
            if (key is not ("playlistPanelVideoRenderer" or "playlistPanelVideoWrapperRenderer")) continue;
            var item = ItemParser.ParseQueueItem(inner);
            if (item is null) continue;
            if (inner.Str("selected") == "true") page.SelectedIndex = index;
            page.Items.Add(item);
            index++;
        }

        page.IsRadioContinuation = panel.At("continuations", 0, "nextRadioContinuationData") is not null;
        page.Continuation = ContinuationToken(panel);
        return page;
    }

    public static QueuePage ParseQueueContinuation(JsonNode root)
    {
        var panel = root.At("continuationContents", "playlistPanelContinuation");
        var page = new QueuePage { PlaylistId = panel.Str("playlistId") };
        foreach (var entry in panel.Arr("contents"))
        {
            var (key, r) = entry.Single();
            var inner = key == "playlistPanelVideoWrapperRenderer" ? r.At("primaryRenderer", "playlistPanelVideoRenderer") : r;
            if (ItemParser.ParseQueueItem(inner) is { } item) page.Items.Add(item);
        }
        page.IsRadioContinuation = panel.At("continuations", 0, "nextRadioContinuationData") is not null;
        page.Continuation = ContinuationToken(panel);
        return page;
    }

    public static WatchInfo ParseWatchInfo(JsonNode root)
    {
        var info = new WatchInfo();
        var tabs = root.Arr("contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer", "tabs");
        foreach (var tab in tabs)
        {
            var id = tab.Str("tabRenderer", "endpoint", "browseEndpoint", "browseId");
            if (id is null || tab.Str("tabRenderer", "unselectable") == "true") continue;
            if (id.StartsWith("MPLY", StringComparison.Ordinal)) info.LyricsBrowseId = id;
            else if (id.StartsWith("MPTR", StringComparison.Ordinal)) info.RelatedBrowseId = id;
        }
        info.LikeStatus = root.At("playerOverlays").FindFirst("likeButtonRenderer").Str("likeStatus");
        var queue = ParseQueue(root);
        if (queue.Items.Count > 0) info.Track = queue.Items[Math.Clamp(queue.SelectedIndex, 0, queue.Items.Count - 1)];
        return info;
    }

    public static Lyrics? ParseLyrics(JsonNode root)
    {
        var shelf = root.FindFirst("musicDescriptionShelfRenderer");
        var text = shelf.At("description").Text();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new Lyrics { PlainText = text, Source = shelf.At("footer").Text() is { Length: > 0 } f ? f : "YouTube Music" };
    }

    public static AccountInfo? ParseAccount(JsonNode root)
    {
        var header = root.FindFirst("activeAccountHeaderRenderer");
        if (header is null) return null;
        return new AccountInfo
        {
            Name = header.At("accountName").Text(),
            Handle = header.At("channelHandle").Text() is { Length: > 0 } h ? h : null,
            PhotoUrl = header.At("accountPhoto").Thumbnail(),
        };
    }
}
