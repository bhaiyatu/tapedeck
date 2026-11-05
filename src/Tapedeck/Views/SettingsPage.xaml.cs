using System.ComponentModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tapedeck.Services;

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
#if DEBUG
        ShowPlayerButton.Visibility = Visibility.Visible;
#endif
        _initializing = false;

        UpdateAccount();
        Auth.PropertyChanged += OnAuthChanged;
        Unloaded += (_, _) => Auth.PropertyChanged -= OnAuthChanged;
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
