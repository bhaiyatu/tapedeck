using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
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
using Windows.UI;

namespace Tapedeck;

public sealed partial class MainWindow : Window
{
    private sealed record NavEntry(string Tag, string Label, string Glyph, Button Button, FontIcon Icon, TextBlock Text);

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly List<NavEntry> _nav = [];
    private readonly List<TextBlock> _sectionLabels = [];
    private CancellationTokenSource? _suggestCts;
    private Uri? _interstitial;
    private string? _panel;
    private bool _compactSidebar;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Tapedeck";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tapedeck.ico"));

        var s = App.Settings.Current;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(1000, s.WindowWidth), Math.Max(680, s.WindowHeight)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 620;
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

        BuildSidebar();
        ApplyTheme(s.Theme);
        Nav.Frame = ContentFrame;

        Bar.PanelRequested += (_, panel) => ShowPanel(panel);
        App.Player.PropertyChanged += OnPlayerChanged;
        App.Auth.PropertyChanged += (_, _) => UpdateAccountCard();
        App.Auth.SessionExpired += (_, _) =>
            ShowToast("Your Google sign-in has expired. Sign in again to see your library.", InfoBarSeverity.Warning);
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
        UpdateAccountCard();

        Root.Loaded += OnLoaded;
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
    }

    public AuthService Auth => App.Auth;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCaptionButtons();
        // Start the player first: its WebView2 profile also holds the Google session cookies.
        var init = App.Bridge.InitializeAsync(PlayerWebView);
        if (SelfTest.IsRequested) _ = SelfTest.RunAsync();
#if DEBUG
        DevCommands.Start();
