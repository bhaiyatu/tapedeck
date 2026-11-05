using System.Text.Json.Nodes;

namespace Tapedeck.Models;

public enum ItemKind
{
    Unknown,
    Song,
    Video,
    Album,
    Playlist,
    Artist,
    Podcast,
    Episode,
    Mood,
}

/// <summary>A named link to another page (artist, album).</summary>
public sealed record Link(string Name, string? Id);

/// <summary>
/// One piece of YouTube Music content as shown in a card, a list row, or a queue entry.
/// Parsed from the various InnerTube item renderers into a single shape.
/// </summary>
public sealed class MusicItem
{
    public ItemKind Kind { get; set; }

    /// <summary>The type word YouTube Music shows ("Song", "Single", "Profile", …), when present.</summary>
    public string? TypeLabel { get; set; }

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? ThumbnailUrl { get; set; }

    /// <summary>Set for playable items (songs, videos, episodes).</summary>
    public string? VideoId { get; set; }

    /// <summary>Playlist to play (for albums, this is the OLAK… id).</summary>
    public string? PlaylistId { get; set; }

    /// <summary>Page to open (MPRE… album, VL… playlist, UC… artist, …).</summary>
    public string? BrowseId { get; set; }
    public string? BrowseParams { get; set; }

    public List<Link> Artists { get; set; } = [];
    public Link? Album { get; set; }
    public string? DurationText { get; set; }
    public bool IsExplicit { get; set; }

    /// <summary>Track number on album pages.</summary>
    public string? Index { get; set; }

    /// <summary>Raw watchEndpoint / watchPlaylistEndpoint used by the play button.</summary>
    public JsonObject? PlayEndpoint { get; set; }

    /// <summary>Colour stripe for mood / genre buttons (ARGB).</summary>
    public uint? AccentColor { get; set; }

    public bool IsPlayable => VideoId is not null;
    public bool IsRound => Kind == ItemKind.Artist;
    public bool IsWide { get; set; }

    public string ArtistText => Artists.Count > 0 ? string.Join(", ", Artists.Select(a => a.Name)) : "";

    public double DurationSeconds => Formatting.ParseDuration(DurationText);

    /// <summary>A shallow copy, so the same song can sit in the queue twice as distinct entries.</summary>
    public MusicItem Clone()
    {
        var copy = (MusicItem)MemberwiseClone();
        copy.Artists = [.. Artists];
        return copy;
    }

    public override string ToString() => $"[{Kind}] {Title} — {Subtitle}";
}

public enum ShelfStyle
{
    /// <summary>Horizontal row of square/round artwork cards.</summary>
    Cards,

    /// <summary>Wrapping grid of artwork cards (library, mood pages).</summary>
    Grid,

    /// <summary>Compact rows (songs, search results).</summary>
    List,

    /// <summary>Coloured mood / genre buttons.</summary>
    Chips,

    /// <summary>A block of text (artist bio, album description).</summary>
    Text,
}

public sealed class Shelf
{
    public string Title { get; set; } = "";
    public string? Strapline { get; set; }
    public ShelfStyle Style { get; set; }
    public List<MusicItem> Items { get; set; } = [];
    public string? Text { get; set; }

    /// <summary>"More" / "See all" target.</summary>
    public string? MoreBrowseId { get; set; }
    public string? MoreParams { get; set; }

    /// <summary>Search filter params for "See all" on search shelves.</summary>
    public string? MoreSearchParams { get; set; }

    public override string ToString() => $"{Style} \"{Title}\" ({Items.Count} items)";
}

public sealed class Chip
{
    public string Text { get; set; } = "";
    public string? BrowseId { get; set; }
    public string? Params { get; set; }
    public bool IsSelected { get; set; }
}

public sealed class ShelvesPageData
{
    public string? Title { get; set; }
    public List<Shelf> Shelves { get; set; } = [];
    public List<Chip> Chips { get; set; } = [];
    public string? Continuation { get; set; }
}

public sealed class CollectionDetails
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string SecondSubtitle { get; set; } = "";
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<Link> Artists { get; set; } = [];
    public List<MusicItem> Tracks { get; set; } = [];
    public string? PlaylistId { get; set; }
    public string? Continuation { get; set; }
    public bool IsAlbum { get; set; }
    public List<Shelf> Related { get; set; } = [];
}

public sealed class ArtistDetails
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? AudienceText { get; set; }
    public JsonObject? ShuffleEndpoint { get; set; }
    public JsonObject? RadioEndpoint { get; set; }
    public List<Shelf> Shelves { get; set; } = [];
}

public sealed class SearchResults
{
    public List<Shelf> Shelves { get; set; } = [];
    public List<Chip> Filters { get; set; } = [];
    public MusicItem? TopResult { get; set; }
    public string? Continuation { get; set; }
}

public sealed class QueuePage
{
    public List<MusicItem> Items { get; set; } = [];
    public int SelectedIndex { get; set; }
    public string? PlaylistId { get; set; }
    public string? Continuation { get; set; }
    public bool IsRadioContinuation { get; set; }
}

public sealed class WatchInfo
{
    public string? LyricsBrowseId { get; set; }
    public string? RelatedBrowseId { get; set; }
    public string? LikeStatus { get; set; }
    public MusicItem? Track { get; set; }
}

public sealed class LyricLine
{
    public double Time { get; init; }
    public string Text { get; init; } = "";
}

public sealed class Lyrics
{
    public string? PlainText { get; set; }
    public List<LyricLine> Lines { get; set; } = [];
    public string? Source { get; set; }
    public bool IsSynced => Lines.Count > 0;
}

public sealed class AccountInfo
{
    public string Name { get; set; } = "";
    public string? Handle { get; set; }
    public string? PhotoUrl { get; set; }
}
