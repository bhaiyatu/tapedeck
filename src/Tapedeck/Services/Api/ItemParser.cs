using System.Text.Json.Nodes;
using Tapedeck.Models;

namespace Tapedeck.Services.Api;

/// <summary>Turns the different InnerTube item renderers into <see cref="MusicItem"/>s.</summary>
internal static partial class ItemParser
{
    /// <summary>Matches play counts and similar stats ("10M plays", "35 songs", "1d ago").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[\d.,]+\s*[KMB]?\s+(plays?|views?|likes?|subscribers?|monthly audience|monthly listeners|songs?|tracks?|episodes?|\w+ ago)$|\bago$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StatRegex();

    private static readonly HashSet<string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Song", "Video", "Album", "Single", "EP", "Artist", "Playlist", "Episode", "Podcast", "Profile", "Station", "Audiobook",
    };

    /// <summary>Parses a wrapper object such as {"musicTwoRowItemRenderer": {...}}.</summary>
    public static MusicItem? Parse(JsonNode? wrapper)
    {
        var (key, r) = wrapper.Single();
        if (r is null) return null;
        return key switch
        {
            "musicResponsiveListItemRenderer" => ParseListItem(r),
            "musicTwoRowItemRenderer" => ParseTwoRow(r),
            "playlistPanelVideoRenderer" => ParseQueueItem(r),
            "playlistPanelVideoWrapperRenderer" => ParseQueueItem(r.At("primaryRenderer", "playlistPanelVideoRenderer")),
            "musicNavigationButtonRenderer" => ParseNavButton(r),
            "musicMultiRowListItemRenderer" => ParseMultiRow(r),
            _ => null,
        };
    }

    public static List<MusicItem> ParseMany(JsonArray items)
    {
        var list = new List<MusicItem>(items.Count);
        foreach (var it in items)
        {
            // Skip tiles that lead nowhere we support (e.g. Library's "New playlist" button).
            if (Parse(it) is { } m && (m.VideoId is not null || m.BrowseId is not null || m.PlayEndpoint is not null))
                list.Add(m);
        }
        return list;
    }

    public static ItemKind KindFromPageType(string? pageType) => pageType switch
    {
        "MUSIC_PAGE_TYPE_ALBUM" or "MUSIC_PAGE_TYPE_AUDIOBOOK" => ItemKind.Album,
        "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" or "MUSIC_PAGE_TYPE_LIBRARY_ARTIST" => ItemKind.Artist,
        "MUSIC_PAGE_TYPE_PLAYLIST" => ItemKind.Playlist,
        "MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE" => ItemKind.Podcast,
        "MUSIC_PAGE_TYPE_NON_MUSIC_AUDIO_TRACK_PAGE" => ItemKind.Episode,
        _ => ItemKind.Unknown,
    };

    public static ItemKind KindFromVideoType(string? videoType) => videoType switch
    {
        null or "MUSIC_VIDEO_TYPE_ATV" => ItemKind.Song,
        "MUSIC_VIDEO_TYPE_PODCAST_EPISODE" => ItemKind.Episode,
        _ => ItemKind.Video,
    };

    /// <summary>Library artist pages use "MPLAUC…" ids; the artist page itself is "UC…".</summary>
    public static string? NormalizeBrowseId(string? id) =>
        id is not null && id.StartsWith("MPLA", StringComparison.Ordinal) ? id[4..] : id;

    private static string? PageType(JsonNode? browseEndpoint) =>
        browseEndpoint.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");

    private static string? VideoType(JsonNode? watchEndpoint) =>
        watchEndpoint.Str("watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig", "musicVideoType");

    private static JsonObject? OverlayPlayEndpoint(JsonNode? r) =>
        (r.At("overlay") ?? r.At("thumbnailOverlay"))
            .At("musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint")
            .CloneObj();

    private static bool HasExplicitBadge(JsonNode? r) =>
        r.Arr("badges").Concat(r.Arr("subtitleBadges"))
            .Any(b => b.Str("musicInlineBadgeRenderer", "icon", "iconType") == "MUSIC_EXPLICIT_BADGE");

    private static string? PlaylistIdOf(JsonObject? endpoint) =>
        endpoint.Str("watchPlaylistEndpoint", "playlistId") ?? endpoint.Str("watchEndpoint", "playlistId");

    /// <summary>
    /// Reads artist / album links and the duration out of a run list, and returns a display
    /// subtitle built from the " • "-separated segments.
    /// </summary>
    private static string ApplyRuns(MusicItem item, IEnumerable<JsonNode?> runs, bool dropSongLabel)
    {
        var segments = new List<string>();
        var current = "";
        string? firstSegment = null;
        foreach (var run in runs)
        {
            var text = run.Str("text") ?? "";
            if (text.Trim() == "•")
            {
                Flush();
                continue;
            }

            var browse = run.At("navigationEndpoint", "browseEndpoint");
            if (browse is not null)
            {
                var kind = KindFromPageType(PageType(browse));
                var id = NormalizeBrowseId(browse.Str("browseId"));
                if (kind == ItemKind.Artist) item.Artists.Add(new Link(text, id));
                else if (kind == ItemKind.Album) item.Album = new Link(text, id);
            }
            current += text;
        }
        Flush();

        if (firstSegment is not null && TypeLabels.Contains(firstSegment)) item.TypeLabel = firstSegment;
        if (firstSegment is not null && TypeLabels.Contains(firstSegment) && item.Kind == ItemKind.Unknown)
            item.Kind = firstSegment switch
            {
                "Song" => ItemKind.Song,
                "Video" => ItemKind.Video,
                "Artist" or "Profile" => ItemKind.Artist,
                "Playlist" or "Station" => ItemKind.Playlist,
                "Episode" => ItemKind.Episode,
                "Podcast" => ItemKind.Podcast,
                _ => ItemKind.Album,
            };

        // Songs whose artist has no channel link (e.g. "Various Artists") still deserve an artist name.
        if (item.Artists.Count == 0 && item.Kind is ItemKind.Song or ItemKind.Video)
        {
            var candidate = segments.FirstOrDefault(s =>
                !TypeLabels.Contains(s) && !Formatting.DurationRegex().IsMatch(s) && !StatRegex().IsMatch(s));
            if (candidate is not null && candidate != item.Album?.Name) item.Artists.Add(new Link(candidate, null));
        }

        var shown = segments.Where(s =>
            !Formatting.DurationRegex().IsMatch(s) &&
            !(dropSongLabel && s.Equals("Song", StringComparison.OrdinalIgnoreCase)));
        return string.Join(" • ", shown);

        void Flush()
        {
            var seg = current.Trim();
            current = "";
            if (seg.Length == 0) return;
            firstSegment ??= seg;
            if (Formatting.DurationRegex().IsMatch(seg)) item.DurationText ??= seg;
            segments.Add(seg);
        }
    }

    private static MusicItem ParseListItem(JsonNode r)
    {
        var item = new MusicItem();
        var columns = r.Arr("flexColumns")
            .Select(c => c.At("musicResponsiveListItemFlexColumnRenderer", "text"))
            .ToList();

        var titleRun = columns.ElementAtOrDefault(0).Runs().FirstOrDefault();
        item.Title = columns.ElementAtOrDefault(0).Text();
        item.ThumbnailUrl = r.At("thumbnail").Thumbnail();
        item.Index = r.At("index").Text() is { Length: > 0 } idx ? idx : null;
        item.IsExplicit = HasExplicitBadge(r);
        item.PlayEndpoint = OverlayPlayEndpoint(r);

        var titleWatch = titleRun.At("navigationEndpoint", "watchEndpoint");
        var overlayWatch = item.PlayEndpoint.At("watchEndpoint");
        item.VideoId = r.Str("playlistItemData", "videoId") ?? titleWatch.Str("videoId") ?? overlayWatch.Str("videoId");

        var browse = r.At("navigationEndpoint", "browseEndpoint") ?? titleRun.At("navigationEndpoint", "browseEndpoint");
        if (browse is not null)
        {
            item.Kind = KindFromPageType(PageType(browse));
            item.BrowseId = NormalizeBrowseId(browse.Str("browseId"));
            item.BrowseParams = browse.Str("params");
        }
        else if (item.VideoId is not null)
        {
            item.Kind = KindFromVideoType(VideoType(titleWatch ?? overlayWatch));
        }

        // Subtitle: every column after the title, joined with separators.
        var runs = new List<JsonNode?>();
        for (var i = 1; i < columns.Count; i++)
        {
            var colRuns = columns[i].Runs();
            if (colRuns.Count == 0) continue;
            if (runs.Count > 0) runs.Add(JsonNode.Parse("{\"text\":\" • \"}"));
            runs.AddRange(colRuns);
        }
        item.Subtitle = ApplyRuns(item, runs, dropSongLabel: true);

        var fixedText = r.Arr("fixedColumns").FirstOrDefault().At("musicResponsiveListItemFixedColumnRenderer", "text").Text();
        if (Formatting.DurationRegex().IsMatch(fixedText)) item.DurationText = fixedText;

        if (item.Kind == ItemKind.Unknown && item.VideoId is not null) item.Kind = ItemKind.Song;
        item.PlaylistId = PlaylistIdOf(item.PlayEndpoint) ?? titleWatch.Str("playlistId");
        item.IsWide = item.Kind == ItemKind.Video;
        return item;
    }

    private static MusicItem ParseTwoRow(JsonNode r)
    {
        var item = new MusicItem
        {
            Title = r.At("title").Text(),
            ThumbnailUrl = r.At("thumbnailRenderer").Thumbnail(),
            PlayEndpoint = OverlayPlayEndpoint(r),
            IsExplicit = HasExplicitBadge(r),
        };
        var nav = r.At("navigationEndpoint");
        var browse = nav.At("browseEndpoint");
        var watch = nav.At("watchEndpoint");
        if (browse is not null)
        {
            item.Kind = KindFromPageType(PageType(browse));
            item.BrowseId = NormalizeBrowseId(browse.Str("browseId"));
            item.BrowseParams = browse.Str("params");
        }
        else if (watch is not null)
        {
            item.VideoId = watch.Str("videoId");
            item.Kind = KindFromVideoType(VideoType(watch));
            item.PlayEndpoint ??= nav.CloneObj();
        }
        else if (nav.At("watchPlaylistEndpoint") is not null)
        {
            item.Kind = ItemKind.Playlist;
            item.PlayEndpoint ??= nav.CloneObj();
        }

        item.Subtitle = ApplyRuns(item, r.At("subtitle").Runs(), dropSongLabel: false);
        item.PlaylistId = PlaylistIdOf(item.PlayEndpoint);
        item.IsWide = (r.Str("aspectRatio") ?? "").Contains("16_9");
        // Episodes open a details page but are played straight from their play button.
        if (item.Kind == ItemKind.Episode) item.VideoId ??= item.PlayEndpoint.Str("watchEndpoint", "videoId");
        return item;
    }

    public static MusicItem? ParseQueueItem(JsonNode? r)
    {
        if (r is null) return null;
        var nav = r.At("navigationEndpoint");
        var watch = nav.At("watchEndpoint");
        var videoId = r.Str("videoId") ?? watch.Str("videoId");
        if (videoId is null) return null;

        var item = new MusicItem
        {
            VideoId = videoId,
            Title = r.At("title").Text(),
            ThumbnailUrl = r.At("thumbnail").Thumbnail(),
            DurationText = r.At("lengthText").Text() is { Length: > 0 } len ? len : null,
            Kind = KindFromVideoType(VideoType(watch)),
            PlayEndpoint = nav.CloneObj(),
            PlaylistId = watch.Str("playlistId"),
            IsExplicit = HasExplicitBadge(r),
        };
        item.Subtitle = ApplyRuns(item, r.At("longBylineText").Runs(), dropSongLabel: true);
        if (item.Artists.Count == 0 && r.At("shortBylineText").Text() is { Length: > 0 } by)
            item.Artists.Add(new Link(by, null));
        return item;
    }

    private static MusicItem ParseNavButton(JsonNode r)
    {
        var cmd = r.At("clickCommand", "browseEndpoint");
        var color = r.Long("solid", "leftStripeColor");
        return new MusicItem
        {
            Kind = ItemKind.Mood,
            Title = r.At("buttonText").Text(),
            BrowseId = cmd.Str("browseId"),
            BrowseParams = cmd.Str("params"),
            AccentColor = color is null ? null : (uint)color.Value,
        };
    }

    private static MusicItem? ParseMultiRow(JsonNode r)
    {
        var watch = r.At("onTap", "watchEndpoint") ?? r.FindFirst("watchEndpoint");
        var item = new MusicItem
        {
            Kind = ItemKind.Episode,
            Title = r.At("title").Text(),
            Subtitle = r.At("subtitle").Text(),
            ThumbnailUrl = r.At("thumbnail").Thumbnail(),
            VideoId = watch.Str("videoId"),
        };
        return item.VideoId is null ? null : item;
    }
}
