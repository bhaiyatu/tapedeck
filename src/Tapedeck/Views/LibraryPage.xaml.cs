using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Models;

namespace Tapedeck.Views;

public sealed partial class LibraryPage : Page
{
    private static string _lastTab = "FEmusic_liked_playlists";
    private List<Shelf> _shelves = [];
    private CancellationTokenSource? _cts;

    public LibraryPage()
    {
        InitializeComponent();
        App.Auth.SessionChanged += OnSessionChanged;
        Unloaded += (_, _) => App.Auth.SessionChanged -= OnSessionChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        SyncTabs();
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _cts?.Cancel();

    private string CurrentTab => _lastTab;

    private void SyncTabs()
    {
        foreach (var chip in Tabs.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
            chip.IsChecked = (string)chip.Tag == _lastTab;
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) _lastTab = tag;
        SyncTabs();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        MessagePanel.Visibility = Visibility.Collapsed;
        ShelvesRepeater.ItemsSource = null;
        PlayAllButton.Visibility = Visibility.Collapsed;

        if (!App.Auth.IsSignedIn)
        {
            SignInPanel.Visibility = Visibility.Visible;
            Tabs.Visibility = Visibility.Collapsed;
            return;
        }
        SignInPanel.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;

        LoadingRing.IsActive = true;
        try
        {
            var page = await App.Api.GetShelvesAsync(CurrentTab, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            _shelves = page.Shelves;
            ShelvesRepeater.ItemsSource = _shelves;
            PlayAllButton.Visibility = _shelves.Any(s => s.Items.Any(i => i.VideoId is not null)) ? Visibility.Visible : Visibility.Collapsed;
            if (_shelves.Count == 0)
            {
                MessageText.Text = "Nothing here yet. Things you like and save on YouTube Music show up here.";
                MessagePanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                MessageText.Text = ex.Message;
                MessagePanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested) LoadingRing.IsActive = false;
        }
    }

    private void OnPlayAll(object sender, RoutedEventArgs e) =>
        App.Player.PlayList(_shelves.SelectMany(s => s.Items).Where(i => i.VideoId is not null));

    private async void OnSignIn(object sender, RoutedEventArgs e) => await App.MainWindow.SignInAsync();

    private void OnSessionChanged(object? sender, EventArgs e) => _ = LoadAsync();
}
