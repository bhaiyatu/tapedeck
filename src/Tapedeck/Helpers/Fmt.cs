using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Helpers;

/// <summary>Static helpers used from x:Bind function bindings in XAML.</summary>
public static class Fmt
{
    public static ImageSource? Img(string? url) => LoadImage(url, 226);

    public static ImageSource? ImgSized(string? url, int size) => LoadImage(url, size);

    private static ImageSource? LoadImage(string? url, int size)
    {
        var sized = Formatting.SizedThumbnail(url, size * 2);
        if (string.IsNullOrEmpty(sized) || !Uri.TryCreate(sized, UriKind.Absolute, out var uri)) return null;
        return new BitmapImage(uri) { DecodePixelWidth = size * 2, DecodePixelType = DecodePixelType.Physical };
    }

    public static string Time(double seconds) => Formatting.Time(seconds);

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowIf(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowObj(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HideObj(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static string PlayGlyph(bool playing) => playing ? "" : "";

    public static string LikeGlyph(bool liked) => liked ? "" : "";

    public static string RepeatGlyph(RepeatMode mode) => mode == RepeatMode.One ? "" : "";

    public static double ActiveOpacity(bool active) => active ? 1.0 : 0.55;

    public static double RepeatOpacity(RepeatMode mode) => mode == RepeatMode.Off ? 0.55 : 1.0;

    public static string VolumeGlyph(double volume, bool muted) =>
        muted || volume <= 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";

    public static double Percent(double volume) => Math.Round(volume * 100);

    public static string Kind(MusicItem item) => item.Kind switch
    {
        ItemKind.Song => "Song",
        ItemKind.Video => "Video",
        ItemKind.Album => item.TypeLabel ?? "Album",
        ItemKind.Playlist => "Playlist",
        ItemKind.Artist => "Artist",
        ItemKind.Episode => "Episode",
        ItemKind.Podcast => "Podcast",
        _ => "",
    };

    public static CornerRadius ArtRadius(bool round) => round ? new CornerRadius(999) : new CornerRadius(6);

    public static Brush? MoodBrush(object? argb)
    {
        if (argb is not uint c) return null;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }

    public static string NowPlayingTitle(MusicItem? item) => item?.Title ?? "Nothing playing";

    public static string NowPlayingArtist(MusicItem? item) =>
        item is null ? "Pick something to play" : item.ArtistText.Length > 0 ? item.ArtistText : item.Subtitle;
}
