using YourTube.App;
using YourTube.Core;
using static StateTests;

internal static class SubscriptionStateTests
{
    internal static readonly SubscribedChannel Channel = new("Fixture channel", true);
    internal static HistoryVideo Video(string id) => new(id, "Fixture subscription", "Fixture channel", "1:00", null);

    public static async Task Run()
    {
        var api = new TestApi();
        var store = new TestStore();
        var controller = new SessionController(api, store, "fixture-device");
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        api.Subscriptions = (account, cursor) => {
            Verify(account == TestApi.A && cursor is null, "subscriptions use the same selected identity and initial request");
            return Task.FromResult(new SubscriptionsPage([Channel], [Video("aaaaaaaaaaa")], "next-page", null));
        };
        await controller.NavigateAsync(BrowseView.Subscriptions);
        Verify(controller.View == BrowseView.Subscriptions && controller.Channels.SequenceEqual([Channel]) && controller.Videos.Single().VideoId == "aaaaaaaaaaa", "subscriptions navigation loads both subscribed channels and latest videos");
        Verify(!controller.Busy && controller.Continuation == "next-page", "subscriptions exposes provider pagination");
        Console.WriteLine("PASS: subscription navigation loads channels and latest videos for the active account.");
        var delayed = new TaskCompletionSource<SubscriptionsPage>();
        api.Subscriptions = (_, _) => delayed.Task;
        var switching = controller.SelectAccountAsync(TestApi.B);
        Verify(controller.Channels.Count == 0 && controller.Videos.Count == 0 && controller.Continuation is null, "switching subscription identity immediately clears previous private data");
        controller.SignOut();
        delayed.SetResult(new([Channel], [Video("bbbbbbbbbbb")], "late-page", null));
        await switching;
        Verify(controller.Channels.Count == 0 && controller.Videos.Count == 0 && controller.Continuation is null && !controller.Busy && store.Value is null, "late subscription response cannot resurrect sign-out data or credentials");
        Console.WriteLine("PASS: account switching and sign-out clear subscription data and reject late results.");
        await Pagination();
        await ChannelSelection();
        await FlatFeedBackgroundPaging(BrowseView.Subscriptions);
        await FlatFeedBackgroundPaging(BrowseView.History);
        await NavigationAndFailures();
        await AccountAndRevocation();
    }

    private static async Task ChannelSelection()
    {
        var api = new TestApi();
        var controller = new SessionController(api, new TestStore(), "fixture-device");
        SubscribedChannel[] channels = [new("Same name", false), new("Same name", false)];
        var all = Video("aaaaaaaaaaa");
        var first = Video("bbbbbbbbbbb") with { Channel = "Unrelated display byline" };
        var second = Video("ccccccccccc");
        var late = new TaskCompletionSource<SubscriptionsPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string?>();
        api.Subscriptions = (_, cursor) =>
        {
            Verify(cursor is null, "returning to All cannot send a channel cursor");
            return Task.FromResult(new SubscriptionsPage(channels, [all], "all-cursor", null));
        };
        api.SubscriptionChannel = (account, channel, cursor) =>
        {
            Verify(account == TestApi.A, "channel request retains the active account");
            requests.Add(cursor);
            return ReferenceEquals(channel, channels[0]) ? late.Task : Task.FromResult(cursor is null
                ? new SubscriptionsPage([], [first], "channel-cursor", null)
                : new SubscriptionsPage([], [second], null, null));
        };
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        await controller.NavigateAsync(BrowseView.Subscriptions);
        await controller.SelectSubscriptionChannelAsync(new("Same name", false));
        Verify(requests.Count == 0, "a matching name is not an authorized channel selection");
        var older = controller.SelectSubscriptionChannelAsync(channels[0]);
        await controller.SelectSubscriptionChannelAsync(channels[1]);
        Verify(ReferenceEquals(controller.SelectedSubscriptionChannel, channels[1]) && controller.Videos.SequenceEqual([first]), "selection uses the exact tab and its provider videos, not the byline");
        late.SetResult(new([], [all], "obsolete-cursor", null));
        await older;
        await controller.LoadHistoryAsync(append: true);
        Verify(requests.SequenceEqual(new string?[] { null, null, "channel-cursor" }) && controller.Videos.SequenceEqual([first, second]) && ReferenceEquals(controller.Channels, channels), "channel paging ignores obsolete responses and retains the rail");
        await controller.SelectSubscriptionChannelAsync(null);
        Verify(controller.SelectedSubscriptionChannel is null && controller.Videos.SequenceEqual([all]) && controller.Continuation == "all-cursor", "All Subscriptions resets the grid and its cursor");
        Console.WriteLine("PASS: exact channel selection, stale response rejection, channel paging and aggregate reset.");
    }

