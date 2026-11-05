using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

public sealed partial class QueuePanel : UserControl
{
    public QueuePanel()
    {
        InitializeComponent();
        Player.PropertyChanged += OnPlayerChanged;
        Player.Queue.CollectionChanged += OnQueueChanged;
        UpdateCount();
    }

    public PlayerService Player => App.Player;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerService.Current))
        {
            UpdateCount();
            ScrollToCurrent();
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var upcoming = Math.Max(0, Player.Queue.Count - Player.CurrentIndex - 1);
        CountText.Text = Player.Current is null ? "" : upcoming == 0 ? "Last song in the queue" : $"{upcoming} up next";
    }

    public void ScrollToCurrent()
    {
        if (Player.Current is null || Visibility != Visibility.Visible) return;
        // Leave the previous track visible above the current one for context.
        var target = Player.CurrentIndex > 0 ? Player.Queue[Player.CurrentIndex - 1] : Player.Current;
        QueueList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MusicItem item) Player.PlayIndex(Player.Queue.IndexOf(item));
    }

    private void OnClear(object sender, RoutedEventArgs e) => Player.ClearUpcoming();
}
