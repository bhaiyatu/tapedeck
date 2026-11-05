using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

/// <summary>Renders one <see cref="Shelf"/> as a carousel, a grid of rows, mood chips, or text.</summary>
public sealed partial class ShelfView : UserControl
{
    public static readonly DependencyProperty ShelfProperty = DependencyProperty.Register(
        nameof(Shelf), typeof(Shelf), typeof(ShelfView), new PropertyMetadata(null, (d, _) => ((ShelfView)d).Render()));

    public ShelfView()
    {
        InitializeComponent();
    }

    public Shelf? Shelf
    {
        get => (Shelf?)GetValue(ShelfProperty);
        set => SetValue(ShelfProperty, value);
    }

    /// <summary>Optional: called for "See all" on search shelves (needs the query, which the page knows).</summary>
    public static Action<Shelf>? SearchMoreHandler { get; set; }

    /// <summary>Titled row shelves start collapsed to this many items, with a "Show all" toggle.</summary>
    private const int CollapsedRows = 8;

    private bool _expanded;

    private bool CanCollapse(Shelf shelf) =>
        shelf.Style == ShelfStyle.List && shelf.Title.Length > 0 && shelf.Items.Count > CollapsedRows
        && shelf.MoreBrowseId is null && shelf.MoreSearchParams is null;

    private void Render()
    {
        var shelf = Shelf;
        Carousel.Visibility = GridRepeater.Visibility = RowsRepeater.Visibility = ChipsRepeater.Visibility = BodyText.Visibility = Visibility.Collapsed;
        LeftButton.Visibility = RightButton.Visibility = MoreButton.Visibility = Visibility.Collapsed;
        if (shelf is null) return;

        TitleText.Text = shelf.Title;
        Header.Visibility = string.IsNullOrEmpty(shelf.Title) ? Visibility.Collapsed : Visibility.Visible;
        StraplineText.Text = shelf.Strapline?.ToUpperInvariant() ?? "";
        StraplineText.Visibility = string.IsNullOrEmpty(shelf.Strapline) ? Visibility.Collapsed : Visibility.Visible;
        _expanded = false;
        MoreButton.Content = CanCollapse(shelf) ? "Show all" : "See all";
        MoreButton.Visibility = shelf.MoreBrowseId is not null || (shelf.MoreSearchParams is not null && SearchMoreHandler is not null) || CanCollapse(shelf)
            ? Visibility.Visible : Visibility.Collapsed;

        switch (shelf.Style)
        {
            case ShelfStyle.Cards:
                CardsRepeater.ItemsSource = shelf.Items;
                Carousel.Visibility = Visibility.Visible;
                Carousel.ChangeView(0, null, null, true);
                UpdateArrows();
                break;
            case ShelfStyle.Grid:
                GridRepeater.ItemsSource = shelf.Items;
                GridRepeater.Visibility = Visibility.Visible;
                break;
            case ShelfStyle.List:
                RowsRepeater.ItemsSource = CanCollapse(shelf) ? shelf.Items.Take(CollapsedRows).ToList() : shelf.Items;
                RowsRepeater.Visibility = Visibility.Visible;
                break;
            case ShelfStyle.Chips:
                ChipsRepeater.ItemsSource = shelf.Items;
                ChipsRepeater.Visibility = Visibility.Visible;
                break;
            case ShelfStyle.Text:
                BodyText.Text = shelf.Text ?? "";
                BodyText.Visibility = Visibility.Visible;
                break;
        }
    }

    private void UpdateArrows()
    {
        if (Shelf?.Style != ShelfStyle.Cards) return;
        var canScroll = Carousel.ScrollableWidth > 1;
        LeftButton.Visibility = RightButton.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
        LeftButton.IsEnabled = Carousel.HorizontalOffset > 1;
        RightButton.IsEnabled = Carousel.HorizontalOffset < Carousel.ScrollableWidth - 1;
    }

    private void OnCarouselViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateArrows();

    private void OnCarouselSizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();

    private void OnScrollLeft(object sender, RoutedEventArgs e) =>
        Carousel.ChangeView(Math.Max(0, Carousel.HorizontalOffset - Carousel.ViewportWidth * 0.85), null, null);

    private void OnScrollRight(object sender, RoutedEventArgs e) =>
        Carousel.ChangeView(Math.Min(Carousel.ScrollableWidth, Carousel.HorizontalOffset + Carousel.ViewportWidth * 0.85), null, null);

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Shelf is not { } shelf) return;
        if (CanCollapse(shelf))
        {
            _expanded = !_expanded;
            RowsRepeater.ItemsSource = _expanded ? shelf.Items : shelf.Items.Take(CollapsedRows).ToList();
            MoreButton.Content = _expanded ? "Show less" : "Show all";
            return;
        }
        if (shelf.MoreSearchParams is not null && SearchMoreHandler is not null)
        {
            SearchMoreHandler(shelf);
            return;
        }
        if (shelf.MoreBrowseId is null) return;
        if (shelf.MoreBrowseId.StartsWith("VL", StringComparison.Ordinal) || shelf.MoreBrowseId.StartsWith("MPRE", StringComparison.Ordinal))
            Nav.ToCollection(shelf.MoreBrowseId);
        else
            Nav.ToBrowse(shelf.MoreBrowseId, shelf.MoreParams, shelf.Title);
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MusicItem item }) Nav.Open(item);
    }

    private void OnBodyTapped(object sender, TappedRoutedEventArgs e) =>
        BodyText.MaxLines = BodyText.MaxLines == 0 ? 4 : 0;
}
