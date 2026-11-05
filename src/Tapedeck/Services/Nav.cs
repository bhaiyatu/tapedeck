using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Tapedeck.Models;
using Tapedeck.Views;

namespace Tapedeck.Services;

public sealed record ShelvesArgs(string BrowseId, string? Params = null, string? Title = null);
public sealed record CollectionArgs(string BrowseId);
public sealed record ArtistArgs(string BrowseId);
public sealed record SearchArgs(string Query, string? FilterParams = null);

/// <summary>Page navigation plus the shared "what happens when you click / right-click an item" logic.</summary>
public static class Nav
{
    public static Frame? Frame { get; set; }

    public static void To(Type page, object? args = null)
    {
        if (Frame is null) return;
        Frame.Navigate(page, args, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    public static void ToArtist(string? id)
    {
        if (!string.IsNullOrEmpty(id)) To(typeof(ArtistPage), new ArtistArgs(id));
    }

    public static void ToCollection(string? id)
    {
        if (!string.IsNullOrEmpty(id)) To(typeof(CollectionPage), new CollectionArgs(id));
    }

    public static void ToSearch(string query, string? filter = null) => To(typeof(SearchPage), new SearchArgs(query, filter));

    public static void ToBrowse(string browseId, string? @params, string? title) => To(typeof(ShelvesPage), new ShelvesArgs(browseId, @params, title));

    /// <summary>Default action for clicking an item that isn't part of a track list.</summary>
    public static void Open(MusicItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Album:
            case ItemKind.Playlist:
            case ItemKind.Podcast:
                if (item.BrowseId is not null) ToCollection(item.BrowseId);
                else if (item.PlayEndpoint is not null) _ = App.Player.PlayEndpointAsync(item.PlayEndpoint);
                break;
            case ItemKind.Artist:
                ToArtist(item.BrowseId);
                break;
            case ItemKind.Mood:
                if (item.BrowseId is not null) ToBrowse(item.BrowseId, item.BrowseParams, item.Title);
                break;
            default:
                if (item.VideoId is not null) _ = App.Player.PlaySongAsync(item);
                else if (item.BrowseId is not null) ToCollection(item.BrowseId);
                break;
        }
    }

    /// <summary>The card play button: play without opening the page.</summary>
    public static void Play(MusicItem item, bool shuffle = false)
    {
        if (item.Kind is ItemKind.Album or ItemKind.Playlist or ItemKind.Podcast && item.BrowseId is not null && (shuffle || item.PlayEndpoint is null))
            _ = App.Player.PlayCollectionAsync(item.BrowseId, shuffle);
        else if (item.PlayEndpoint is not null)
            _ = App.Player.PlayEndpointAsync(item.PlayEndpoint);
        else if (item.VideoId is not null)
            _ = App.Player.PlaySongAsync(item);
        else
            Open(item);
    }

    public static void ShowItemMenu(MusicItem item, FrameworkElement target, Windows.Foundation.Point? at = null, bool inQueue = false)
    {
        var menu = new MenuFlyout();

        void Add(string text, string glyph, Action action)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        if (inQueue)
        {
            Add("Play", "", () => App.Player.PlayIndex(App.Player.Queue.IndexOf(item)));
            if (!ReferenceEquals(item, App.Player.Current))
            {
                Add("Play next", "", () =>
                {
                    App.Player.RemoveFromQueue(item);
                    App.Player.PlayNext(item);
                });
                Add("Remove from queue", "", () => App.Player.RemoveFromQueue(item));
            }
        }
        else if (item.VideoId is not null)
        {
            Add("Play", "", () => _ = App.Player.PlaySongAsync(item));
            Add("Play next", "", () => App.Player.PlayNext(item));
            Add("Add to queue", "", () => App.Player.AddToQueue(item));
            Add("Start radio", "", () => _ = App.Player.PlayRadioAsync(item));
        }
        else if (item.Kind is ItemKind.Album or ItemKind.Playlist && item.BrowseId is not null)
        {
            Add("Play", "", () => Play(item));
            Add("Shuffle", "", () => Play(item, shuffle: true));
            Add("Play next", "", () => _ = QueueCollectionAsync(item.BrowseId, next: true));
            Add("Add to queue", "", () => _ = QueueCollectionAsync(item.BrowseId, next: false));
        }
        else if (item.Kind == ItemKind.Artist && item.BrowseId is not null)
        {
            Add("Open artist", "", () => ToArtist(item.BrowseId));
        }

        var artist = item.Artists.FirstOrDefault(a => a.Id is not null);
        if (artist is not null && item.Kind != ItemKind.Artist)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            Add($"Go to {artist.Name}", "", () => ToArtist(artist.Id));
        }
        if (item.Album?.Id is { } albumId && item.Kind != ItemKind.Album)
            Add("Go to album", "", () => ToCollection(albumId));

        if (item.VideoId is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Copy link", "", () => CopyLink($"https://music.youtube.com/watch?v={item.VideoId}"));
        }
        else if (item.PlaylistId is not null || item.BrowseId is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var link = item.Kind == ItemKind.Artist
                ? $"https://music.youtube.com/channel/{item.BrowseId}"
                : item.PlaylistId is not null
                    ? $"https://music.youtube.com/playlist?list={item.PlaylistId}"
                    : $"https://music.youtube.com/browse/{item.BrowseId}";
            Add("Copy link", "", () => CopyLink(link));
        }

        if (menu.Items.Count == 0) return;
        if (at is { } p) menu.ShowAt(target, new FlyoutShowOptions { Position = p });
        else menu.ShowAt(target);
    }

    private static async Task QueueCollectionAsync(string browseId, bool next)
    {
        try
        {
            var details = await App.Api.GetCollectionAsync(browseId);
            App.Player.AddToQueue(details.Tracks, next);
        }
        catch (Exception ex)
        {
            App.Player.StatusMessage = ex.Message;
        }
    }

    public static void CopyLink(string url)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        App.Player.StatusMessage = "Link copied";
    }

    /// <summary>Finds the MusicItem behind whatever element was right-clicked inside a list.</summary>
    public static MusicItem? ItemFromSource(object? source)
    {
        var el = source as DependencyObject;
        while (el is not null)
        {
            if (el is FrameworkElement { DataContext: MusicItem m }) return m;
            el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
        }
        return null;
    }
}
