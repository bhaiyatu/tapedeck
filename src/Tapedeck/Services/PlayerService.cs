using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Tapedeck.Models;
using Tapedeck.Services.Api;

namespace Tapedeck.Services;

/// <summary>
/// Owns the play queue and playback state. The hidden web player plays whatever this service
/// tells it to; state flows back through <see cref="PlayerBridge"/> events.
/// </summary>
public sealed partial class PlayerService : ObservableObject
{
    private readonly PlayerBridge _bridge;
    private readonly InnerTubeClient _api;
    private readonly SettingsService _settings;

    private string? _queuePlaylistId;
    private string? _queueContinuation;
    private bool _loadingMore;
    private List<MusicItem>? _unshuffled;
    private bool _suppressQueueSync;
    private int _skipOnErrorGeneration;
    private CancellationTokenSource? _trackInfoCts;

    public PlayerService(PlayerBridge bridge, InnerTubeClient api, SettingsService settings)
    {
        _bridge = bridge;
        _api = api;
        _settings = settings;
        Volume = settings.Current.Volume;
        Repeat = settings.Current.Repeat;

        _bridge.StateChanged += OnBridgeState;
        _bridge.Advanced += OnBridgeAdvanced;
        _bridge.Ended += (_, _) => IsPlaying = false;
        _bridge.Error += OnBridgeError;
        _bridge.Ready += (_, _) =>
        {
            _ = _bridge.SetVolumeAsync(Volume);
            if (Current is not null) _ = SyncNextAsync();
        };
        Queue.CollectionChanged += OnQueueChanged;
    }

