using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Views;

/// <summary>Home, Explore, mood/genre and "See all" pages: a vertical list of shelves.</summary>
public sealed partial class ShelvesPage : Page
{
    private sealed class CachedPage
    {
        public required List<Shelf> Shelves { get; init; }
        public required List<Chip> Chips { get; init; }
        public string? Continuation { get; set; }
        public string? Title { get; set; }
        public double ScrollOffset { get; set; }
        public DateTime LoadedAt { get; } = DateTime.UtcNow;
    }

    private static readonly Dictionary<ShelvesArgs, CachedPage> Cache = [];

    private readonly ObservableCollection<Shelf> _shelves = [];
    private ShelvesArgs _args = new("FEmusic_home");
    private string? _chipParams;
    private string? _continuation;
    private bool _loadingMore;
    private CancellationTokenSource? _cts;

    public ShelvesPage()
    {
        InitializeComponent();
        ShelvesRepeater.ItemsSource = _shelves;
        App.Auth.SessionChanged += OnSessionChanged;
        Unloaded += (_, _) => App.Auth.SessionChanged -= OnSessionChanged;
    }

    public static void ClearCache() => Cache.Clear();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as ShelvesArgs ?? new ShelvesArgs("FEmusic_home");
        TitleText.Text = _args.Title ?? DefaultTitle(_args.BrowseId);
        _ = LoadAsync(useCache: true);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        _cts?.Cancel();
        if (Cache.TryGetValue(CacheKey, out var cached)) cached.ScrollOffset = Scroller.VerticalOffset;
    }

    private ShelvesArgs CacheKey => _args with { Params = _chipParams ?? _args.Params };

    private static string DefaultTitle(string browseId) => browseId switch
    {
        "FEmusic_home" => GreetingTitle(),
        "FEmusic_explore" => "Explore",
        "FEmusic_new_releases" => "New releases",
        "FEmusic_charts" => "Charts",
        "FEmusic_moods_and_genres" => "Moods & genres",
        _ => "",
    };

    private static string GreetingTitle()
    {
        var name = App.Auth.Account?.Name?.Split(' ').FirstOrDefault();
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        return string.IsNullOrEmpty(name) ? greeting : $"{greeting}, {name}";
    }

    private async Task LoadAsync(bool useCache)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ErrorPanel.Visibility = Visibility.Collapsed;

        if (useCache && Cache.TryGetValue(CacheKey, out var cached) && DateTime.UtcNow - cached.LoadedAt < TimeSpan.FromMinutes(10))
        {
            Show(cached.Shelves, cached.Chips, cached.Continuation, cached.Title);
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, cached.ScrollOffset, null, true);
            return;
        }

        _shelves.Clear();
        LoadingRing.IsActive = true;
        try
        {
            var page = await App.Api.GetShelvesAsync(_args.BrowseId, _chipParams ?? _args.Params, cts.Token);
            if (cts.IsCancellationRequested) return;
            Cache[CacheKey] = new CachedPage
            {
                Shelves = page.Shelves,
                Chips = page.Chips,
                Continuation = page.Continuation,
                Title = page.Title,
            };
            Show(page.Shelves, page.Chips, page.Continuation, page.Title);
            if (page.Shelves.Count == 0) ShowError("Nothing to show here yet.");
            // Short first pages (Home) look empty; pull one more batch straight away.
            if (_continuation is not null && _shelves.Count < 6) await LoadMoreAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowError(ex.Message);
        }
        finally
        {
            if (!cts.IsCancellationRequested) LoadingRing.IsActive = false;
        }
    }

    private void Show(List<Shelf> shelves, List<Chip> chips, string? continuation, string? title)
    {
        _shelves.Clear();
        foreach (var s in shelves) _shelves.Add(s);
        _continuation = continuation;
        if (_args.Title is null && !string.IsNullOrEmpty(title)) TitleText.Text = title;
        if (_args.BrowseId == "FEmusic_home") TitleText.Text = GreetingTitle();
        ChipsRepeater.ItemsSource = chips;
        ChipsScroller.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = false;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _continuation is null) return;
        _loadingMore = true;
        MoreRing.IsActive = true;
        var token = _continuation;
        try
        {
            var more = await App.Api.ContinueShelvesAsync(token, _cts?.Token ?? default);
            foreach (var s in more.Shelves) _shelves.Add(s);
            _continuation = more.Continuation;
            if (Cache.TryGetValue(CacheKey, out var cached))
            {
                cached.Shelves.AddRange(more.Shelves);
                cached.Continuation = more.Continuation;
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

    private void OnScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Scroller.VerticalOffset > Scroller.ScrollableHeight - 600) _ = LoadMoreAsync();
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: Chip chip } button) return;
        // Clicking the active chip clears the filter.
        _chipParams = button.IsChecked == true ? chip.Params : null;
        _ = LoadAsync(useCache: true);
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = LoadAsync(useCache: false);

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        ClearCache();
        _ = LoadAsync(useCache: false);
    }
}
