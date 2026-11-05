using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Tapedeck.Services;
using Tapedeck.Views;
using Windows.System;

namespace Tapedeck;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private CancellationTokenSource? _suggestCts;
    private Uri? _interstitial;
    private bool _syncingNav;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Tapedeck";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tapedeck.ico"));

        var s = App.Settings.Current;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(960, s.WindowWidth), Math.Max(640, s.WindowHeight)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                App.Settings.Current.WindowWidth = AppWindow.Size.Width;
                App.Settings.Current.WindowHeight = AppWindow.Size.Height;
                App.Settings.Save();
            }
        };

        ApplyTheme(s.Theme);
        Nav.Frame = ContentFrame;

        Bar.PanelRequested += (_, panel) => ShowPanel(panel);
        App.Player.PropertyChanged += OnPlayerChanged;
        App.Bridge.BlockedByInterstitial += (_, uri) =>
        {
            _interstitial = uri;
            InterstitialBar.IsOpen = true;
        };
        App.Bridge.Ready += (_, _) => InterstitialBar.IsOpen = false;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.IsOpen = false;
        };

        Root.Loaded += OnLoaded;
    }

    public AuthService Auth => App.Auth;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Start the player first: its WebView2 profile also holds the Google session cookies.
        var init = App.Bridge.InitializeAsync(PlayerWebView);
        if (SelfTest.IsRequested) _ = SelfTest.RunAsync();
        DevCommands.Start();
        NavView.SelectedItem = NavView.MenuItems[0];
        ShowPanel(App.Settings.Current.SidePanel);
        try
        {
            await init;
            await App.Auth.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowToast($"The player couldn't start: {ex.Message}. Is the WebView2 runtime installed?", InfoBarSeverity.Error);
        }
    }

    // ───────────────────────── Theme ─────────────────────────

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppTitleBar.RequestedTheme = Root.RequestedTheme;
    }

    // ───────────────────────── Navigation ─────────────────────────

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav) return;
        if (args.IsSettingsSelected)
        {
            Nav.To(typeof(SettingsPage));
            return;
        }
        switch ((args.SelectedItem as NavigationViewItem)?.Tag as string)
        {
            case "home": Nav.ToBrowse("FEmusic_home", null, null); break;
            case "explore": Nav.ToBrowse("FEmusic_explore", null, null); break;
            case "library": Nav.To(typeof(LibraryPage)); break;
            case "liked":
                if (App.Auth.IsSignedIn) Nav.ToCollection("VLLM");
                else _ = SignInAsync();
                break;
            case "charts": Nav.ToBrowse("FEmusic_charts", null, "Charts"); break;
            case "new": Nav.ToBrowse("FEmusic_new_releases", null, "New releases"); break;
            case "moods": Nav.ToBrowse("FEmusic_moods_and_genres", null, "Moods & genres"); break;
        }
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppTitleBar.IsBackButtonEnabled = ContentFrame.CanGoBack;
        // Keep the sidebar highlight in step with back/forward navigation.
        string? tag = e.Parameter switch
        {
            ShelvesArgs { BrowseId: "FEmusic_home" } => "home",
            ShelvesArgs { BrowseId: "FEmusic_explore" } => "explore",
            ShelvesArgs { BrowseId: "FEmusic_charts" } => "charts",
            ShelvesArgs { BrowseId: "FEmusic_new_releases" } => "new",
            ShelvesArgs { BrowseId: "FEmusic_moods_and_genres" } => "moods",
            CollectionArgs { BrowseId: "VLLM" } => "liked",
            _ when e.SourcePageType == typeof(LibraryPage) => "library",
            _ => null,
        };
        _syncingNav = true;
        if (e.SourcePageType == typeof(SettingsPage)) NavView.SelectedItem = NavView.SettingsItem;
        else NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        _syncingNav = false;
    }

    private void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    // ───────────────────────── Search ─────────────────────────

    public void SetSearchText(string text)
    {
        if (SearchBox.Text != text) SearchBox.Text = text;
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _suggestCts?.Cancel();
        var text = sender.Text.Trim();
        if (text.Length < 2)
        {
            sender.ItemsSource = null;
            return;
        }
        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(180, cts.Token);
            var suggestions = await App.Api.GetSuggestionsAsync(text, cts.Token);
            if (!cts.IsCancellationRequested) sender.ItemsSource = suggestions;
        }
        catch (Exception)
        {
            // Suggestions are optional.
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _suggestCts?.Cancel();
        var query = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (query.Length == 0) return;
        sender.ItemsSource = null;
        Nav.ToSearch(query);
    }

    // ───────────────────────── Side panel ─────────────────────────

    private string? _panel;

    private void ShowPanel(string? panel)
    {
        _panel = panel;
        App.Settings.Current.SidePanel = panel;
        App.Settings.Save();
        Bar.SetPanelState(panel);
        SidePanel.Visibility = panel is null ? Visibility.Collapsed : Visibility.Visible;
        QueueView.Visibility = panel == "queue" ? Visibility.Visible : Visibility.Collapsed;
        LyricsView.Visibility = panel == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        LyricsView.IsOpen = panel == "lyrics";
        PanelTabs.SelectedItem = panel == "lyrics" ? LyricsTab : panel == "queue" ? QueueTab : null;
        if (panel == "queue") QueueView.ScrollToCurrent();
    }

    internal void ShowPanelFromDev(string? panel) => ShowPanel(panel);

    private void OnPanelTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        if (tag is not null && tag != _panel) ShowPanel(tag);
    }

    private void OnClosePanel(object sender, RoutedEventArgs e) => ShowPanel(null);

    // ───────────────────────── Account ─────────────────────────

    private void OnAccountMenuOpening(object? sender, object e)
    {
        AccountMenu.Items.Clear();
        if (App.Auth.IsSignedIn)
        {
            AccountMenu.Items.Add(new MenuFlyoutItem { Text = App.Auth.Account?.Name ?? "Signed in", IsEnabled = false });
            if (App.Auth.Account?.Handle is { } handle)
                AccountMenu.Items.Add(new MenuFlyoutItem { Text = handle, IsEnabled = false });
            AccountMenu.Items.Add(new MenuFlyoutSeparator());
            var signOut = new MenuFlyoutItem { Text = "Sign out", Icon = new FontIcon { Glyph = "" } };
            signOut.Click += async (_, _) => await App.Auth.SignOutAsync();
            AccountMenu.Items.Add(signOut);
        }
        else
        {
            var signIn = new MenuFlyoutItem { Text = "Sign in with Google", Icon = new FontIcon { Glyph = "" } };
            signIn.Click += async (_, _) => await SignInAsync();
            AccountMenu.Items.Add(signIn);
        }
        var settings = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => Nav.To(typeof(SettingsPage));
        AccountMenu.Items.Add(settings);
    }

    private LoginWindow? _login;

    public async Task SignInAsync()
    {
        if (_login is not null)
        {
            _login.Activate();
            return;
        }
        _login = new LoginWindow();
        _login.Activate();
        try
        {
            if (await _login.Result) ShowToast($"Signed in{(App.Auth.Account is { } a ? " as " + a.Name : "")}", InfoBarSeverity.Success);
        }
        finally
        {
            _login = null;
        }
    }

    private void OnOpenInterstitial(object sender, RoutedEventArgs e)
    {
        InterstitialBar.IsOpen = false;
        var window = new LoginWindow(_interstitial?.ToString() ?? "https://music.youtube.com/");
        window.Activate();
    }

    // ───────────────────────── Status toasts ─────────────────────────

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerService.StatusMessage) && App.Player.StatusMessage is { Length: > 0 } msg)
            ShowToast(msg, msg.StartsWith("Can't", StringComparison.Ordinal) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        else if (e.PropertyName == nameof(PlayerService.Current))
            Title = App.Player.Current is { } t ? $"{t.Title} · {t.ArtistText} — Tapedeck" : "Tapedeck";
    }

    public void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ───────────────────────── Keyboard ─────────────────────────

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        // Let text boxes type spaces and buttons / list items activate as usual.
        if (focused is TextBox or RichEditBox or PasswordBox or ButtonBase or ListViewItem or GridViewItem or Slider or ComboBox) return;
        App.Player.TogglePlayPause();
        e.Handled = true;
    }

    private void OnAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var player = App.Player;
        switch (sender.Key)
        {
            case VirtualKey.Right: player.Next(); break;
            case VirtualKey.Left: player.Previous(); break;
            case VirtualKey.Up: player.IsMuted = false; player.Volume = Math.Min(1, player.Volume + 0.05); break;
            case VirtualKey.Down: player.Volume = Math.Max(0, player.Volume - 0.05); break;
            case VirtualKey.F: SearchBox.Focus(FocusState.Keyboard); break;
            case VirtualKey.L: _ = player.ToggleLikeAsync(); break;
            case VirtualKey.S: player.ToggleShuffle(); break;
            case VirtualKey.R: player.CycleRepeat(); break;
        }
        args.Handled = true;
    }
}
