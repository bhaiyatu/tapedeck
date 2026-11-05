using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Controls;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Views;

public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<Shelf> _shelves = [];
    private SearchArgs _args = new("");
    private string? _continuation;
    private Shelf? _pagedShelf;
    private bool _loadingMore;
    private CancellationTokenSource? _cts;

    public SearchPage()
    {
        InitializeComponent();
        ShelvesRepeater.ItemsSource = _shelves;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as SearchArgs ?? new SearchArgs("");
        TitleText.Text = $"Results for “{_args.Query}”";
        ShelfView.SearchMoreHandler = shelf => Nav.ToSearch(_args.Query, shelf.MoreSearchParams);
        App.MainWindow.SetSearchText(_args.Query);
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _cts?.Cancel();
        ShelfView.SearchMoreHandler = null;
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _shelves.Clear();
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        try
        {
            var results = await App.Api.SearchAsync(_args.Query, _args.FilterParams, cts.Token);
            if (cts.IsCancellationRequested) return;
            BuildFilters(results.Filters);

            var shelves = results.Shelves.Where(s => s.Items.Count > 0 || s.Style == ShelfStyle.Text).ToList();
            // Filtered results arrive as one long list: show it full width and page it in.
            if (_args.FilterParams is not null && shelves.Count == 1)
            {
                shelves[0].Title = "";
                _pagedShelf = shelves[0];
            }
            foreach (var s in shelves) _shelves.Add(s);
            _continuation = results.Continuation;

            if (_shelves.Count == 0)
            {
                EmptyText.Text = $"No results for “{_args.Query}”. Check the spelling or try different keywords.";
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                EmptyText.Text = ex.Message;
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested) LoadingRing.IsActive = false;
        }
    }

    private void BuildFilters(List<Chip> filters)
    {
        FilterPanel.Children.Clear();
        var all = new ToggleButton { Content = "All", IsChecked = _args.FilterParams is null, Style = (Style)Application.Current.Resources["ChipToggleStyle"] };
        all.Click += (_, _) => Nav.ToSearch(_args.Query);
        FilterPanel.Children.Add(all);
        foreach (var f in filters)
        {
            var selected = f.IsSelected || (f.Params is not null && f.Params == _args.FilterParams);
            var chip = new ToggleButton { Content = f.Text, IsChecked = selected, Style = (Style)Application.Current.Resources["ChipToggleStyle"] };
            // A selected chip's own endpoint means "clear filter" in YouTube Music.
            chip.Click += (_, _) =>
            {
                if (selected) Nav.ToSearch(_args.Query);
                else Nav.ToSearch(_args.Query, f.Params);
            };
            FilterPanel.Children.Add(chip);
        }
    }

    private async void OnScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_loadingMore || _continuation is null || _pagedShelf is null) return;
        if (Scroller.VerticalOffset < Scroller.ScrollableHeight - 600) return;
        _loadingMore = true;
        MoreRing.IsActive = true;
        try
        {
            var more = await App.Api.ContinueSearchAsync(_continuation, _cts?.Token ?? default);
            var items = more.Shelves.SelectMany(s => s.Items).ToList();
            _continuation = items.Count > 0 ? more.Continuation : null;
            if (items.Count > 0)
            {
                // Append as its own untitled shelf so the existing one doesn't re-render.
                _shelves.Add(new Shelf { Style = ShelfStyle.List, Items = items });
            }
        }
        catch (Exception)
        {
            _continuation = null;
        }
        finally
        {
            _loadingMore = false;
            MoreRing.IsActive = false;
        }
    }
}
