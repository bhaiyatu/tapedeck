using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tapedeck.Models;
using Tapedeck.Services.Api;

namespace Tapedeck.Services.Discord;

/// <summary>What the member list shows next to your name ("Listening to …").</summary>
public enum DiscordStatusDisplay
{
    App = 0,    // Discord: status_display_type NAME    → "Listening to Tapedeck"
    Artist = 1, //          status_display_type STATE   → "Listening to Daft Punk"
    Song = 2,   //          status_display_type DETAILS → "Listening to One More Time"
}

/// <summary>The parts of a track that are shared on Discord, already validated and trimmed.</summary>
public sealed record PresenceTrack(
    string VideoId,
    string Title,
    string? Artist,
    string? ArtistId,
    string? Album,
    string? AlbumId,
    string? ArtUrl,
    double Duration);

/// <summary>Builds the Rich Presence activity Discord shows for the current track.</summary>
public static partial class DiscordActivity
{
    /// <summary>
    /// The public application id of the "Tapedeck" app in the Discord Developer Portal. Discord shows
    /// that app's name and icon in the activity header. It is an identifier, not a secret.
    /// </summary>
    public const string ApplicationId = "1568338558559846521";

    public const string ProjectUrl = "https://github.com/bhaiyatu/tapedeck";
    public const string DownloadUrl = ProjectUrl + "/releases/latest";
    /// <summary>
    /// The Tapedeck badge: a Rich Presence art asset uploaded to the Discord app under this key
    /// (from assets/discord/tapedeck.png), so it never depends on an outside server.
    /// </summary>
    public const string LogoAsset = "tapedeck";

    // Discord's limits: text fields 2–128 characters, image keys/URLs up to 256, button labels up to 32.
    private const int MaxText = 128;
    private const int MaxImageUrl = 256;

    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelIdRegex();

    [GeneratedRegex("^MPRE[A-Za-z0-9_-]{5,60}$")]
    private static partial Regex AlbumIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Picks out what is shared about a queue item, or null when it can't be shared (no valid video id).</summary>
    public static PresenceTrack? FromItem(MusicItem item, double duration)
    {
        var videoId = Validate.VideoId(item.VideoId);
        if (videoId is null || string.IsNullOrWhiteSpace(item.Title)) return null;

        var artist = item.Artists.Count > 0 ? item.ArtistText : FirstSubtitlePart(item.Subtitle);
        var artistId = item.Artists.Select(a => a.Id).FirstOrDefault(id => id is not null && ChannelIdRegex().IsMatch(id));
        var albumId = item.Album?.Id is { } a && AlbumIdRegex().IsMatch(a) ? a : null;
        return new PresenceTrack(
            videoId,
            Clean(item.Title)!,
            Clean(artist),
            artistId,
            Clean(item.Album?.Name),
            albumId,
            ArtworkUrl(item.ThumbnailUrl, videoId),
            Math.Round(duration > 0 && double.IsFinite(duration) ? duration : item.DurationSeconds));
    }

    /// <summary>The activity JSON for a playing track that started (position 0) at <paramref name="startedAt"/>.</summary>
    public static JsonObject Build(PresenceTrack t, DateTimeOffset startedAt, DiscordStatusDisplay display)
    {
        var watchUrl = $"https://music.youtube.com/watch?v={t.VideoId}";

        // Asking for the artist line when there isn't one would leave the status blank.
        if (display == DiscordStatusDisplay.Artist && t.Artist is null) display = DiscordStatusDisplay.App;

        var activity = new JsonObject
        {
            ["type"] = 2, // Listening: a music card with a progress bar.
            ["status_display_type"] = (int)display,
            ["details"] = Field(t.Title),
            ["details_url"] = watchUrl,
        };
        if (t.Artist is not null)
        {
            activity["state"] = Field(t.Artist);
            if (t.ArtistId is not null) activity["state_url"] = $"https://music.youtube.com/channel/{t.ArtistId}";
        }

        var start = startedAt.ToUnixTimeMilliseconds();
        var timestamps = new JsonObject { ["start"] = start };
        if (t.Duration > 0) timestamps["end"] = start + (long)Math.Round(t.Duration * 1000);
        activity["timestamps"] = timestamps;

        var assets = new JsonObject
        {
            ["large_image"] = t.ArtUrl ?? LogoAsset,
            ["small_image"] = LogoAsset,
            ["small_text"] = "Tapedeck",
            ["small_url"] = ProjectUrl,
        };
        if (t.Album is not null)
        {
            assets["large_text"] = Field(t.Album);
            assets["large_url"] = t.AlbumId is not null ? $"https://music.youtube.com/browse/{t.AlbumId}" : watchUrl;
        }
        else
        {
            assets["large_url"] = watchUrl;
        }
        activity["assets"] = assets;

        activity["buttons"] = new JsonArray
        {
            new JsonObject { ["label"] = "Play on YouTube Music", ["url"] = watchUrl },
            new JsonObject { ["label"] = "Get Tapedeck", ["url"] = DownloadUrl },
        };
        return activity;
    }

    /// <summary>
    /// Square, 512px album art from YouTube's image servers. Anything else (or an over-long URL)
    /// falls back to the video's own thumbnail.
    /// </summary>
    public static string? ArtworkUrl(string? thumbnail, string videoId)
    {
        var sized = Formatting.SizedThumbnail(thumbnail, 512);
        if (sized is not null && sized.Length <= MaxImageUrl && Uri.TryCreate(sized, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && IsYouTubeImageHost(uri.Host))
            return sized;
        return $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
    }

    private static bool IsYouTubeImageHost(string host) =>
        host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ggpht.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase);

    private static string? FirstSubtitlePart(string? subtitle) =>
        subtitle?.Split(" • ", 2, StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>One line of plain text: no control characters or runs of whitespace.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var s = WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Fits Discord's 2–128 character limit without splitting a character in half.</summary>
    public static string Field(string text)
    {
        if (text.Length > MaxText)
        {
            var info = new StringInfo(text);
            var cut = info.LengthInTextElements;
            string s;
            do
            {
                s = info.SubstringByTextElements(0, --cut) + "…";
            } while (s.Length > MaxText && cut > 1);
            text = s;
        }
        // Discord rejects one-character fields; pad with an invisible Braille blank.
        while (text.Length < 2) text += "⠀";
        return text;
    }
}
