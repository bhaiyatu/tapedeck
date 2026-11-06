using System.ComponentModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Tapedeck.Services.Discord;

namespace Tapedeck.Services;

public enum DiscordConnection
{
    Off,
    NotConfigured,
    Waiting,
    Connected,
    Error,
}

/// <summary>
/// Shows the playing track on the user's Discord profile ("Listening to Tapedeck": artwork, song,
/// artist, album and a live progress bar), like Spotify's integration. Off unless the user turns it
/// on. Talks only to the Discord app on this PC; hidden while paused, during ads and when Tapedeck closes.
/// </summary>
public sealed partial class DiscordPresenceService : ObservableObject, IDisposable
{
    // A pause shorter than this (track changes, buffering) doesn't clear the card.
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryWhenClosed = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(30);
    // Re-send when the position drifts this far from what Discord is counting (a seek).
    private const long DriftMs = 2000;
    // Discord allows 5 activity updates per 20 seconds.
    private const int RateCount = 5;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(20);

    private readonly PlayerService _player;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _life = new();
    private readonly Queue<DateTime> _sends = new();

    private DiscordIpcClient? _client;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private Sent? _sent;              // what Discord currently shows (null = nothing)
    private DateTime _retryAt;
    private DateTime _notPlayingSince = DateTime.UtcNow;

    [ObservableProperty]
    public partial DiscordConnection Connection { get; set; }