    private static async Task FlatFeedBackgroundPaging(BrowseView view)
    {
        var api = new TestApi();
        var controller = new SessionController(api, new TestStore(), "fixture-device");
        var original = Video("aaaaaaaaaaa");
        var added = Video("bbbbbbbbbbb");
        var page = new TaskCompletionSource<HistoryPage>();
        var requests = new List<string>();
        Task<HistoryPage> ReadPage(string? cursor)
        {
            if (cursor is null) return Task.FromResult(new HistoryPage([original], "second", null));
            requests.Add(cursor);
            return page.Task;
        }
        api.History = (_, cursor) => ReadPage(cursor);
        api.Subscriptions = async (_, cursor) =>
        {
            var result = await ReadPage(cursor);
            return new([cursor is null ? Channel : new("Unexpected channel", false)], result.Videos, result.Continuation, result.Message);
        };
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        await controller.NavigateAsync(view);
        var channels = controller.Channels;
        var pending = controller.LoadHistoryAsync(append: true);
        await controller.LoadHistoryAsync(append: true);
        Verify(controller.Busy && controller.CanPlayFeed && requests.SequenceEqual(["second"]), $"{view} keeps existing cards playable and coalesces paging while the next page is pending");
        page.SetException(new YouTubeException("network", "Could not load the next page."));
        await pending;
        Verify(!controller.Busy && controller.AppendFailed && controller.Continuation == "second" && ReferenceEquals(controller.Videos.Single(), original), $"{view} retains cards and cursor and pauses automatic paging on failure");
        using var playback = new PlaybackController<PlaybackHandle>((_, _) => Task.FromResult(new PlaybackHandle()));
        void SyncPlayback() => playback.SetContext(controller.CanPlayFeed, controller.SelectedAccount, controller.View, controller.Videos);
        controller.Changed += SyncPlayback;
        SyncPlayback();
        page = new();
        pending = controller.LoadHistoryAsync(append: true);
        await playback.SelectAsync(original);
        var source = playback.Active;
        Verify(source is not null, $"{view} starts playback from an existing card while a page is pending");
        page.SetResult(new([original, added], "third", null));
        await pending;
        Verify(ReferenceEquals(playback.Active, source) && !source!.Disposed && ReferenceEquals(playback.Selected, original) &&
            playback.Queue.SequenceEqual([added]), $"{view} appending a page updates Up next without interrupting playback");
        Verify(!controller.AppendFailed && controller.Videos.SequenceEqual([original, added]) && ReferenceEquals(controller.Videos[0], original) && ReferenceEquals(controller.Channels, channels), $"{view} resumes paging on retry and appends distinct cards without replacing existing cards or channels");
        page = new();
        pending = controller.LoadHistoryAsync(append: true);
        page.SetResult(new([added], "fourth", null));
        await pending;
        Verify(controller.AppendFailed && controller.Continuation == "fourth" && controller.Videos.Count == 2, $"{view} pauses automatic paging but preserves a usable cursor after a duplicate-only page");
        page = new();
        pending = controller.LoadHistoryAsync(append: true);
        page.SetResult(new([], "second", null));
        await pending;
        Verify(!controller.AppendFailed && controller.Continuation is null && requests.SequenceEqual(["second", "second", "third", "fourth"]), $"{view} stops on a cyclic cursor and hides retry");
        Console.WriteLine($"PASS: {view} background pagination, retry, card identity and cursor cycles.");
    }

