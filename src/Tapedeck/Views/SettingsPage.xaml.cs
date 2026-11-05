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
            Content = "Tapedeck will forget your Google session on this PC.",
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

    private void OnReloadPlayer(object sender, RoutedEventArgs e) => App.Bridge.Reload();

    private void OnShowPlayer(object sender, RoutedEventArgs e) => App.Bridge.OpenDevTools();

    private async void OnOpenData(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchFolderPathAsync(SettingsService.DataFolder);
}
