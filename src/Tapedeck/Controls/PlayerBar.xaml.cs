using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

public sealed partial class PlayerBar : UserControl, INotifyPropertyChanged
{
    private bool _dragging;
    private bool _updatingSeek;
    private bool _updatingVolume;

    public PlayerBar()
    {
        InitializeComponent();
        SeekSlider.ThumbToolTipValueConverter = new TimeConverter();
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        SeekSlider.ValueChanged += OnSeekValueChanged;

        Player.PropertyChanged += OnPlayerChanged;
        SyncVolume();
        UpdateTimeline();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PlayerService Player => App.Player;

    /// <summary>Raised when the user toggles the queue or lyrics panel ("queue" / "lyrics" / null).</summary>
    public event EventHandler<string?>? PanelRequested;

    public Brush ShuffleBrush => Player.IsShuffle ? Accent : Normal;
    public Brush RepeatBrush => Player.Repeat != RepeatMode.Off ? Accent : Normal;
    private static Brush Accent => (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    private static Brush Normal => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    public void SetPanelState(string? panel)
    {
        QueueToggle.IsChecked = panel == "queue";
        LyricsToggle.IsChecked = panel == "lyrics";
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Position):
            case nameof(PlayerService.Duration):
            case nameof(PlayerService.Current):
                UpdateTimeline();
                break;
            case nameof(PlayerService.Volume):
            case nameof(PlayerService.IsMuted):
                SyncVolume();
                break;
            case nameof(PlayerService.IsShuffle):
                PropertyChanged?.Invoke(this, new(nameof(ShuffleBrush)));
                break;
            case nameof(PlayerService.Repeat):
                PropertyChanged?.Invoke(this, new(nameof(RepeatBrush)));
                break;
        }
    }

    private void UpdateTimeline()
    {
        var duration = Math.Max(Player.Duration, 0);
        DurationText.Text = Formatting.Time(duration);
        if (_dragging) return;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, duration);
        SeekSlider.Value = Math.Clamp(Player.Position, 0, SeekSlider.Maximum);
        _updatingSeek = false;
        PositionText.Text = Formatting.Time(Player.Position);
    }

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek) return;
        PositionText.Text = Formatting.Time(e.NewValue);
        // Keyboard / click-to-position changes seek immediately; drags seek on release.
        if (!_dragging) Player.Seek(e.NewValue);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Player.Seek(SeekSlider.Value);
    }

    private void SyncVolume()
    {
        _updatingVolume = true;
        VolumeSlider.Value = Player.IsMuted ? 0 : Math.Round(Player.Volume * 100);
        _updatingVolume = false;
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume) return;
        Player.IsMuted = false;
        Player.Volume = e.NewValue / 100.0;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();

    private void OnNext(object sender, RoutedEventArgs e) => Player.Next();

    private void OnPrevious(object sender, RoutedEventArgs e) => Player.Previous();

    private void OnShuffle(object sender, RoutedEventArgs e) => Player.ToggleShuffle();

    private void OnRepeat(object sender, RoutedEventArgs e) => Player.CycleRepeat();

    private void OnMute(object sender, RoutedEventArgs e) => Player.IsMuted = !Player.IsMuted;

    private void OnLike(object sender, RoutedEventArgs e) => _ = Player.ToggleLikeAsync();

    private void OnQueueToggle(object sender, RoutedEventArgs e) =>
        PanelRequested?.Invoke(this, QueueToggle.IsChecked == true ? "queue" : null);

    private void OnLyricsToggle(object sender, RoutedEventArgs e) =>
        PanelRequested?.Invoke(this, LyricsToggle.IsChecked == true ? "lyrics" : null);

    private void OnTitleTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Player.Current?.Album?.Id is { } album) Nav.ToCollection(album);
    }

    private void OnArtistTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Player.Current?.Artists.FirstOrDefault(a => a.Id is not null)?.Id is { } artist) Nav.ToArtist(artist);
    }

    private sealed partial class TimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            Formatting.Time(value is double d ? d : 0);

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
