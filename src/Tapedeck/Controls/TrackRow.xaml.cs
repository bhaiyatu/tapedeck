using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

/// <summary>
/// A compact row for songs and search results. Clicking opens/plays via <see cref="Nav.Open"/>
/// unless <see cref="HandlesTap"/> is false (track lists play the whole list instead).
/// </summary>
public sealed partial class TrackRow : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(MusicItem), typeof(TrackRow), new PropertyMetadata(null, (d, _) => ((TrackRow)d).Refresh()));

    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(
        nameof(Number), typeof(string), typeof(TrackRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowAlbumProperty = DependencyProperty.Register(
        nameof(ShowAlbum), typeof(bool), typeof(TrackRow), new PropertyMetadata(false, (d, _) => ((TrackRow)d).Refresh()));

    public static readonly DependencyProperty ShowArtProperty = DependencyProperty.Register(
        nameof(ShowArt), typeof(bool), typeof(TrackRow), new PropertyMetadata(true, (d, _) => ((TrackRow)d).Refresh()));

    private bool _subscribed;

    public TrackRow()
    {
        InitializeComponent();
    }

    public MusicItem? Item
    {
        get => (MusicItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public string? Number
    {
        get => (string?)GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public bool ShowAlbum
    {
        get => (bool)GetValue(ShowAlbumProperty);
        set => SetValue(ShowAlbumProperty, value);
    }

    public bool ShowArt
    {
        get => (bool)GetValue(ShowArtProperty);
        set => SetValue(ShowArtProperty, value);
    }

    public bool HandlesTap { get; set; } = true;

    /// <summary>Rows in the queue panel get queue-specific menu items (remove, play next).</summary>
    public bool InQueue { get; set; }

    public Visibility AlbumVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility ArtVisibility { get; private set; } = Visibility.Visible;

    private void Refresh()
    {
        AlbumVisibility = ShowAlbum && Item?.Album is not null ? Visibility.Visible : Visibility.Collapsed;
        ArtVisibility = ShowArt ? Visibility.Visible : Visibility.Collapsed;
        Bindings.Update();
        UpdateNowPlaying();
        if (Item is { } item)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"{item.Title}, {item.Subtitle}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            App.Player.PropertyChanged += OnPlayerChanged;
            _subscribed = true;
        }
        UpdateNowPlaying();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Player.PropertyChanged -= OnPlayerChanged;
            _subscribed = false;
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerService.Current)) UpdateNowPlaying();
    }

    private void UpdateNowPlaying()
    {
        var playing = InQueue
            ? Item is not null && ReferenceEquals(Item, App.Player?.Current)
            : Item?.VideoId is { } id && App.Player?.Current?.VideoId == id;
        PlayingOverlay.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (playing) TitleText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else TitleText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Root.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        MoreButton.Opacity = 1;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        MoreButton.Opacity = 0;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!HandlesTap || MusicCard.IsInside(e.OriginalSource as DependencyObject, MoreButton)) return;
        if (Item is { } item) Nav.Open(item);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Item is not { } item) return;
        Nav.ShowItemMenu(item, this, e.GetPosition(this), InQueue);
        e.Handled = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Item is { } item) Nav.ShowItemMenu(item, MoreButton, inQueue: InQueue);
    }
}
