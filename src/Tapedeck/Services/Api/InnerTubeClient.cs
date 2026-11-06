using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tapedeck.Models;

namespace Tapedeck.Services.Api;

public sealed class AuthExpiredException(string message) : Exception(message);

/// <summary>
/// Minimal client for YouTube Music's InnerTube API (the JSON API music.youtube.com itself uses).
/// When a cookie provider is set, requests are signed with SAPISIDHASH so they act as the signed-in user.
/// </summary>
public sealed class InnerTubeClient
{
    public const string Origin = "https://music.youtube.com";
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0";

    private readonly HttpClient _http;

    public InnerTubeClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            // Never follow redirects: HttpClient would re-send our manually added Cookie header
            // (the Google session) to whatever host a redirect points at.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 256, // KB; YouTube sends very large Set-Cookie / CSP headers
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    // Values below can be updated from the player page; only well-formed ones are accepted,
    // since they are sent as request headers alongside the session cookies.
    private string _clientVersion = "1.20261104.00.00";
    private string _language = "en";
    private string _region = "US";
    private string? _visitorData;

    public string ClientVersion
    {
        get => _clientVersion;
        set => _clientVersion = Validate.ClientVersion(value) ?? _clientVersion;
    }

    public string Language
    {
        get => _language;
        set => _language = Validate.Locale(value) ?? _language;
    }

    public string Region
    {
        get => _region;
        set => _region = Validate.Locale(value) ?? _region;
    }

    public string? VisitorData
    {
        get => _visitorData;
        set => _visitorData = Validate.VisitorData(value) ?? _visitorData;
    }

    public string UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>Returns the Cookie header for music.youtube.com, or null when signed out.</summary>
    public Func<Task<string?>>? CookieProvider { get; set; }

    /// <summary>Raised (on a background thread) when a request that carried the session got 401/403.</summary>
    public event Action? SessionRejected;

    // ───────────────────────── Transport ─────────────────────────

    private JsonObject Context() => new()
    {
        ["client"] = new JsonObject
        {
            ["clientName"] = "WEB_REMIX",
            ["clientVersion"] = ClientVersion,
            ["hl"] = Language,
            ["gl"] = Region,
            ["visitorData"] = VisitorData,
            ["userAgent"] = UserAgent,
            ["platform"] = "DESKTOP",
            ["osName"] = "Windows",
            ["osVersion"] = "10.0",
            ["utcOffsetMinutes"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes,
        },
        ["user"] = new JsonObject { ["lockedSafetyMode"] = false },
    };

    public async Task<JsonNode> PostAsync(string endpoint, JsonObject body, string? extraQuery = null, CancellationToken ct = default)
    {
        body["context"] = Context();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Origin}/youtubei/v1/{endpoint}?prettyPrint=false{extraQuery}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Add("Origin", Origin);
        req.Headers.Add("X-Origin", Origin);
        req.Headers.Referrer = new Uri(Origin + "/");
        req.Headers.Add("X-Youtube-Client-Name", "67");
        req.Headers.Add("X-Youtube-Client-Version", ClientVersion);
        req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(Language));
        if (VisitorData is not null) req.Headers.Add("X-Goog-Visitor-Id", VisitorData);