    [ObservableProperty]
    public partial string? UserName { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    public bool IsAvailable => DiscordActivity.ApplicationId.Length > 0;

    public DiscordPresenceService(PlayerService player, SettingsService settings, DispatcherQueue dispatcher)
    {
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;
        Connection = !_settings.Current.DiscordPresence ? DiscordConnection.Off
            : IsAvailable ? DiscordConnection.Waiting : DiscordConnection.NotConfigured;
        _player.PropertyChanged += OnPlayerChanged;
        UpdateSnapshot();
        _ = Task.Run(RunAsync);
    }

    /// <summary>Call after changing the Discord settings.</summary>
    public void SettingsChanged()
    {
        UpdateSnapshot();
        _wake.Release();
    }

    // ───────────── UI thread: follow the player ─────────────

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Current):
            case nameof(PlayerService.TrackInfoLoaded): // album, artist links and artwork can arrive late
            case nameof(PlayerService.IsPlaying):
            case nameof(PlayerService.IsAd):
            case nameof(PlayerService.Duration):
            case nameof(PlayerService.Position):
                UpdateSnapshot();
                break;
        }
    }

    private void UpdateSnapshot()
    {
        var item = _player.Current;
        var playing = item is not null && _player.IsPlaying && !_player.IsAd;
        var now = DateTime.UtcNow;
        if (playing) _notPlayingSince = DateTime.MaxValue;
        else if (_notPlayingSince == DateTime.MaxValue) _notPlayingSince = now;

        var track = item is null ? null : DiscordActivity.FromItem(item, _player.Duration);
        var startedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Math.Max(0, _player.Position));
        var next = new Snapshot(track, playing, startedAt, _notPlayingSince, _settings.Current.DiscordStatus);
        var prev = _snapshot;
        _snapshot = next;

        // Position ticks four times a second; only wake the sender for changes Discord should see.
        if (prev.Track != next.Track || prev.Playing != next.Playing || prev.Display != next.Display
            || Math.Abs((next.StartedAt - prev.StartedAt).TotalMilliseconds) > DriftMs)
            _wake.Release();
    }

    // ───────────── Background: keep Discord in step ─────────────

    private async Task RunAsync()
    {
        var delay = TimeSpan.Zero;
        while (!_life.IsCancellationRequested)
        {
            try
            {
                if (await _wake.WaitAsync(delay, _life.Token))
                {
                    // Let a burst of changes (a track change touches several properties) settle into one update.
                    await Task.Delay(400, _life.Token);
                    while (_wake.CurrentCount > 0) await _wake.WaitAsync(_life.Token);
                }
                delay = await StepAsync();
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                delay = RetryAfterError;
            }
        }
    }

    /// <summary>Moves Discord one step closer to what it should show; returns how long to wait before looking again.</summary>
    private async Task<TimeSpan> StepAsync()
    {
        var ct = _life.Token;
        if (!_settings.Current.DiscordPresence || !IsAvailable)
        {
            await DisconnectAsync();
            Report(_settings.Current.DiscordPresence ? DiscordConnection.NotConfigured : DiscordConnection.Off);
            return Timeout.InfiniteTimeSpan;
        }

        if (_client is not { IsConnected: true })
        {
            await DisconnectAsync();
            var wait = _retryAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
            var client = new DiscordIpcClient();
            try
            {
                await client.ConnectAsync(DiscordActivity.ApplicationId, ct);
            }
            catch (DiscordNotRunningException)
            {
                await client.DisposeAsync();
                _retryAt = DateTime.UtcNow + RetryWhenClosed;
                Report(DiscordConnection.Waiting);
                return RetryWhenClosed;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await client.DisposeAsync();
                _retryAt = DateTime.UtcNow + RetryAfterError;
                Report(DiscordConnection.Error, error: ex.Message);
                return RetryAfterError;
            }
            client.Disconnected += _ => _wake.Release();
            client.CommandError += message =>
            {
                Log($"rejected: {message}");
                _dispatcher.TryEnqueue(() => LastError = message);
            };
            client.Reply += reply => Log($"reply: {reply.ToJsonString()}");
            _client = client;
            _sent = null;
            _sends.Clear();
            Report(DiscordConnection.Connected, client.UserName);
        }

        var snap = _snapshot;
        Sent? target;
        var recheck = Timeout.InfiniteTimeSpan;
        if (snap.Track is null)
        {
            target = null;
        }
        else if (!snap.Playing)
        {
            // Hidden when paused (as Spotify does), but not for the blink between two tracks.
            var left = snap.NotPlayingSince + PauseGrace - DateTime.UtcNow;
            if (left > TimeSpan.Zero) return left;
            target = null;
        }
        else
        {
            target = new Sent(snap.Track, snap.StartedAt, snap.Display);
            // Small stalls (buffering) add up without tripping the seek check; look again now and then.
            recheck = TimeSpan.FromSeconds(15);
        }

        if (Same(target, _sent)) return recheck;

        // Stay inside Discord's rate limit; the newest state is sent once there's room.
        var now = DateTime.UtcNow;
        while (_sends.Count > 0 && now - _sends.Peek() > RateWindow) _sends.Dequeue();
        if (_sends.Count >= RateCount) return _sends.Peek() + RateWindow - now + TimeSpan.FromMilliseconds(50);

        var activity = target is null ? null : DiscordActivity.Build(target.Track, target.StartedAt, target.Display);
        Log(activity is null ? "clear" : $"set: {activity.ToJsonString()}");
        await _client!.SetActivityAsync(activity, ct);
        _sends.Enqueue(now);
        _sent = target;
        _dispatcher.TryEnqueue(() => LastError = null);
        return recheck;
    }

    private static bool Same(Sent? a, Sent? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Track == b.Track && a.Display == b.Display
            && Math.Abs((a.StartedAt - b.StartedAt).TotalMilliseconds) <= DriftMs;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null) return;
        var client = _client;
        _client = null;
        _sent = null;
        await client.DisposeAsync();
    }

    private void Report(DiscordConnection connection, string? user = null, string? error = null) =>
        _dispatcher.TryEnqueue(() =>
        {
            Connection = connection;
            UserName = connection == DiscordConnection.Connected ? user : null;
            LastError = error;
        });

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(string line)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("TAPEDECK_DEBUG") != "1") return;
        try
        {
            File.AppendAllText(Path.Combine(SettingsService.DataFolder, "discord.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch
        {
            // Diagnostics only.
        }
#endif
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerChanged;
        _life.Cancel();
        // Closing the pipe is what makes Discord drop the card; don't wait on it at shutdown.
        var client = _client;
        _client = null;
        if (client is not null) _ = client.DisposeAsync().AsTask();
    }

    private sealed record Snapshot(PresenceTrack? Track, bool Playing, DateTimeOffset StartedAt, DateTime NotPlayingSince, DiscordStatusDisplay Display)
    {
        public static readonly Snapshot Empty = new(null, false, DateTimeOffset.UtcNow, DateTime.UtcNow, DiscordStatusDisplay.App);
    }

    private sealed record Sent(PresenceTrack Track, DateTimeOffset StartedAt, DiscordStatusDisplay Display);
}
