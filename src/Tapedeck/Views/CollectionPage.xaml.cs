using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Helpers;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Views;

/// <summary>Album, playlist and podcast pages.</summary>
public sealed partial class CollectionPage : Page
{
    private const int AutoLoadLimit = 1000;

    private readonly ObservableCollection<MusicItem> _tracks = [];
    private CollectionArgs _args = new("");
    private CollectionDetails? _details;
    private CancellationTokenSource? _cts;

    public CollectionPage()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as CollectionArgs ?? new CollectionArgs("");
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _cts?.Cancel();

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ErrorPanel.Visibility = Visibility.Collapsed;
        HeaderGrid.Visibility = Visibility.Collapsed;
        _tracks.Clear();
        LoadingRing.IsActive = true;
        try
        {
            var details = await App.Api.GetCollectionAsync(_args.BrowseId, cts.Token);
            if (cts.IsCancellationRequested) return;
            _details = details;
            ShowHeader(details);
            TrackList.ItemTemplate = (DataTemplate)Resources[details.IsAlbum ? "AlbumTrackTemplate" : "PlaylistTrackTemplate"];
            foreach (var t in details.Tracks) _tracks.Add(t);
            RelatedRepeater.ItemsSource = details.Related;
            LoadingRing.IsActive = false;
            UpdateFooter();
            await LoadRemainingAsync(details, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                ErrorText.Text = ex.Message;
                ErrorPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested) LoadingRing.IsActive = false;
        }
    }

    /// <summary>Long playlists come in pages of ~100; fetch the rest in the background.</summary>
    private async Task LoadRemainingAsync(CollectionDetails details, CancellationToken ct)
    {
        var token = details.Continuation;
        if (token is null) return;
        MoreRing.IsActive = true;
        try
        {
            while (token is not null && _tracks.Count < AutoLoadLimit && !ct.IsCancellationRequested)
            {
                var (items, next) = await App.Api.ContinueCollectionAsync(token, ct);
                if (ct.IsCancellationRequested || items.Count == 0) break;
                foreach (var t in items)
                {
                    if (t.Artists.Count == 0) t.Artists.AddRange(details.Artists);
                    _tracks.Add(t);
                    details.Tracks.Add(t);
                }
                App.Player.AppendFromSource(_args.BrowseId, items);
                token = next;
                UpdateFooter();
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Partial lists are still playable.
        }
        finally
        {
            MoreRing.IsActive = false;
        }
    }

    private void ShowHeader(CollectionDetails d)
    {
        HeaderGrid.Visibility = Visibility.Visible;
        ArtImage.Source = Fmt.ImgSized(d.ThumbnailUrl, 232);
        var parts = d.Subtitle.Split(" • ", 2);
        KindText.Text = parts[0].ToUpperInvariant();
        SubtitleText.Text = string.Join(" • ", new[] { parts.Length > 1 ? parts[1] : null, d.SecondSubtitle }.Where(s => !string.IsNullOrEmpty(s)));
        TitleText.Text = d.Title;
        DescriptionText.Text = d.Description ?? "";
        DescriptionText.Visibility = string.IsNullOrEmpty(d.Description) ? Visibility.Collapsed : Visibility.Visible;

        ArtistsPanel.Children.Clear();
        foreach (var a in d.Artists)
        {
            if (a.Id is null)
            {
                ArtistsPanel.Children.Add(new TextBlock { Text = a.Name, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                continue;
            }
            var link = new HyperlinkButton { Content = a.Name, Padding = new Thickness(11, 4, 11, 4) };
            link.Click += (_, _) => Nav.ToArtist(a.Id);
            ArtistsPanel.Children.Add(link);
        }
    }

    private void UpdateFooter()
    {
        if (_details is null) return;
        var total = _tracks.Sum(t => t.DurationSeconds);
        FooterText.Text = _tracks.Count == 0 ? "" : $"{_tracks.Count} {(_tracks.Count == 1 ? "song" : "songs")} • {Formatting.Time(total)}";
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MusicItem item) return;
        var index = _tracks.IndexOf(item);
        App.Player.PlayList(_tracks, index, _details?.PlaylistId, sourceId: _args.BrowseId);
    }

    private void OnPlayAll(object sender, RoutedEventArgs e) => App.Player.PlayList(_tracks, 0, _details?.PlaylistId, sourceId: _args.BrowseId);

    private void OnShuffle(object sender, RoutedEventArgs e) => App.Player.PlayList(_tracks, 0, _details?.PlaylistId, shuffle: true, sourceId: _args.BrowseId);

    private void OnPlayNext(object sender, RoutedEventArgs e) => App.Player.AddToQueue(_tracks, next: true);

    private void OnAddToQueue(object sender, RoutedEventArgs e) => App.Player.AddToQueue(_tracks, next: false);

    private string Link => _details?.PlaylistId is { } pl
        ? $"https://music.youtube.com/playlist?list={pl}"
        : $"https://music.youtube.com/browse/{_args.BrowseId}";

    private void OnCopyLink(object sender, RoutedEventArgs e) => Nav.CopyLink(Link);

    private async void OnOpenInBrowser(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri(Link));

    private void OnDescriptionTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        DescriptionText.MaxLines = DescriptionText.MaxLines == 0 ? 3 : 0;

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadAsync();
}