        // The session is only ever attached to requests for the fixed music.youtube.com origin over HTTPS.
        var cookies = CookieProvider is null ? null : await CookieProvider().ConfigureAwait(false);
        var withSession = false;
        if (!string.IsNullOrEmpty(cookies) && req.RequestUri is { Scheme: "https", Host: "music.youtube.com" } && Validate.IsHeaderSafe(cookies))
        {
            withSession = true;
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (BuildAuthorization(cookies) is { } auth)
            {
                req.Headers.Add("Authorization", auth);
                req.Headers.Add("X-Goog-AuthUser", "0");
            }
        }

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if ((int)res.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"YouTube Music redirected the request ({(int)res.StatusCode}); not following it.");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (!withSession) throw new HttpRequestException($"YouTube Music refused the request ({(int)res.StatusCode}).");
            SessionRejected?.Invoke();
            throw new AuthExpiredException($"YouTube Music rejected the request ({(int)res.StatusCode}). Try signing in again.");
        }
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"YouTube Music returned {(int)res.StatusCode} for {endpoint}.");
        var json = JsonNode.Parse(text) ?? throw new HttpRequestException("Empty response from YouTube Music.");
        if (json.Str("responseContext", "visitorData") is { } vd && VisitorData is null) VisitorData = vd;
        return json;
    }

    /// <summary>
    /// Builds the SAPISIDHASH Authorization header YouTube's own web client sends:
    /// SHA1("{timestamp} {SAPISID} {origin}"), plus the 1P/3P variants when those cookies exist.
    /// </summary>
    public static string? BuildAuthorization(string cookieHeader, long? timestamp = null)
    {
        var cookies = ParseCookieHeader(cookieHeader);
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sapisid = cookies.GetValueOrDefault("SAPISID") ?? cookies.GetValueOrDefault("__Secure-3PAPISID");
        if (sapisid is null) return null;

        var parts = new List<string> { $"SAPISIDHASH {Hash(sapisid)}" };
        if (cookies.GetValueOrDefault("__Secure-1PAPISID") is { } p1) parts.Add($"SAPISID1PHASH {Hash(p1)}");
        if (cookies.GetValueOrDefault("__Secure-3PAPISID") is { } p3) parts.Add($"SAPISID3PHASH {Hash(p3)}");
        return string.Join(' ', parts);

        string Hash(string secret)
        {
            var digest = SHA1.HashData(Encoding.UTF8.GetBytes($"{ts} {secret} {Origin}"));
            return $"{ts}_{Convert.ToHexStringLower(digest)}";
        }
    }

    public static Dictionary<string, string> ParseCookieHeader(string header)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) dict[part[..eq]] = part[(eq + 1)..];
        }
        return dict;
    }

    private static string Continuation(string token) => $"&ctoken={Uri.EscapeDataString(token)}&continuation={Uri.EscapeDataString(token)}&type=next";

    private static JsonObject Browse(string browseId, string? @params = null)
    {
        var body = new JsonObject { ["browseId"] = browseId };
        if (!string.IsNullOrEmpty(@params)) body["params"] = @params;
        return body;
    }

    // ───────────────────────── High-level API ─────────────────────────

    public async Task<ShelvesPageData> GetShelvesAsync(string browseId, string? @params = null, CancellationToken ct = default) =>
        PageParser.ParseShelvesPage(await PostAsync("browse", Browse(browseId, @params), ct: ct));

    public async Task<ShelvesPageData> ContinueShelvesAsync(string token, CancellationToken ct = default) =>
        PageParser.ParseShelvesContinuation(await PostAsync("browse", [], Continuation(token), ct));

    public async Task<SearchResults> SearchAsync(string query, string? @params = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["query"] = query };
        if (!string.IsNullOrEmpty(@params)) body["params"] = @params;
        return PageParser.ParseSearch(await PostAsync("search", body, ct: ct));
    }

    public async Task<SearchResults> ContinueSearchAsync(string token, CancellationToken ct = default) =>
        PageParser.ParseSearchContinuation(await PostAsync("search", [], Continuation(token), ct));

    public async Task<List<string>> GetSuggestionsAsync(string input, CancellationToken ct = default) =>
        PageParser.ParseSuggestions(await PostAsync("music/get_search_suggestions", new JsonObject { ["input"] = input }, ct: ct));

    public async Task<CollectionDetails> GetCollectionAsync(string browseId, CancellationToken ct = default) =>
        PageParser.ParseCollection(await PostAsync("browse", Browse(browseId), ct: ct), browseId);

    public async Task<(List<MusicItem> Items, string? Continuation)> ContinueCollectionAsync(string token, CancellationToken ct = default)
    {
        // New-style tokens go in the body; old-style ones in the query. Sending both is accepted.
        var json = await PostAsync("browse", new JsonObject { ["continuation"] = token }, ct: ct);
        var result = PageParser.ParseCollectionContinuation(json);
        if (result.Items.Count == 0)
            result = PageParser.ParseCollectionContinuation(await PostAsync("browse", [], Continuation(token), ct));
        return result;
    }

    public async Task<ArtistDetails> GetArtistAsync(string browseId, CancellationToken ct = default) =>
        PageParser.ParseArtist(await PostAsync("browse", Browse(browseId), ct: ct));

    /// <summary>The "Up next" queue YouTube Music builds for a video / playlist / radio.</summary>
    public async Task<QueuePage> GetQueueAsync(string? videoId, string? playlistId, string? @params = null, int? index = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
            ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
        };
        if (videoId is not null) body["videoId"] = videoId;
        if (playlistId is not null) body["playlistId"] = playlistId;
        if (@params is not null) body["params"] = @params;
        if (index is not null) body["index"] = index;
        return PageParser.ParseQueue(await PostAsync("next", body, ct: ct));
    }

    public async Task<QueuePage> ContinueQueueAsync(string token, string? playlistId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
            ["continuation"] = token,
        };
        if (playlistId is not null) body["playlistId"] = playlistId;
        return PageParser.ParseQueueContinuation(await PostAsync("next", body, ct: ct));
    }

    public async Task<WatchInfo> GetWatchInfoAsync(string videoId, CancellationToken ct = default) =>
        PageParser.ParseWatchInfo(await PostAsync("next", new JsonObject { ["videoId"] = videoId, ["isAudioOnly"] = true }, ct: ct));

    public async Task<Lyrics?> GetLyricsAsync(string browseId, CancellationToken ct = default) =>
        PageParser.ParseLyrics(await PostAsync("browse", Browse(browseId), ct: ct));

    /// <summary>like = true (like), false (dislike), null (remove rating).</summary>
    public async Task RateAsync(string videoId, bool? like, CancellationToken ct = default)
    {
        var endpoint = like switch { true => "like/like", false => "like/dislike", null => "like/removelike" };
        await PostAsync(endpoint, new JsonObject { ["target"] = new JsonObject { ["videoId"] = videoId } }, ct: ct);
    }

    public async Task<AccountInfo?> GetAccountAsync(CancellationToken ct = default) =>
        PageParser.ParseAccount(await PostAsync("account/account_menu", [], ct: ct));
}
