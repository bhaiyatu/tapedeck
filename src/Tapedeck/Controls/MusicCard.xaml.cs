using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tapedeck.Models;
using Tapedeck.Services;

namespace Tapedeck.Controls;

/// <summary>Square (or round, or 16:9) artwork card with a hover play button.</summary>
public sealed partial class MusicCard : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(MusicItem), typeof(MusicCard), new PropertyMetadata(null, (d, _) => ((MusicCard)d).OnItemChanged()));

    public MusicCard()
    {
        InitializeComponent();
        ArtHost.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
    }

    private void OnArtSizeChanged(object sender, SizeChangedEventArgs e) =>
        ArtHost.CenterPoint = new System.Numerics.Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);

    public MusicItem? Item
    {
        get => (MusicItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public double CardWidth { get; private set; } = 168;
    public double ArtHeight { get; private set; } = 168;
    public Visibility CanPlay { get; private set; } = Visibility.Visible;
    public HorizontalAlignment TextAlignment { get; private set; } = HorizontalAlignment.Stretch;
    public Microsoft.UI.Xaml.TextAlignment TextAlign { get; private set; } = Microsoft.UI.Xaml.TextAlignment.Left;

    private void OnItemChanged()
    {
        var item = Item;
        if (item is null) return;
        CardWidth = item.IsWide ? 288 : 172;
        ArtHeight = item.IsWide ? 162 : 172;
        CanPlay = item.Kind is ItemKind.Artist or ItemKind.Mood ? Visibility.Collapsed : Visibility.Visible;
        TextAlign = item.IsRound ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;
        TextAlignment = item.IsRound ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Bindings.Update();
        AutomationProperties_SetName(item);
    }

    private void AutomationProperties_SetName(MusicItem item) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"{item.Title}, {item.Subtitle}");

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HoverOverlay.Opacity = 1;
        ArtHost.Scale = new System.Numerics.Vector3(1.03f, 1.03f, 1);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverOverlay.Opacity = 0;
        ArtHost.Scale = System.Numerics.Vector3.One;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInside(e.OriginalSource as DependencyObject, PlayButton)) return;
        if (Item is { } item) Nav.Open(item);
    }

    internal static bool IsInside(DependencyObject? source, DependencyObject container)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, container)) return true;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Item is { } item) Nav.Play(item);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Item is not { } item) return;
        Nav.ShowItemMenu(item, this, e.GetPosition(this));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Item is not { } item) return;
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            Nav.Open(item);
            e.Handled = true;
        }
    }
}
