using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tapedeck.Models;
using Tapedeck.Services.Api;

namespace Tapedeck.Services;

/// <summary>
/// Finds lyrics for a track: time-synced lyrics from LRCLIB (a free, open lyrics database)
/// when available, otherwise YouTube Music's own plain lyrics.
/// </summary>
public sealed partial class LyricsService
{
    private readonly InnerTubeClient _api;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Lyrics?> _cache = [];

    public LyricsService(InnerTubeClient api)
    {
        _api = api;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Tapedeck/0.1 (Windows YouTube Music client)");
    }

    public async Task<Lyrics?> GetAsync(MusicItem track, string? lyricsBrowseId, double durationSeconds, CancellationToken ct)
    {
        if (track.VideoId is { } id && _cache.TryGetValue(id, out var cached)) return cached;

        Lyrics? result = null;
        var failed = false;
        if (App.Settings.Current.UseLrclib)
        {
            try
            {
                result = await FromLrclibAsync(track, durationSeconds, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fall through to YouTube Music lyrics.
                failed = true;
            }
        }

        if (result is null && lyricsBrowseId is not null)
        {
            try
            {
                result = await _api.GetLyricsAsync(lyricsBrowseId, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failed = true;
            }
        }

        // Remember what was found, or a definite "none"; a lookup that failed (e.g. offline) is retried next time.
        if (track.VideoId is not null && !ct.IsCancellationRequested && (result is not null || !failed)) _cache[track.VideoId] = result;
        return result;
    }

    private async Task<Lyrics?> FromLrclibAsync(MusicItem track, double duration, CancellationToken ct)
    {
        var artist = track.Artists.FirstOrDefault()?.Name;
        if (string.IsNullOrEmpty(artist)) return null;
        var title = CleanTitle(track.Title);

        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (track.Album?.Name is { Length: > 0 } album) query += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration > 0) query += $"&duration={Math.Round(duration).ToString(CultureInfo.InvariantCulture)}";

        JsonNode? hit = null;
        using (var res = await _http.GetAsync($"https://lrclib.net/api/get?{query}", ct))
        {
            if (res.IsSuccessStatusCode) hit = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            else if (res.StatusCode != HttpStatusCode.NotFound) return null;
        }

        if (hit?["syncedLyrics"] is null)
        {
            // Exact lookup failed; search and pick the closest duration with synced lyrics.
            var search = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var res = await _http.GetAsync(search, ct);
            if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) is JsonArray results)
            {
                hit = results
                    .Where(r => !string.IsNullOrEmpty((string?)r?["syncedLyrics"]))
                    .OrderBy(r => duration > 0 ? Math.Abs(((double?)r?["duration"] ?? 0) - duration) : 0)
                    .FirstOrDefault(r => duration <= 0 || Math.Abs(((double?)r?["duration"] ?? 0) - duration) < 4)
                    ?? hit;
            }
        }

        if (hit is null) return null;
        if (hit["instrumental"]?.GetValue<bool>() == true)
            return new Lyrics { PlainText = "♪ Instrumental ♪", Source = "LRCLIB" };

        var synced = (string?)hit["syncedLyrics"];
        if (!string.IsNullOrWhiteSpace(synced))
        {
            var lines = ParseLrc(synced);
            if (lines.Count > 0)
                return new Lyrics { Lines = lines, PlainText = (string?)hit["plainLyrics"], Source = "LRCLIB" };
        }
        var plain = (string?)hit["plainLyrics"];
        return string.IsNullOrWhiteSpace(plain) ? null : new Lyrics { PlainText = plain, Source = "LRCLIB" };
    }

    /// <summary>Strips decorations that rarely appear in lyric databases: "(Official Video)", "[Remastered 2011]", "feat. X".</summary>
    public static string CleanTitle(string title)
    {
        var t = BracketNoise().Replace(title, "");
        t = FeatRegex().Replace(t, "");
        return t.Trim();
    }

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = TimeTag().Matches(raw);
            if (matches.Count == 0) continue;
            var text = TimeTag().Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine { Time = minutes * 60 + seconds, Text = text });
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(official|video|audio|lyric|visuali[sz]er|remaster|live|mv|hd|4k)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoise();

    [GeneratedRegex(@"\s*[\(\[]?\s*(feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();
}
