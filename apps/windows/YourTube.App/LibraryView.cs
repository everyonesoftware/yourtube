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
    private ScrollViewer? homeScroller;
    private readonly ObservableCollection<HistoryVideo> libraryVideos = new();
    private HistoryVideo? libraryOrigin;
    private HomeShelf? homeOrigin;
    private bool? compactLibrary;

    private void WireLibrary()
    {
        var feed = C<GridView>("HistoryList");
        feed.ItemsSource = libraryVideos;
        WireFeedPaging(feed, home: false);
        C<Grid>("BrowseAndSettings").SizeChanged += (_, args) => ArrangeLibraryNavigation(args.NewSize.Width);
        feed.SizeChanged += (_, _) => SizeLibraryCards(feed);
        feed.Loaded += (_, _) => SizeLibraryCards(feed);
        feed.ContainerContentChanging += (_, args) => RenderLibraryCard(feed, args);
        var shelves = C<ListView>("HomeShelvesList");
        shelves.ItemsSource = homeShelves;
        shelves.ContainerContentChanging += (_, args) => RenderHomeShelf(args);
        WireFeedPaging(shelves, home: true);
    }

    private void WireFeedPaging(ListViewBase feed, bool home)
    {
        ScrollViewer? scroller = null;
        double offset = 0;
        bool queued = false;
        feed.Loaded += (_, _) =>
        {
            feed.ApplyTemplate();
            if (FindFeedScroller(feed) is not { } found || ReferenceEquals(scroller, found)) return;
            Detach();
            scroller = found;
            if (home) homeScroller = found;
            offset = found.VerticalOffset;
            found.ViewChanged += ViewChanged;
        };
        feed.Unloaded += (_, _) => Detach();
        feed.LayoutUpdated += (_, _) => QueuePage(underfilled: true);
        // A downward gesture may not change the offset at the end of a short page.
        feed.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var wheel = args.GetCurrentPoint(feed).Properties;
            if (!wheel.IsHorizontalMouseWheel && wheel.MouseWheelDelta < 0 && (args.KeyModifiers & VirtualKeyModifiers.Shift) == 0)
                QueuePage();
        }), handledEventsToo: true);
        feed.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End) QueuePage();
        }), handledEventsToo: true);

        void Detach()
        {
            if (scroller is not null) scroller.ViewChanged -= ViewChanged;
            scroller = null;
            if (home) homeScroller = null;
        }

        void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (scroller is null) return;
            var movedDown = scroller.VerticalOffset > offset;
            offset = scroller.VerticalOffset;
            if (movedDown) QueuePage();
        }

        static bool NearEnd(ScrollViewer candidate, bool underfilled) => candidate.ViewportHeight > 0 &&
            (underfilled ? candidate.ScrollableHeight == 0 :
                candidate.ScrollableHeight - candidate.VerticalOffset <= candidate.ViewportHeight / 2);
        bool IsActive(BrowseView view) => !closed && !settingsVisible && !search.Active && playback.Selected is null &&
            feed.Visibility == Visibility.Visible && (home ? view == BrowseView.Home : view is BrowseView.History or BrowseView.Subscriptions);

        void QueuePage(bool underfilled = false)
        {
            if (queued || scroller is not { } candidate || !NearEnd(candidate, underfilled) ||
                controller is not { Busy: false, Phase: SessionPhase.SignedIn, AppendFailed: false,
                    SelectedAccount: { } account, Continuation: { } cursor } current || !IsActive(current.View)) return;
            var view = current.View;
            var videos = current.Videos;
            // Don't mutate the collection inside a native scroll/input callback.
            queued = DispatcherQueue.TryEnqueue(async () =>
            {
                queued = false;
                if (!ReferenceEquals(candidate, scroller) || !NearEnd(candidate, underfilled) ||
                    !ReferenceEquals(current, controller) || current is not { Busy: false, Phase: SessionPhase.SignedIn, AppendFailed: false } ||
                    current.View != view || !IsActive(view) || !ReferenceEquals(account, current.SelectedAccount) ||
                    !ReferenceEquals(videos, current.Videos) || cursor != current.Continuation) return;
                await current.LoadHistoryAsync(append: true);
            });
        }
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
            if (closed || settingsVisible || playback.Selected is not null || controller is not { CanPlayFeed: true }) return;
            if (search.Active)
            {
                var results = C<GridView>("SearchResultsList");
                if (origin is not null && search.Videos.Any(video => ReferenceEquals(video, origin)))
                {
                    results.ScrollIntoView(origin);
                    results.UpdateLayout();
                    if (results.ContainerFromItem(origin) is Control item && item.Focus(FocusState.Programmatic)) return;
                }
                C<TextBox>("BrowseSearchBox").Focus(FocusState.Programmatic);
                return;
            }
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

    private void RenderLibraryVideos(IReadOnlyList<HistoryVideo>? videos)
    {
        if (ReferenceEquals(displayedVideos, videos)) return;
        displayedVideos = videos;
        var keepsPrefix = videos is not null && libraryVideos.Count <= videos.Count &&
            libraryVideos.Select((video, i) => ReferenceEquals(video, videos[i])).All(same => same);
        if (!keepsPrefix)
        {
            libraryOrigin = null;
            CancelLibraryImages(C<GridView>("HistoryList"));
            libraryVideos.Clear();
        }
        if (videos is not null)
            for (var i = libraryVideos.Count; i < videos.Count; i++) libraryVideos.Add(videos[i]);
    }

    private void RenderHomeShelf(ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid host ||
            host.Children[0] is not TextBlock header || host.Children[1] is not GridView feed) return;
        var shelf = !closed && !args.InRecycleQueue ? args.Item as HomeShelf : null;
        if (ReferenceEquals(feed.Tag, shelf)) return;

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
        if (host.Tag is null)
        {
            host.Tag = true;
            WireHomeShelfWheel(feed);
        }
        feed.ItemsSource = shelf.Videos;
        args.Handled = true;
    }

    private static ScrollViewer? FindFeedScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindFeedScroller(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void WireHomeShelfWheel(GridView feed)
    {
        Action? detach = null;
        void Attach()
        {
            detach?.Invoke();
            detach = null;
            feed.ApplyTemplate();
            if (FindFeedScroller(feed) is not { } scroller || scroller.Content is not UIElement wheelSurface) return;
            // Intercept below the inner ScrollViewer, before native wheel-to-horizontal conversion.
            scroller.HorizontalSnapPointsType = SnapPointsType.None;
            PointerEventHandler handler = (_, args) => ForwardHomeShelfWheel(feed, scroller, args);
            wheelSurface.AddHandler(UIElement.PointerWheelChangedEvent, handler, handledEventsToo: true);
            detach = () => wheelSurface.RemoveHandler(UIElement.PointerWheelChangedEvent, handler);
        }
        feed.Loaded += (_, _) => Attach();
        feed.Unloaded += (_, _) => { detach?.Invoke(); detach = null; };
        if (feed.IsLoaded) Attach();
    }

    private void ForwardHomeShelfWheel(GridView feed, ScrollViewer scroller, PointerRoutedEventArgs args)
    {
        if (closed || feed.Tag is not HomeShelf) return;
        var wheel = args.GetCurrentPoint(feed).Properties;
        var delta = wheel.MouseWheelDelta / 120.0 * 48;
        if (delta == 0) return;
        args.Handled = true;
        if ((args.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
        {
            var offset = Math.Clamp(scroller.HorizontalOffset + (wheel.IsHorizontalMouseWheel ? delta : -delta), 0, scroller.ScrollableWidth);
            if (offset != scroller.HorizontalOffset) scroller.ChangeView(offset, null, null, disableAnimation: true);
        }
        else if (!wheel.IsHorizontalMouseWheel && homeScroller is { } outer)
        {
            var vertical = Math.Clamp(outer.VerticalOffset - delta, 0, outer.ScrollableHeight);
            if (vertical != outer.VerticalOffset) outer.ChangeView(null, vertical, null, disableAnimation: true);
        }
    }

    private void RenderShelfCard(ListViewBase sender, ContainerContentChangingEventArgs args) => RenderLibraryCard((GridView)sender, args, resize: false);

    private async void OpenHomeVideoAsync(object sender, ItemClickEventArgs args)
    {
        if (closed || settingsVisible || search.Active || playback.Selected is not null ||
            controller is not { CanPlayFeed: true, View: BrowseView.Home } current ||
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
        var progress = controller?.View is BrowseView.History or BrowseView.Subscriptions ? video.PercentWatched : null;
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
        var metadata = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var channelLabel = new TextBlock { Text = video.Channel, FontSize = 12, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
        string? publication = null;
        if (video.PublishedAge is { } age)
        {
            publication = $"{(age.StartsWith("Streamed ", StringComparison.OrdinalIgnoreCase) || age.StartsWith("Premiered ", StringComparison.OrdinalIgnoreCase) ? "" : "Published ")}{age}";
            var ageLabel = new TextBlock { Text = age, FontSize = 12, Foreground = muted };
            if (video.PublishedDate is { } date)
            {
                var exact = $"Published {date:D}";
                ToolTipService.SetToolTip(ageLabel, exact);
                publication += $" ({exact})";
            }
            if (!string.IsNullOrWhiteSpace(video.Channel))
            {
                metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                metadata.Children.Add(channelLabel);
                var separator = new TextBlock { Text = "·", FontSize = 12, Foreground = muted, Margin = new Thickness(5, 0, 5, 0) };
                Grid.SetColumn(separator, 1);
                metadata.Children.Add(separator);
                Grid.SetColumn(ageLabel, 2);
            }
            metadata.Children.Add(ageLabel);
        }
        else if (!string.IsNullOrWhiteSpace(video.Channel)) metadata.Children.Add(channelLabel);
        labels.Children.Add(metadata);
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        host.Children.Add(cover);
        Grid.SetRow(labels, 1);
        host.Children.Add(labels);
        var description = string.Join(", ", new[] { video.Title, video.Channel, publication, video.Duration, progress is int value ? $"{Math.Clamp(value, 0, 100)}% watched" : "" }.Where(part => !string.IsNullOrWhiteSpace(part)));
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