#endif
        Navigate("home");
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

    // ───────────────────────── Sidebar ─────────────────────────

    private void BuildSidebar()
    {
        AddSection("Discover");
        AddNav("home", "Home", "");
        AddNav("explore", "Explore", "");
        AddNav("charts", "Charts", "");
        AddNav("moods", "Moods & genres", "");
        AddNav("new", "New releases", "");
        AddNav("podcasts", "Podcasts", "");
        AddSection("Collection");
        AddNav("library", "Library", "");
        AddNav("liked", "Liked music", "");
        AddNav("history", "History", "");
        AddSection("App");
        AddNav("settings", "Settings", "");
    }

    private void AddSection(string title)
    {
        var label = new TextBlock { Text = title.ToUpperInvariant(), Style = (Style)Application.Current.Resources["SectionLabelStyle"] };
        _sectionLabels.Add(label);
        NavPanel.Children.Add(label);
    }

    private void AddNav(string tag, string label, string glyph)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 15 };
        var text = new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(icon);
        content.Children.Add(text);
        var button = new Button { Content = content, Tag = tag, Style = (Style)Application.Current.Resources["NavButtonStyle"] };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => Navigate(tag);
        NavPanel.Children.Add(button);
        _nav.Add(new NavEntry(tag, label, glyph, button, icon, text));
    }

    private void Navigate(string tag)
    {
        switch (tag)
        {
            case "home": Nav.ToBrowse("FEmusic_home", null, null); break;
            case "explore": Nav.ToBrowse("FEmusic_explore", null, null); break;
            case "charts": Nav.ToBrowse("FEmusic_charts", null, "Charts"); break;
            case "moods": Nav.ToBrowse("FEmusic_moods_and_genres", null, "Moods & genres"); break;
            case "new": Nav.ToBrowse("FEmusic_new_releases", null, "New releases"); break;
            case "podcasts": Nav.ToBrowse("FEmusic_non_music_audio", null, "Podcasts"); break;
            case "library": Nav.To(typeof(LibraryPage)); break;
            case "history":
                if (App.Auth.IsSignedIn) Nav.ToBrowse("FEmusic_history", null, "History");
                else _ = SignInAsync();
                break;
            case "liked":
                if (App.Auth.IsSignedIn) Nav.ToCollection("VLLM");
                else _ = SignInAsync();
                break;
            case "settings": Nav.To(typeof(SettingsPage)); break;
        }
    }

    private void HighlightNav(string? tag)
    {
        var accent = (Brush)Application.Current.Resources["BrandGradient"];
        foreach (var entry in _nav)
        {
            var selected = entry.Tag == tag;
            if (selected)
            {
                entry.Button.Background = (Brush)Root.Resources.ThemeLookup("SurfaceBackground");
                entry.Button.Foreground = (Brush)Root.Resources.ThemeLookup("TextStrong");
                entry.Icon.Foreground = accent;
                entry.Text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                entry.Button.ClearValue(Control.BackgroundProperty);
                entry.Button.ClearValue(Control.ForegroundProperty);
                entry.Icon.ClearValue(IconElement.ForegroundProperty);
                entry.Text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args)
    {
        _compactSidebar = !_compactSidebar;
        SidebarColumn.Width = new GridLength(_compactSidebar ? 76 : 220);
        foreach (var entry in _nav)
        {
            entry.Text.Visibility = _compactSidebar ? Visibility.Collapsed : Visibility.Visible;
            entry.Button.HorizontalContentAlignment = _compactSidebar ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        foreach (var label in _sectionLabels) label.Visibility = _compactSidebar ? Visibility.Collapsed : Visibility.Visible;
        AccountText.Visibility = AccountChevron.Visibility = _compactSidebar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateAccountCard()
    {
        if (App.Auth.IsSignedIn)
        {
            AccountName.Text = App.Auth.Account?.Name ?? "Signed in";
            AccountDetail.Text = App.Auth.Account?.Handle ?? "Google account";
        }
        else
        {
            AccountName.Text = "Sign in";
            AccountDetail.Text = "Library & mixes";
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
        UpdateCaptionButtons();
        HighlightNav(CurrentNavTag);
    }

    /// <summary>Caption buttons are drawn by Windows; match them to the app theme.</summary>
    private void UpdateCaptionButtons()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Color.FromArgb(255, 244, 244, 245) : Color.FromArgb(255, 10, 10, 12);
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 107, 107, 117) : Color.FromArgb(255, 154, 154, 164);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0);
        HighlightNav(CurrentNavTag);
    }

    // ───────────────────────── Navigation ─────────────────────────

    private string? CurrentNavTag { get; set; }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppTitleBar.IsBackButtonEnabled = ContentFrame.CanGoBack;
        CurrentNavTag = e.Parameter switch
        {
            ShelvesArgs { BrowseId: "FEmusic_home" } => "home",
            ShelvesArgs { BrowseId: "FEmusic_explore" } => "explore",
            ShelvesArgs { BrowseId: "FEmusic_charts" } => "charts",
            ShelvesArgs { BrowseId: "FEmusic_new_releases" } => "new",
            ShelvesArgs { BrowseId: "FEmusic_moods_and_genres" } => "moods",
            ShelvesArgs { BrowseId: "FEmusic_non_music_audio" } => "podcasts",
            ShelvesArgs { BrowseId: "FEmusic_history" } => "history",
            CollectionArgs { BrowseId: "VLLM" } => "liked",
            _ when e.SourcePageType == typeof(LibraryPage) => "library",
            _ when e.SourcePageType == typeof(SettingsPage) => "settings",
            _ => null,
        };
        HighlightNav(CurrentNavTag);
    }

    private void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

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
        QueueTab.IsChecked = panel == "queue";
        LyricsTab.IsChecked = panel == "lyrics";
        if (panel == "queue") QueueView.ScrollToCurrent();
    }

    internal void ShowPanelFromDev(string? panel) => ShowPanel(panel);

    private void OnQueueTab(object sender, RoutedEventArgs e) => ShowPanel("queue");

    private void OnLyricsTab(object sender, RoutedEventArgs e) => ShowPanel("lyrics");

    private void OnClosePanel(object sender, RoutedEventArgs e) => ShowPanel(null);

    // ───────────────────────── Account ─────────────────────────

    private void OnAccountMenuOpening(object? sender, object e)
    {
        AccountMenu.Items.Clear();
        if (App.Auth.IsSignedIn)
        {
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
        settings.Click += (_, _) => Navigate("settings");
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
            case VirtualKey.F:
            case VirtualKey.K: SearchBox.Focus(FocusState.Keyboard); break;
            case VirtualKey.L: _ = player.ToggleLikeAsync(); break;
            case VirtualKey.S: player.ToggleShuffle(); break;
            case VirtualKey.R: player.CycleRepeat(); break;
        }
        args.Handled = true;
    }
}

internal static class ResourceExtensions
{
    /// <summary>Looks up a theme resource for the current theme of <paramref name="scope"/> (or the main window).</summary>
    public static object ThemeLookup(this ResourceDictionary _, string key, FrameworkElement? scope = null)
    {
        var app = Application.Current.Resources;
        var element = scope ?? App.MainWindow?.Content as FrameworkElement;
        var theme = element is { ActualTheme: ElementTheme.Light } ? "Light" : "Dark";
        foreach (var dict in app.MergedDictionaries.Prepend(app))
        {
            if (dict.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue(key, out var v))
                return v;
        }
        return app[key];
    }
}
