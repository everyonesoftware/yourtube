using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using YourTube.Core;
using YourTube.Playback;

namespace YourTube.App;

internal sealed partial class MainWindow
{
    private readonly PublicVideoSearch publicSearch = new();
    private ScopedSearch search = null!;

    private void WireSearch()
    {
        search = new ScopedSearch(async (query, token) => (await publicSearch.SearchAsync(query, token))
            .Select(video => new HistoryVideo(video.VideoId, video.Title, video.Channel, video.Duration, null)).ToArray());
        var box = C<TextBox>("BrowseSearchBox");
        C<Grid>("BrowseHeader").SizeChanged += (_, args) => ArrangeSearch(args.NewSize.Width);
        box.TextChanged += (_, _) => UpdateSearchButtons();
        box.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; await SubmitSearchAsync(box.Text); }
            else if (args.Key == VirtualKey.Escape && (search.Active || box.Text.Length > 0))
            { args.Handled = true; ClearSearch(); }
        };
        C<Button>("BrowseSearchSubmit").Click += async (_, _) => await SubmitSearchAsync(box.Text);
        C<Button>("BrowseSearchClear").Click += (_, _) => ClearSearch();
        var feed = C<GridView>("SearchResultsList");
        feed.SizeChanged += (_, _) => SizeLibraryCards(feed);
        feed.Loaded += (_, _) => SizeLibraryCards(feed);
        feed.ContainerContentChanging += (_, args) => RenderLibraryCard(feed, args);
        feed.ItemClick += async (_, args) =>
        {
            if (!CanSearch() || !search.Active || args.ClickedItem is not HistoryVideo video ||
                !search.Videos.Any(item => ReferenceEquals(item, video))) return;
            libraryOrigin = video;
            await OpenVideoAsync(video);
        };
    }

    private bool CanSearch() => !closed && !settingsVisible && playback.Selected is null &&
        controller is { CanPlayFeed: true, SelectedAccount: not null };

    private void ArrangeSearch(double width)
    {
        if (width <= 0) return;
        var panel = C<Grid>("BrowseSearch");
        var compact = width < 650;
        Grid.SetRow(panel, compact ? 1 : 0);
        Grid.SetColumn(panel, compact ? 0 : 1);
        Grid.SetColumnSpan(panel, compact ? 3 : 1);
        panel.Width = compact ? Math.Min(430, width) : 430;
        panel.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private void BindSearch()
    {
        var current = controller;
        if (search.Bind(current is { Phase: SessionPhase.SignedIn } ? current.SelectedAccount : null,
            settingsVisible ? BrowseView.Settings : current?.View ?? BrowseView.Home, current?.Videos ?? []))
            C<TextBox>("BrowseSearchBox").Text = "";
    }

    private void RenderSearch()
    {
        var box = C<TextBox>("BrowseSearchBox");
        var label = controller?.View switch
        {
            BrowseView.Subscriptions => "Search subscriptions",
            BrowseView.History => "Search history",
            _ => "Search YouTube"
        };
        if (box.PlaceholderText != label) { box.PlaceholderText = label; AutomationProperties.SetName(box, label); }
        C<Grid>("BrowseSearch").Visibility = ToVisibility(controller?.SelectedAccount is not null);
        box.IsEnabled = controller is { CanPlayFeed: true, SelectedAccount: not null };
        UpdateSearchButtons();
        C<Grid>("SearchResultsPanel").Visibility = ToVisibility(search.Active);
        var feed = C<GridView>("SearchResultsList");
        if (!ReferenceEquals(feed.ItemsSource, search.Videos))
        {
            CancelLibraryImages(feed);
            feed.ItemsSource = search.Videos;
            FindFeedScroller(feed)?.ChangeView(null, 0, null, disableAnimation: true);
        }
        var scope = controller?.View switch
        {
            BrowseView.History => "loaded history",
            BrowseView.Subscriptions => controller.SelectedSubscriptionChannel is { } channel ? $"loaded videos from {channel.Name}" : "loaded subscriptions",
            _ => "YouTube"
        };
        var status = !search.Active ? "" : search.Busy ? "Searching YouTube…" : search.Failed ?
            "Couldn’t search YouTube. Try again." : controller is { Busy: true, Appending: false } && controller.View != BrowseView.Home ?
            $"Loading {scope}…" : search.Videos.Count == 0 ? $"No matches in {scope}." :
            $"{search.Videos.Count} {(search.Videos.Count == 1 ? "result" : "results")} in {scope}";
        C<TextBlock>("SearchResultsStatus").Text = status;
    }

    private void UpdateSearchButtons()
    {
        bool hasText = !string.IsNullOrWhiteSpace(C<TextBox>("BrowseSearchBox").Text);
        C<Button>("BrowseSearchSubmit").IsEnabled = controller is { CanPlayFeed: true, SelectedAccount: not null } && hasText;
        C<Button>("BrowseSearchClear").Visibility = ToVisibility(search.Active || hasText);
    }

    private async Task SubmitSearchAsync(string text)
    {
        if (!CanSearch()) return;
        var operation = search.SubmitAsync(text);
        Render();
        await operation;
        Render();
    }

    private void ClearSearch()
    {
        if (closed || settingsVisible || playback.Selected is not null || controller?.SelectedAccount is null) return;
        search.Clear();
        var box = C<TextBox>("BrowseSearchBox");
        box.Text = "";
        Render();
        box.Focus(FocusState.Programmatic);
    }
}
