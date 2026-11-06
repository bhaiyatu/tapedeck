using System.ComponentModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tapedeck.Services;
using Tapedeck.Services.Discord;
using Windows.UI;

namespace Tapedeck.Views;

public sealed partial class SettingsPage : Page
{
    private bool _initializing = true;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = $"Tapedeck {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        foreach (ComboBoxItem item in ThemeBox.Items)
            if ((string)item.Tag == App.Settings.Current.Theme) ThemeBox.SelectedItem = item;
        StaySignedInSwitch.IsOn = App.Settings.Current.StaySignedIn;
        LrclibSwitch.IsOn = App.Settings.Current.UseLrclib;
        DiscordSwitch.IsOn = App.Settings.Current.DiscordPresence;
        foreach (ComboBoxItem item in DiscordStatusBox.Items)
            if ((string)item.Tag == App.Settings.Current.DiscordStatus.ToString()) DiscordStatusBox.SelectedItem = item;
#if DEBUG
        ShowPlayerButton.Visibility = Visibility.Visible;
#endif
        _initializing = false;

        UpdateAccount();
        UpdateDiscord();
        Auth.PropertyChanged += OnAuthChanged;
        App.Discord.PropertyChanged += OnDiscordChanged;
        Unloaded += (_, _) =>
        {
            Auth.PropertyChanged -= OnAuthChanged;
            App.Discord.PropertyChanged -= OnDiscordChanged;
        };
    }

    public AuthService Auth => App.Auth;

    private void OnAuthChanged(object? sender, PropertyChangedEventArgs e) => UpdateAccount();

    private void UpdateAccount()
    {
        if (!Auth.IsSignedIn)
        {
            AccountName.Text = "Not signed in";
            AccountDetail.Text = "Sign in to see your library, personalised recommendations, and to like songs. Premium plays without ads.";
            return;
        }
        AccountName.Text = Auth.Account?.Name ?? "Signed in";
        AccountDetail.Text = Auth.Account?.Handle ?? "Google account";
    }

    private async void OnSignIn(object sender, RoutedEventArgs e) => await App.MainWindow.SignInAsync();

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Sign out?",
            Content = "Tapedeck will end the session with Google and wipe its cookies and site data from this PC.",
            PrimaryButtonText = "Sign out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Auth.SignOutAsync();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.Settings.Current.Theme = theme;
        App.Settings.Save();
        App.MainWindow.ApplyTheme(theme);
    }

    private async void OnStaySignedInToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || StaySignedInSwitch.IsOn == App.Settings.Current.StaySignedIn) return;
        var stay = StaySignedInSwitch.IsOn;
        var dialog = new ContentDialog
        {
            Title = stay ? "Remember your sign-in?" : "Stop remembering your sign-in?",
            Content = stay
                ? "After a restart, Tapedeck keeps your Google session on this PC (encrypted to your Windows account) so you stay signed in."
                : "Tapedeck will sign you out now and wipe the stored session. After a restart, sign-ins are kept in memory only and end when you close the app.",
            PrimaryButtonText = "Restart Tapedeck",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _initializing = true;
            StaySignedInSwitch.IsOn = !stay;
            _initializing = false;
            return;
        }
        // Wipe whatever is on disk before switching to the in-memory profile.
        if (!stay) await Auth.ForgetStoredSessionAsync();
        App.Settings.Current.StaySignedIn = stay;
        App.Settings.SaveNow();
        App.Restart();
    }

    private void OnLrclibToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        App.Settings.Current.UseLrclib = LrclibSwitch.IsOn;
        App.Settings.Save();
    }

    private void OnDiscordToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        App.Settings.Current.DiscordPresence = DiscordSwitch.IsOn;
        App.Settings.Save();
        App.Discord.SettingsChanged();
        UpdateDiscord();
    }

    private void OnDiscordStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || DiscordStatusBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<DiscordStatusDisplay>(tag, out var display)) return;
        App.Settings.Current.DiscordStatus = display;
        App.Settings.Save();
        App.Discord.SettingsChanged();
    }

    private void OnDiscordChanged(object? sender, PropertyChangedEventArgs e) => UpdateDiscord();

    // Discord's own status colours.
    private static readonly Color Online = Color.FromArgb(255, 35, 165, 90);
    private static readonly Color Idle = Color.FromArgb(255, 240, 178, 50);
    private static readonly Color Problem = Color.FromArgb(255, 242, 63, 67);

    private void UpdateDiscord()
    {
        var discord = App.Discord;
        var on = App.Settings.Current.DiscordPresence;
        DiscordStatusRow.Opacity = on ? 1 : 0.5;
        DiscordStatusBox.IsEnabled = on;
        // While the switch was just flipped the service hasn't caught up yet; describe what is about to happen.
        var state = on && discord.Connection == DiscordConnection.Off ? DiscordConnection.Waiting : discord.Connection;
        if (!on) state = DiscordConnection.Off;

        (Color? Dot, string Text) look = state switch
        {
            DiscordConnection.Connected when discord.LastError is { } error =>
                (Problem, $"Discord didn't accept the last update: {error}"),
            DiscordConnection.Connected =>
                (Online, discord.UserName is { } name ? $"Connected to Discord as {name}" : "Connected to Discord"),
            DiscordConnection.Waiting => (Idle, "Waiting for Discord. Open the Discord app on this PC and it connects by itself."),
            DiscordConnection.NotConfigured => (Idle, "Discord sharing isn't set up in this build yet."),
            DiscordConnection.Error => (Problem, $"Couldn't connect to Discord: {discord.LastError}. Trying again shortly."),
            _ => (null, "Off. Nothing is sent to Discord."),
        };
        DiscordDot.Visibility = look.Dot is null ? Visibility.Collapsed : Visibility.Visible;
        if (look.Dot is { } c) DiscordDot.Fill = new SolidColorBrush(c);
        DiscordStateText.Text = look.Text;
    }

    private void OnReloadPlayer(object sender, RoutedEventArgs e) => App.Bridge.Reload();

    private void OnShowPlayer(object sender, RoutedEventArgs e)
    {
#if DEBUG
        App.Bridge.OpenDevTools();
#endif
    }

    private async void OnOpenData(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchFolderPathAsync(SettingsService.DataFolder);
}
