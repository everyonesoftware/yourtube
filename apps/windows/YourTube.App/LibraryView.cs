using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using YourTube.Core;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private readonly LibraryThumbnails libraryThumbnails = new();
    private readonly Dictionary<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem, CancellationTokenSource> libraryImages = new();
    private HistoryVideo? libraryOrigin;
    private bool? compactLibrary;

    private void WireLibrary()
    {
        var feed = C<GridView>("HistoryList");
        C<Grid>("BrowseAndSettings").SizeChanged += (_, args) => ArrangeLibraryNavigation(args.NewSize.Width);
        feed.SizeChanged += (_, _) => SizeLibraryCards(feed);
        feed.Loaded += (_, _) => SizeLibraryCards(feed);
        feed.ContainerContentChanging += (_, args) => RenderLibraryCard(feed, args);
    }

    private void ArrangeLibraryNavigation(double width)
    {
        if (width <= 0) return;
        var compact = width < 640;
        if (compactLibrary == compact) return;
        compactLibrary = compact;
        var rail = C<Border>("NavigationRail");
        var nav = C<StackPanel>("LibraryNavigation");
        var content = C<Grid>("WorkspaceContent");
        rail.Width = compact ? double.NaN : 166;
        rail.Padding = compact ? new Thickness(6, 6, 6, 6) : new Thickness(11, 16, 11, 16);
        rail.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        nav.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
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

    private void RenderLibraryCard(GridView feed, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        if (libraryImages.Remove(container, out var previous)) previous.Cancel();
        AutomationProperties.SetName(container, "");
        if (container.ContentTemplateRoot is not Grid host) return;
        host.Children.Clear();
        host.RowDefinitions.Clear();
        if (closed || args.InRecycleQueue || args.Item is not HistoryVideo video) return;
        SizeLibraryCards(feed);

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
        libraryImages.Add(container, request);
        _ = LoadLibraryImageAsync(container, image, video.VideoId, request);
    }

    private async Task LoadLibraryImageAsync(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, Image image, string videoId, CancellationTokenSource request)
    {
        try
        {
            var bytes = await libraryThumbnails.LoadAsync(videoId, request.Token);
            if (closed || request.IsCancellationRequested || bytes is null) return;
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 480 };
            await bitmap.SetSourceAsync(stream);
            if (!closed && !request.IsCancellationRequested && libraryImages.TryGetValue(container, out var current) && ReferenceEquals(request, current))
                image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (System.Runtime.InteropServices.COMException) { } // An unavailable or undecodable image leaves the placeholder.
        finally
        {
            if (libraryImages.TryGetValue(container, out var current) && ReferenceEquals(request, current)) libraryImages.Remove(container);
            request.Dispose();
        }
    }

    private void CancelLibraryImages()
    {
        var requests = libraryImages.Values.ToArray();
        libraryImages.Clear();
        foreach (var request in requests) request.Cancel();
    }

    private void DisposeLibrary()
    {
        CancelLibraryImages();
        libraryThumbnails.Dispose();
    }
}
