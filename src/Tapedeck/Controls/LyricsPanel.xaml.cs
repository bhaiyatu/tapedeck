using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

public sealed partial class LyricLineView : ObservableObject
{
    public double Time { get; init; }
    public string Text { get; init; } = "";

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>Shows synced lyrics (highlighting and following the current line) or plain lyrics.</summary>
public sealed partial class LyricsPanel : UserControl
{
    private List<LyricLineView> _lines = [];
    private int _active = -1;
    private string? _loadedFor;
    private CancellationTokenSource? _cts;
    private bool _isOpen;

    public LyricsPanel()
    {
        InitializeComponent();
        App.Player.PropertyChanged += OnPlayerChanged;
    }

    /// <summary>Lyrics only load while the panel is open, to avoid needless lookups.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            _isOpen = value;
            if (value) _ = LoadAsync();
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Current):
            case nameof(PlayerService.TrackInfoLoaded):
                if (IsOpen) _ = LoadAsync();
                break;
            case nameof(PlayerService.Position):
                UpdateActiveLine();
                break;
        }
    }

    private async Task LoadAsync()
    {
        var track = App.Player.Current;
        if (track is null)
        {
            ShowStatus("Lyrics show up here while a song plays.");
            _loadedFor = null;
            return;
        }

        // Lookups run once the track's watch info is in: it carries the album, duration and the
        // YouTube Music lyrics id used as the fallback source.
        var infoReady = App.Player.TrackInfoLoaded;
        var key = $"{track.VideoId}|{infoReady}";
        if (_loadedFor == key) return;
        _loadedFor = key;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ShowStatus(null, loading: true);
        if (!infoReady) return;
        try
        {
            var lyrics = await App.Lyrics.GetAsync(track, App.Player.LyricsBrowseId, App.Player.Duration, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(track, App.Player.Current)) return;
            if (lyrics is null)
            {
                ShowStatus("No lyrics for this song.");
                return;
            }
            Show(lyrics);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowStatus(ex.Message);
        }
    }

    private void Show(Lyrics lyrics)
    {
        StatusPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        SourceText.Text = lyrics.Source is null ? "" : lyrics.Source.StartsWith("Source", StringComparison.OrdinalIgnoreCase) ? lyrics.Source : $"Lyrics from {lyrics.Source}";

        if (lyrics.IsSynced)
        {
            _lines = lyrics.Lines.Select(l => new LyricLineView { Time = l.Time, Text = l.Text.Length == 0 ? "♪" : l.Text }).ToList();
            _active = -1;
            LinesList.ItemsSource = _lines;
            LinesList.Visibility = Visibility.Visible;
            PlainScroller.Visibility = Visibility.Collapsed;
            UpdateActiveLine();
        }
        else
        {
            _lines = [];
            PlainText.Text = lyrics.PlainText ?? "";
            PlainScroller.Visibility = Visibility.Visible;
            PlainScroller.ChangeView(null, 0, null, true);
            LinesList.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(string? message, bool loading = false)
    {
        LinesList.Visibility = PlainScroller.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = loading;
        StatusIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = message ?? "";
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        SourceText.Text = "";
    }

    private void UpdateActiveLine()
    {
        if (_lines.Count == 0) return;
        // Small lead so the highlight lands as the line starts, not after.
        var t = App.Player.Position + 0.25;
        var idx = -1;
        int lo = 0, hi = _lines.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[mid].Time <= t) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (idx == _active) return;
        if (_active >= 0 && _active < _lines.Count) _lines[_active].IsActive = false;
        _active = idx;
        if (idx < 0) return;
        _lines[idx].IsActive = true;
        if (IsOpen) LinesList.ScrollIntoView(_lines[Math.Max(0, idx - 3)], ScrollIntoViewAlignment.Leading);
    }

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LyricLineView line) App.Player.Seek(line.Time);
    }
}
