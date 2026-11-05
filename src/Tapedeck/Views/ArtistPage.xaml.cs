using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Helpers;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Views;

public sealed partial class ArtistPage : Page
{
    private ArtistArgs _args = new("");
    private ArtistDetails? _artist;
    private CancellationTokenSource? _cts;

    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as ArtistArgs ?? new ArtistArgs("");
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _cts?.Cancel();

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        try
        {
            var artist = await App.Api.GetArtistAsync(_args.BrowseId, cts.Token);
            if (cts.IsCancellationRequested) return;
            _artist = artist;

            Banner.Visibility = Visibility.Visible;
            BannerImage.Source = Fmt.ImgSized(artist.ThumbnailUrl, 1080);
            NameText.Text = artist.Name;
            AudienceText.Text = artist.AudienceText ?? "";
            ShuffleButton.Visibility = artist.ShuffleEndpoint is null ? Visibility.Collapsed : Visibility.Visible;
            RadioButton.Visibility = artist.RadioEndpoint is null ? Visibility.Collapsed : Visibility.Visible;
            DescriptionText.Text = artist.Description ?? "";
            DescriptionText.Visibility = string.IsNullOrEmpty(artist.Description) ? Visibility.Collapsed : Visibility.Visible;

            // Top songs: everything else shows as cards, so keep that shelf to a tidy five rows.
            foreach (var shelf in artist.Shelves.Where(s => s.Style == ShelfStyle.List))
                shelf.Items = shelf.Items.Take(6).ToList();
            ShelvesRepeater.ItemsSource = artist.Shelves;
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

    private void OnShuffle(object sender, RoutedEventArgs e)
    {
        if (_artist?.ShuffleEndpoint is { } ep) _ = App.Player.PlayEndpointAsync(ep);
    }

    private void OnRadio(object sender, RoutedEventArgs e)
    {
        if (_artist?.RadioEndpoint is { } ep) _ = App.Player.PlayEndpointAsync(ep);
    }

    private void OnCopyLink(object sender, RoutedEventArgs e) => Nav.CopyLink($"https://music.youtube.com/channel/{_args.BrowseId}");

    private void OnDescriptionTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        DescriptionText.MaxLines = DescriptionText.MaxLines == 0 ? 3 : 0;

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadAsync();
}
