using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using YourTube.Core;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private readonly LibraryThumbnails libraryThumbnails = new();
    private readonly Dictionary<SelectorItem, (GridView Feed, CancellationTokenSource Request)> libraryImages = new();
    private readonly ObservableCollection<HomeShelf> homeShelves = new();
    private PointerEventHandler? homeShelfWheelHandler;
    private HistoryVideo? libraryOrigin;
    private HomeShelf? homeOrigin;
    private bool? compactLibrary;

    private void WireLibrary()
    {
        var feed = C<GridView>("HistoryList");
        C<Grid>("BrowseAndSettings").SizeChanged += (_, args) => ArrangeLibraryNavigation(args.NewSize.Width);
        feed.SizeChanged += (_, _) => SizeLibraryCards(feed);
        feed.Loaded += (_, _) => SizeLibraryCards(feed);
        feed.ContainerContentChanging += (_, args) => RenderLibraryCard(feed, args);
        var shelves = C<ListView>("HomeShelvesList");
        shelves.ItemsSource = homeShelves;
        shelves.ContainerContentChanging += (_, args) => RenderHomeShelf(args);
    }

    private void ArrangeLibraryNavigation(double width)
    {
        if (width <= 0) return;
        var compact = width < 640;
        if (compactLibrary == compact) return;
        compactLibrary = compact;
        var rail = C<Border>("NavigationRail");
        var railScroll = C<ScrollViewer>("NavigationRailScroll");
        var nav = C<StackPanel>("LibraryNavigation");
        var content = C<Grid>("WorkspaceContent");
        rail.Width = compact ? double.NaN : 166;
        rail.Padding = compact ? new Thickness(6, 6, 6, 6) : new Thickness(11, 16, 11, 16);
        rail.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        nav.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        railScroll.HorizontalScrollMode = compact ? ScrollMode.Enabled : ScrollMode.Disabled;
        railScroll.HorizontalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Grid.SetRow(rail, compact ? 0 : 1);
        Grid.SetColumnSpan(rail, compact ? 2 : 1);
        Grid.SetColumn(content, compact ? 0 : 1);
        Grid.SetColumnSpan(content, compact ? 2 : 1);
    }

    private static void SizeLibraryCards(GridView feed)
    {
        if (feed.ItemsPanelRoot is not ItemsWrapGrid panel || feed.ActualWidth <= 24) return;
        var available = feed.ActualWidth - feed.Padding.Left - feed.Padding.Right - 2;
        var columns = Math.Clamp((int)(available / 190), 1, 4);
        var width = Math.Max(1, Math.Min(328, Math.Floor(available / columns)));
        var height = Math.Ceiling(Math.Max(1, width - 12) * 9 / 16) + 76;
        if (panel.ItemWidth != width) panel.ItemWidth = width;
        if (panel.ItemHeight != height) panel.ItemHeight = height;
    }

    private static void SetLibraryDestination(Button button, string title, bool enabled, bool active)
    {
        button.IsEnabled = enabled;
        button.Background = active ? new SolidColorBrush(ColorHelper.FromArgb(255, 51, 66, 54)) : new SolidColorBrush(Colors.Transparent);
        if (active) button.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 192, 215, 174));
        else button.ClearValue(Control.ForegroundProperty);
        AutomationProperties.SetName(button, active ? $"{title}, selected" : title);
    }

    private void RestoreLibraryFocus(HistoryVideo? origin)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || settingsVisible || playback.Selected is not null || controller is not { Phase: SessionPhase.SignedIn, Busy: false }) return;
            if (controller.View == BrowseView.Home)
            {
                RestoreHomeFocus(origin);
                return;
            }
            var feed = C<GridView>("HistoryList");
            if (origin is not null && controller.Videos.Any(video => ReferenceEquals(video, origin)))
            {
                feed.ScrollIntoView(origin);
                feed.UpdateLayout();
                if (feed.ContainerFromItem(origin) is Control item && item.Focus(FocusState.Programmatic)) return;
            }
            if (!feed.Focus(FocusState.Programmatic))
                C<Button>(controller.View == BrowseView.Subscriptions ? "SubscriptionsButton" : "HistoryButton").Focus(FocusState.Programmatic);
        });
    }

    private void RenderHomeShelves(IReadOnlyList<HomeShelf> shelves)
    {
        // Continuations append new shelf objects to the existing snapshot, so don't reset its scroller.
        var keepsPrefix = homeShelves.Count <= shelves.Count && homeShelves.Select((shelf, i) => ReferenceEquals(shelf, shelves[i])).All(same => same);
        if (!keepsPrefix)
        {
            libraryOrigin = null;
            homeOrigin = null;
            CancelLibraryImages();
            homeShelves.Clear();
        }
        for (var i = homeShelves.Count; i < shelves.Count; i++) homeShelves.Add(shelves[i]);
    }

    private void RenderHomeShelf(ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid host ||
            host.Children[0] is not TextBlock header || host.Children[1] is not GridView feed) return;
        var shelf = !closed && !args.InRecycleQueue ? args.Item as HomeShelf : null;
        if (ReferenceEquals(feed.Tag, shelf)) return;

        homeShelfWheelHandler ??= ForwardHomeShelfWheel;
        if (feed.Tag is HomeShelf) feed.RemoveHandler(UIElement.PointerWheelChangedEvent, homeShelfWheelHandler);
        feed.Tag = null;
        feed.ItemsSource = null;
        CancelLibraryImages(feed);
        feed.ContainerContentChanging -= RenderShelfCard;
        feed.ItemClick -= OpenHomeVideoAsync;
        header.Text = "";
        AutomationProperties.SetName(feed, "");
        if (shelf is null) return;

        header.Text = shelf.Title;
        AutomationProperties.SetName(feed, shelf.Title);
        feed.Tag = shelf;
        feed.ContainerContentChanging += RenderShelfCard;
        feed.ItemClick += OpenHomeVideoAsync;
        // A shelf GridView can handle the wheel before the outer ListView sees it.
        feed.AddHandler(UIElement.PointerWheelChangedEvent, homeShelfWheelHandler, handledEventsToo: true);
        feed.ItemsSource = shelf.Videos;
        args.Handled = true;
    }

    private void ForwardHomeShelfWheel(object sender, PointerRoutedEventArgs args)
    {
        if (closed || sender is not GridView { Tag: HomeShelf } feed) return;
        var wheel = args.GetCurrentPoint(feed).Properties;
        if (wheel.IsHorizontalMouseWheel || args.KeyModifiers != VirtualKeyModifiers.None) return;
        if (wheel.MouseWheelDelta == 0) return;

        var shelves = C<ListView>("HomeShelvesList");
        ScrollViewer? outer = null;
        var ancestor = VisualTreeHelper.GetParent(feed);
        while (ancestor is not null && !ReferenceEquals(ancestor, shelves))
        {
            if (ancestor is ScrollViewer scroller) outer = scroller;
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        if (ancestor is null || outer is null) return;

        const double dipPerNotch = 48;
        const double wheelDeltaPerNotch = 120;
        var offset = Math.Clamp(outer.VerticalOffset - wheel.MouseWheelDelta / wheelDeltaPerNotch * dipPerNotch, 0, outer.ScrollableHeight);
        args.Handled = true;
        if (offset != outer.VerticalOffset) outer.ChangeView(null, offset, null, disableAnimation: true);
    }

    private void RenderShelfCard(ListViewBase sender, ContainerContentChangingEventArgs args) => RenderLibraryCard((GridView)sender, args, resize: false);

    private async void OpenHomeVideoAsync(object sender, ItemClickEventArgs args)
    {
        if (closed || settingsVisible || playback.Selected is not null ||
            controller is not { Busy: false, Phase: SessionPhase.SignedIn, View: BrowseView.Home } current ||
            sender is not GridView { Tag: HomeShelf shelf } || args.ClickedItem is not HistoryVideo video ||
            !current.HomeShelves.Any(candidate => ReferenceEquals(candidate, shelf)) ||
            !shelf.Videos.Any(candidate => ReferenceEquals(candidate, video))) return;
        libraryOrigin = video;
        homeOrigin = shelf;
        await OpenVideoAsync(video);
    }

    private void RestoreHomeFocus(HistoryVideo? origin)
    {
        var shelves = C<ListView>("HomeShelvesList");
        if (homeOrigin is { } shelf && origin is not null && homeShelves.Any(candidate => ReferenceEquals(candidate, shelf)) &&
            shelf.Videos.Any(candidate => ReferenceEquals(candidate, origin)))
        {
            shelves.ScrollIntoView(shelf);
            shelves.UpdateLayout();
            if (shelves.ContainerFromItem(shelf) is ListViewItem { ContentTemplateRoot: Grid host } &&
                host.Children[1] is GridView feed && ReferenceEquals(feed.Tag, shelf))
            {
                feed.ScrollIntoView(origin);
                feed.UpdateLayout();
                if (feed.ContainerFromItem(origin) is Control item && item.Focus(FocusState.Programmatic)) return;
            }
        }
        C<Button>("HomeButton").Focus(FocusState.Programmatic);
    }

    private void RenderLibraryCard(GridView feed, ContainerContentChangingEventArgs args, bool resize = true)
    {
        var container = args.ItemContainer;
        if (libraryImages.Remove(container, out var previous)) previous.Request.Cancel();
        AutomationProperties.SetName(container, "");
        if (container.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        host.RowDefinitions.Clear();
        if (closed || args.InRecycleQueue || args.Item is not HistoryVideo video) return;
        if (resize) SizeLibraryCards(feed);

        var sage = new SolidColorBrush(ColorHelper.FromArgb(255, 192, 215, 174));
        var muted = new SolidColorBrush(ColorHelper.FromArgb(255, 172, 184, 174));
        var image = new Image { Stretch = Stretch.UniformToFill };
        var art = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(255, 39, 53, 45)), IsHitTestVisible = false };
        art.Children.Add(new FontIcon { Glyph = PlayGlyph, FontSize = 26, Foreground = sage });
        art.Children.Add(image);
        if (!string.IsNullOrWhiteSpace(video.Duration))
        {
            art.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 7), Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ColorHelper.FromArgb(230, 17, 23, 19)),
                Child = new TextBlock { Text = video.Duration, FontSize = 12 }
            });
        }
        var progress = controller?.View == BrowseView.History ? video.PercentWatched : null;
        if (progress is > 0)
        {
            var bar = new ProgressBar { Value = Math.Clamp(progress.Value, 0, 100), Foreground = sage, Height = 4, VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetAccessibilityView(bar, AccessibilityView.Raw);
            art.Children.Add(bar);
        }
        var cover = new Border { CornerRadius = new CornerRadius(7), Child = art };
        var labels = new StackPanel { Spacing = 2, Margin = new Thickness(3, 6, 3, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = video.Title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock { Text = video.Channel, FontSize = 12, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        host.Children.Add(cover);
        Grid.SetRow(labels, 1);
        host.Children.Add(labels);
        var description = string.Join(", ", new[] { video.Title, video.Channel, video.Duration, progress is int value ? $"{Math.Clamp(value, 0, 100)}% watched" : "" }.Where(part => !string.IsNullOrWhiteSpace(part)));
        AutomationProperties.SetName(container, $"{description}. Play video.");
        args.Handled = true;

        var request = new CancellationTokenSource();
        libraryImages.Add(container, (feed, request));
        _ = LoadLibraryImageAsync(container, image, video.VideoId, request);
    }

    private async Task LoadLibraryImageAsync(SelectorItem container, Image image, string videoId, CancellationTokenSource request)
    {
        try
        {
            var bytes = await libraryThumbnails.LoadAsync(videoId, request.Token);
            if (closed || request.IsCancellationRequested || bytes is null) return;
            var bitmap = await DecodeImageAsync(bytes, 480);
            if (!closed && !request.IsCancellationRequested && libraryImages.TryGetValue(container, out var current) && ReferenceEquals(request, current.Request))
                image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // An unavailable or undecodable image leaves the placeholder.
        finally
        {
            if (libraryImages.TryGetValue(container, out var current) && ReferenceEquals(request, current.Request)) libraryImages.Remove(container);
            request.Dispose();
        }
    }

    private static async Task<BitmapImage> DecodeImageAsync(byte[] bytes, int width)
    {
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = width };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void CancelLibraryImages(GridView? feed = null)
    {
        foreach (var (container, entry) in libraryImages.Where(pair => feed is null || ReferenceEquals(pair.Value.Feed, feed)).ToArray())
        {
            libraryImages.Remove(container);
            entry.Request.Cancel();
        }
    }

    private void DisposeLibrary()
    {
        CancelLibraryImages();
        libraryThumbnails.Dispose();
    }
}