    private sealed class PlaybackHandle : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static async Task NavigationAndFailures()
    {
        var api = new TestApi();
        var store = new TestStore();
        var controller = new SessionController(api, store, "fixture-device");
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        var late = new TaskCompletionSource<SubscriptionsPage>();
        api.Subscriptions = (_, _) => late.Task;
        var loading = controller.NavigateAsync(BrowseView.Subscriptions);
        Verify(controller.Busy && controller.Status.Contains("subscriptions", StringComparison.OrdinalIgnoreCase), "subscription loading status identifies the requested data");
        await controller.NavigateAsync(BrowseView.Settings);
        Verify(!controller.Busy && controller.Videos.Count == 0 && controller.Channels.Count == 0, "Settings navigation cancels the pending browse and clears busy state");
        var historyCalls = 0;
        api.History = (_, _) => { historyCalls++; return Task.FromResult(new HistoryPage([], null, null)); };
        await controller.LoadHistoryAsync();
        await controller.SelectAccountAsync(TestApi.B);
        Verify(historyCalls == 0 && controller.SelectedAccount == TestApi.A, "hidden browse actions cannot issue requests in Settings");
        late.SetResult(new([Channel], [Video("aaaaaaaaaaa")], "stale", null));
        await loading;
        Verify(controller.View == BrowseView.Settings && controller.Videos.Count == 0 && controller.Continuation is null && !controller.Busy, "late subscription completion cannot restore a hidden feed");
        api.Subscriptions = (_, _) => Task.FromResult(new SubscriptionsPage([Channel], [Video("bbbbbbbbbbb")], "retry-page", null));
        await controller.NavigateAsync(BrowseView.Subscriptions);
        api.Subscriptions = (_, _) => throw new YouTubeException("service_unavailable", "YouTube is temporarily unavailable.");
        await controller.LoadHistoryAsync(true);
        Verify(controller.Phase == SessionPhase.SignedIn && controller.Channels.Count == 1 && controller.Videos.Count == 1 && controller.Continuation == "retry-page" && !controller.Busy, "retryable subscription error retains current data and cursor");
        api.Subscriptions = (_, _) => Task.FromResult(new SubscriptionsPage([], [], null, "No more subscription videos were returned."));
        await controller.LoadHistoryAsync(true);
        Verify(controller.Status == "No more subscription videos were returned." && controller.Continuation is null, "retry succeeds and shows honest empty completion");
        controller.UtcNow = () => DateTimeOffset.UtcNow.AddHours(2);
        var refresh = new TaskCompletionSource<YouTubeSession>();
        api.Refresh = () => refresh.Task;
        var refreshing = controller.LoadHistoryAsync();
        controller.SignOut();
        var saves = store.Saves;
        refresh.SetResult(TestApi.Fresh);
        await refreshing;
        Verify(store.Value is null && store.Saves == saves && controller.Channels.Count == 0 && !controller.Busy, "late subscription refresh cannot restore vault or private data after sign-out");
        Console.WriteLine("PASS: subscriptions handle Settings navigation, stale responses, safe retry, empty pages and delayed credential refresh.");
    }

    private static async Task AccountAndRevocation()
    {
        var api = new TestApi();
        var store = new TestStore();
        var controller = new SessionController(api, store, "fixture-device");
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        var old = new TaskCompletionSource<SubscriptionsPage>();
        api.Subscriptions = (account, _) => account == TestApi.A ? old.Task : Task.FromResult(new SubscriptionsPage([Channel], [Video("bbbbbbbbbbb")], "b-cursor", null));
        var loading = controller.NavigateAsync(BrowseView.Subscriptions);
        await controller.SelectAccountAsync(TestApi.B);
        old.SetResult(new([new("Old channel", false)], [Video("aaaaaaaaaaa")], "a-cursor", null));
        await loading;
        Verify(controller.SelectedAccount == TestApi.B && controller.Videos.Single().VideoId == "bbbbbbbbbbb" && controller.Continuation == "b-cursor" && controller.Channels.Single() == Channel, "old identity subscription response cannot replace the new identity's channels, videos or cursor");
        api.Subscriptions = (_, _) => throw new YouTubeException("auth_required", "Sign in to YouTube again.");
        await controller.LoadHistoryAsync();
        Verify(controller.Phase == SessionPhase.SignedOut && controller.Accounts.Count == 0 && controller.SelectedAccount is null && controller.Channels.Count == 0 && controller.Videos.Count == 0 && controller.Continuation is null && store.Value is null, "subscription authorization rejection removes all private data and saved login");
        Console.WriteLine("PASS: stale subscription identity results are rejected and authentication revocation clears all private state.");
    }

    private static async Task Pagination()
    {
        var api = new TestApi();
        var controller = new SessionController(api, new TestStore(), "fixture-device");
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        api.Subscriptions = (_, cursor) => Task.FromResult(cursor switch {
            null => new SubscriptionsPage([Channel], [Video("aaaaaaaaaaa")], "page2", null),
            "page2" => new SubscriptionsPage([], [Video("aaaaaaaaaaa"), Video("bbbbbbbbbbb")], "page3", null),
            _ => new SubscriptionsPage([], [Video("ccccccccccc")], "page2", null)
        });
        await controller.NavigateAsync(BrowseView.Subscriptions);
        await controller.LoadHistoryAsync(append: true);
        Verify(controller.Videos.Count == 2 && controller.Channels.Count == 1 && controller.Continuation == "page3", "subscription pagination appends distinct videos while retaining subscribed channels");
        await controller.LoadHistoryAsync(append: true);
        Verify(controller.Videos.Count == 3 && controller.Continuation is null, "cyclic subscription cursor stops pagination rather than loading forever");
        await controller.LoadHistoryAsync();
        Verify(controller.Videos.Count == 1 && controller.Channels.Count == 1 && controller.Continuation == "page2", "subscription refresh replaces old pages and resets cursor protection");
        Console.WriteLine("PASS: subscription pagination retains channels, deduplicates cards, stops cursor cycles and resets on refresh.");
    }
}