    public ObservableCollection<MusicItem> Queue { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    public partial MusicItem? Current { get; set; }

    [ObservableProperty]
    public partial int CurrentIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsBuffering { get; set; }

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial double Duration { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsShuffle { get; set; }

    [ObservableProperty]
    public partial RepeatMode Repeat { get; set; }

    [ObservableProperty]
    public partial bool IsAd { get; set; }

    /// <summary>LIKE, DISLIKE, INDIFFERENT, or null when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiked))]
    public partial string? LikeStatus { get; set; }

    [ObservableProperty]
    public partial string? LyricsBrowseId { get; set; }

    /// <summary>True once like status / lyrics id for the current track have been fetched (or failed).</summary>
    [ObservableProperty]
    public partial bool TrackInfoLoaded { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasTrack => Current is not null;
    public bool IsLiked => LikeStatus == "LIKE";

    /// <summary>Raised when a different track becomes current.</summary>
    public event EventHandler<MusicItem>? TrackChanged;

    partial void OnVolumeChanged(double value)
    {
        _settings.Current.Volume = value;
        _settings.Save();
        _ = _bridge.SetVolumeAsync(value);
    }

    partial void OnIsMutedChanged(bool value) => _bridge.IsMuted = value;

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Current.Repeat = value;
        _settings.Save();
        _ = SyncNextAsync();
    }

    // ───────────────────────── Starting playback ─────────────────────────

    /// <summary>Which page (browse id) the current queue came from, so that page can keep feeding it.</summary>
    public string? QueueSourceId { get; private set; }

    /// <summary>Plays a list the app already has (album / playlist tracks, search results).</summary>
    public void PlayList(IEnumerable<MusicItem> items, int startIndex = 0, string? playlistId = null, string? continuation = null,
        bool shuffle = false, string? sourceId = null)
    {
        // Map the start index onto the filtered (playable-only) list.
        var source = items.ToList();
        var startItem = startIndex >= 0 && startIndex < source.Count ? source[startIndex] : null;
        var list = source.Where(i => i.VideoId is not null).ToList();
        if (list.Count == 0) return;
        startIndex = startItem is null ? 0 : Math.Max(0, list.IndexOf(startItem));
        if (shuffle) startIndex = Random.Shared.Next(list.Count);
        ReplaceQueue(list, startIndex, playlistId, continuation);
        QueueSourceId = sourceId;
        if (shuffle) ApplyShuffle();
        PlayIndex(CurrentIndex);
    }

    /// <summary>Adds tracks a page loaded later (long playlists) if the queue still comes from that page.</summary>
    public void AppendFromSource(string sourceId, IEnumerable<MusicItem> items)
    {
        if (QueueSourceId != sourceId) return;
        var known = Queue.Select(q => q.VideoId).ToHashSet();
        var added = items.Where(i => i.VideoId is not null && known.Add(i.VideoId)).ToList();
        if (added.Count == 0) return;
        _suppressQueueSync = true;
        if (IsShuffle)
        {
            // Scatter new tracks among the upcoming ones.
            foreach (var item in added)
                Queue.Insert(Random.Shared.Next(CurrentIndex + 1, Queue.Count + 1), item);
            _unshuffled?.AddRange(added);
        }
        else
        {
            foreach (var item in added) Queue.Add(item);
        }
        _suppressQueueSync = false;
        _ = SyncNextAsync();
    }

    /// <summary>Starts a radio (an endless mix) seeded by a song.</summary>
    public Task PlayRadioAsync(MusicItem seed) =>
        seed.VideoId is null ? Task.CompletedTask : PlayFromQueueApiAsync(seed.VideoId, "RDAMVM" + seed.VideoId, "wAEB", null, seed);

    /// <summary>Plays a song, building the "Up next" mix around it the way YouTube Music does.</summary>
    public Task PlaySongAsync(MusicItem song) => PlayRadioAsync(song);

    /// <summary>Plays from a raw watchEndpoint / watchPlaylistEndpoint (card play buttons, artist shuffle, …).</summary>
    public Task PlayEndpointAsync(JsonObject endpoint)
    {
        var we = endpoint["watchEndpoint"] ?? endpoint["watchPlaylistEndpoint"] ?? endpoint;
        var videoId = (string?)we["videoId"];
        var playlistId = (string?)we["playlistId"];
        var @params = (string?)we["params"];
        int? index = we["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        return PlayFromQueueApiAsync(videoId, playlistId, @params, index, null);
    }

    public async Task PlayCollectionAsync(string browseId, bool shuffle = false)
    {
        try
        {
            var details = await _api.GetCollectionAsync(browseId);
            PlayList(details.Tracks, 0, details.PlaylistId, details.Continuation, shuffle);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task PlayFromQueueApiAsync(string? videoId, string? playlistId, string? @params, int? index, MusicItem? seed)
    {
        // Start the seed straight away so the click feels instant; the queue fills in after.
        if (seed?.VideoId is not null)
        {
            ReplaceQueue([seed], 0, playlistId, null);
            PlayIndex(0);
        }

        try
        {
            var page = await _api.GetQueueAsync(videoId, playlistId, @params, index);
            if (page.Items.Count == 0)
            {
                if (seed is null) StatusMessage = "Nothing to play here.";
                return;
            }

            if (seed is not null)
            {
                // Keep the seed playing; append the mix after it.
                if (Current?.VideoId != seed.VideoId) return; // user moved on meanwhile
                var rest = page.Items.Where(i => i.VideoId != seed.VideoId);
                var enriched = page.Items.FirstOrDefault(i => i.VideoId == seed.VideoId);
                if (enriched is not null && Current is not null && Current.Artists.Count == 0) Current.Artists.AddRange(enriched.Artists);
                _suppressQueueSync = true;
                foreach (var item in rest) Queue.Add(item);
                _suppressQueueSync = false;
                _queuePlaylistId = page.PlaylistId ?? playlistId;
                _queueContinuation = page.Continuation;
                await SyncNextAsync();
            }
            else
            {
                ReplaceQueue(page.Items, page.SelectedIndex, page.PlaylistId ?? playlistId, page.Continuation);
                PlayIndex(CurrentIndex);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void ReplaceQueue(IList<MusicItem> items, int index, string? playlistId, string? continuation)
    {
        _suppressQueueSync = true;
        Queue.Clear();
        foreach (var item in items) Queue.Add(item);
        _suppressQueueSync = false;
        _unshuffled = null;
        IsShuffle = false;
        QueueSourceId = null;
        _queuePlaylistId = playlistId;
        _queueContinuation = continuation;
        CurrentIndex = Math.Clamp(index, 0, Math.Max(0, Queue.Count - 1));
    }

    public void PlayIndex(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        var item = Queue[index];
        CurrentIndex = index;
        SetCurrent(item);
        Position = 0;
        Duration = item.DurationSeconds;
        IsBuffering = true;
        _ = _bridge.LoadAsync(item.VideoId!, NextVideoId());
        _ = LoadMoreIfNeededAsync();
    }

    private void SetCurrent(MusicItem item)
    {
        var changed = !ReferenceEquals(Current, item);
        Current = item;
        StatusMessage = null;
        if (!changed) return;
        LikeStatus = null;
        LyricsBrowseId = null;
        TrackInfoLoaded = false;
        TrackChanged?.Invoke(this, item);
        _ = LoadTrackInfoAsync(item);
    }

    /// <summary>Fetches like status and the lyrics tab for the current track.</summary>
    private async Task LoadTrackInfoAsync(MusicItem item)
    {
        _trackInfoCts?.Cancel();
        var cts = _trackInfoCts = new CancellationTokenSource();
        try
        {
            var info = await _api.GetWatchInfoAsync(item.VideoId!, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(Current, item)) return;
            LikeStatus = info.LikeStatus;
            LyricsBrowseId = info.LyricsBrowseId;
            // Fill in details the source list didn't carry (e.g. album, artwork).
            if (info.Track is { } t)
            {
                item.Album ??= t.Album;
                item.ThumbnailUrl ??= t.ThumbnailUrl;
                if (item.Artists.Count == 0) item.Artists.AddRange(t.Artists);
                if (item.DurationText is null && t.DurationText is not null)
                {
                    item.DurationText = t.DurationText;
                    if (Duration <= 0) Duration = t.DurationSeconds;
                }
            }
        }
        catch (Exception)
        {
            // Non-essential.
        }
        finally
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(Current, item)) TrackInfoLoaded = true;
        }
    }

    // ───────────────────────── Transport ─────────────────────────

    public void TogglePlayPause()
    {
        if (Current is null) return;
        if (IsPlaying)
        {
            IsPlaying = false;
            _ = _bridge.PauseAsync();
        }
        else
        {
            IsPlaying = true;
            _ = _bridge.PlayAsync();
        }
    }

    public void Play()
    {
        if (Current is null || IsPlaying) return;
        TogglePlayPause();
    }

    public void Pause()
    {
        if (Current is null || !IsPlaying) return;
        TogglePlayPause();
    }

    public async void Next()
    {
        if (Queue.Count == 0) return;
        if (CurrentIndex + 1 < Queue.Count)
        {
            PlayIndex(CurrentIndex + 1);
        }
        else if (Repeat == RepeatMode.All)
        {
            PlayIndex(0);
        }
        else if (_queueContinuation is not null)
        {
            await LoadMoreIfNeededAsync(force: true);
            if (CurrentIndex + 1 < Queue.Count) PlayIndex(CurrentIndex + 1);
        }
    }

    public void Previous()
    {
        if (Current is null) return;
        if (Position > 3 || CurrentIndex <= 0)
        {
            Seek(0);
            return;
        }
        PlayIndex(CurrentIndex - 1);
    }

    public void Seek(double seconds)
    {
        if (Current is null) return;
        Position = seconds;
        _ = _bridge.SeekAsync(seconds);
    }

    public void ToggleShuffle()
    {
        if (IsShuffle)
        {
            // Restore original order, keeping the current track playing.
            if (_unshuffled is not null)
            {
                var extras = Queue.Where(q => !_unshuffled.Contains(q)).ToList();
                var restored = _unshuffled.Where(Queue.Contains).ToList();
                var currentPos = Current is null ? -1 : restored.IndexOf(Current);
                if (currentPos >= 0) restored.InsertRange(currentPos + 1, extras);
                else restored.AddRange(extras);
                ReorderQueue(restored);
            }
            _unshuffled = null;
            IsShuffle = false;
        }
        else
        {
            ApplyShuffle();
        }
        _ = SyncNextAsync();
    }

    private void ApplyShuffle()
    {
        _unshuffled = Queue.ToList();
        var current = Current ?? (CurrentIndex >= 0 && CurrentIndex < Queue.Count ? Queue[CurrentIndex] : null);
        var others = Queue.Where(q => !ReferenceEquals(q, current)).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(others));
        var shuffled = current is null ? others : others.Prepend(current).ToList();
        ReorderQueue(shuffled);
        IsShuffle = true;
    }

    private void ReorderQueue(List<MusicItem> order)
    {
        _suppressQueueSync = true;
        Queue.Clear();
        foreach (var i in order) Queue.Add(i);
        _suppressQueueSync = false;
        CurrentIndex = Current is null ? 0 : Queue.IndexOf(Current);
    }

    public void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    // ───────────────────────── Queue editing ─────────────────────────

    public void PlayNext(MusicItem item)
    {
        if (item.VideoId is null) return;
        if (Current is null)
        {
            PlayList([item]);
            return;
        }
        Queue.Insert(CurrentIndex + 1, Queue.Contains(item) ? item.Clone() : item);
        StatusMessage = $"“{item.Title}” will play next";
    }

    public void AddToQueue(MusicItem item)
    {
        if (item.VideoId is null) return;
        if (Current is null)
        {
            PlayList([item]);
            return;
        }
        Queue.Add(Queue.Contains(item) ? item.Clone() : item);
        StatusMessage = $"Added “{item.Title}” to the queue";
    }

    public void AddToQueue(IEnumerable<MusicItem> items, bool next)
    {
        var list = items.Where(i => i.VideoId is not null).ToList();
        if (list.Count == 0) return;
        if (Current is null)
        {
            PlayList(list);
            return;
        }
        var at = next ? CurrentIndex + 1 : Queue.Count;
        foreach (var item in list) Queue.Insert(at++, Queue.Contains(item) ? item.Clone() : item);
        StatusMessage = next ? $"{list.Count} songs will play next" : $"Added {list.Count} songs to the queue";
    }

    public void RemoveFromQueue(MusicItem item)
    {
        if (ReferenceEquals(item, Current)) return;
        Queue.Remove(item);
        _unshuffled?.Remove(item);
    }

    public void ClearUpcoming()
    {
        if (Current is null) return;
        _suppressQueueSync = true;
        for (var i = Queue.Count - 1; i > CurrentIndex; i--) Queue.RemoveAt(i);
        _suppressQueueSync = false;
        _queueContinuation = null;
        _ = SyncNextAsync();
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressQueueSync) return;
        // Reorders from the queue panel arrive as remove + add; recompute where "now" is.
        if (Current is not null)
        {
            var idx = Queue.IndexOf(Current);
            if (idx >= 0) CurrentIndex = idx;
        }
        _ = SyncNextAsync();
    }

    private string? NextVideoId()
    {
        if (Repeat == RepeatMode.One || Queue.Count == 0) return null;
        if (CurrentIndex + 1 < Queue.Count) return Queue[CurrentIndex + 1].VideoId;
        return Repeat == RepeatMode.All ? Queue[0].VideoId : null;
    }

    private Task SyncNextAsync() => _bridge.SetNextAsync(NextVideoId(), Repeat == RepeatMode.One);

    private async Task LoadMoreIfNeededAsync(bool force = false)
    {
        if (_loadingMore || _queueContinuation is null) return;
        if (!force && CurrentIndex < Queue.Count - 3) return;
        _loadingMore = true;
        try
        {
            var page = await _api.ContinueQueueAsync(_queueContinuation, _queuePlaylistId);
            var known = Queue.Select(q => q.VideoId).ToHashSet();
            _suppressQueueSync = true;
            foreach (var item in page.Items.Where(i => known.Add(i.VideoId)))
            {
                Queue.Add(item);
                _unshuffled?.Add(item);
            }
            _suppressQueueSync = false;
            _queueContinuation = page.Items.Count > 0 ? page.Continuation : null;
            await SyncNextAsync();
        }
        catch (Exception)
        {
            _queueContinuation = null;
        }
        finally
        {
            _suppressQueueSync = false;
            _loadingMore = false;
        }
    }

    // ───────────────────────── Bridge events ─────────────────────────

    private void OnBridgeState(object? sender, BridgeState s)
    {
        IsAd = s.Ad;
        IsPlaying = s.Playing;
        IsBuffering = s.Buffering;
        if (s.Ad) return;
        if (Current is not null && s.VideoId is not null && s.VideoId != Current.VideoId) return; // stale report from the previous track
        Position = s.Position;
        if (s.Duration > 0) Duration = s.Duration;
    }

    private void OnBridgeAdvanced(object? sender, string videoId)
    {
        // The page moved on by itself at the end of a track.
        var idx = -1;
        if (CurrentIndex + 1 < Queue.Count && Queue[CurrentIndex + 1].VideoId == videoId) idx = CurrentIndex + 1;
        else if (Repeat == RepeatMode.All && Queue.Count > 0 && Queue[0].VideoId == videoId && CurrentIndex == Queue.Count - 1) idx = 0;
        else idx = Queue.ToList().FindIndex(q => q.VideoId == videoId);

        if (idx < 0)
        {
            // Not ours (e.g. YouTube Music insisted on something else). Show it rather than lie.
            var stranger = new MusicItem { Kind = ItemKind.Song, VideoId = videoId, Title = "Now playing" };
            idx = CurrentIndex + 1;
            _suppressQueueSync = true;
            Queue.Insert(Math.Min(idx, Queue.Count), stranger);
            _suppressQueueSync = false;
        }

        CurrentIndex = idx;
        SetCurrent(Queue[idx]);
        Position = 0;
        Duration = Queue[idx].DurationSeconds;
        _ = SyncNextAsync();
        _ = LoadMoreIfNeededAsync();
    }

    private async void OnBridgeError(object? sender, string message)
    {
        StatusMessage = $"Can't play this track: {message}";
        var generation = ++_skipOnErrorGeneration;
        await Task.Delay(2500);
        if (generation == _skipOnErrorGeneration && CurrentIndex + 1 < Queue.Count) Next();
    }

    // ───────────────────────── Library actions ─────────────────────────

    public async Task ToggleLikeAsync()
    {
        if (Current?.VideoId is not { } id) return;
        if (!App.Auth.IsSignedIn)
        {
            StatusMessage = "Sign in to like songs.";
            return;
        }
        var wasLiked = IsLiked;
        LikeStatus = wasLiked ? "INDIFFERENT" : "LIKE";
        try
        {
            await _api.RateAsync(id, wasLiked ? null : true);
        }
        catch (Exception ex)
        {
            LikeStatus = wasLiked ? "LIKE" : "INDIFFERENT";
            StatusMessage = ex.Message;
        }
    }
}
