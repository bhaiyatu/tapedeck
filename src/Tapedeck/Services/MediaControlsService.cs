using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Tapedeck.Models;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Tapedeck.Services;

/// <summary>
/// Publishes the current track to Windows' System Media Transport Controls: media keys,
/// the volume flyout / quick settings media card, and the lock screen.
/// </summary>
public sealed class MediaControlsService : IDisposable
{
    private readonly PlayerService _player;
    private readonly DispatcherQueue _dispatcher;
    private readonly MediaPlayer _mediaPlayer;
    private readonly SystemMediaTransportControls _smtc;
    private DateTime _lastTimelineUpdate;

    public MediaControlsService(PlayerService player, DispatcherQueue dispatcher)
    {
        _player = player;
        _dispatcher = dispatcher;

        // An unpackaged desktop app gets its SMTC instance through a MediaPlayer whose own
        // command handling is disabled; we drive the controls ourselves.
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.CommandManager.IsEnabled = false;
        _smtc = _mediaPlayer.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = false;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += (_, e) =>
            _dispatcher.TryEnqueue(() => _player.Seek(e.RequestedPlaybackPosition.TotalSeconds));

        _player.PropertyChanged += OnPlayerChanged;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: _player.Play(); break;
                case SystemMediaTransportControlsButton.Pause: _player.Pause(); break;
                case SystemMediaTransportControlsButton.Next: _player.Next(); break;
                case SystemMediaTransportControlsButton.Previous: _player.Previous(); break;
            }
        });
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Current):
            case nameof(PlayerService.TrackInfoLoaded): // album / artwork may arrive with the watch info
                UpdateMetadata(_player.Current);
                break;
            case nameof(PlayerService.IsPlaying):
                _smtc.PlaybackStatus = _player.Current is null ? MediaPlaybackStatus.Closed
                    : _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                UpdateTimeline(force: true);
                break;
            case nameof(PlayerService.Position):
            case nameof(PlayerService.Duration):
                UpdateTimeline(force: false);
                break;
        }
    }

    private void UpdateMetadata(MusicItem? track)
    {
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        if (track is null)
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            updater.Update();
            return;
        }
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.ArtistText;
        updater.MusicProperties.AlbumTitle = track.Album?.Name ?? "";
        if (Formatting.SizedThumbnail(track.ThumbnailUrl, 512) is { } art && Uri.TryCreate(art, UriKind.Absolute, out var uri))
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        updater.Update();
        UpdateTimeline(force: true);
    }

    private void UpdateTimeline(bool force)
    {
        if (!force && DateTime.UtcNow - _lastTimelineUpdate < TimeSpan.FromSeconds(1)) return;
        _lastTimelineUpdate = DateTime.UtcNow;
        var duration = Math.Max(0, _player.Duration);
        var props = new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(duration),
            MaxSeekTime = TimeSpan.FromSeconds(duration),
            Position = TimeSpan.FromSeconds(Math.Clamp(_player.Position, 0, duration)),
        };
        _smtc.UpdateTimelineProperties(props);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerChanged;
        _smtc.DisplayUpdater.ClearAll();
        _smtc.IsEnabled = false;
        _mediaPlayer.Dispose();
    }
}
