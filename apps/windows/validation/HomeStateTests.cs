using YourTube.App;
using YourTube.Core;
using static StateTests;

internal static class HomeStateTests
{
    public static async Task Run()
    {
        var recommended = new HomeShelf("Recommended", [new("aaaaaaaaaaa", "Fixture recommendation", "", "", null)]);
        var shorts = new HomeShelf("Shorts", [new("bbbbbbbbbbb", "Fixture Short", "", "", null)]);
        var extra = new HomeShelf("More videos", [new("ccccccccccc", "Fixture video", "", "", null)]);
        var api = new TestApi();
        var controller = new SessionController(api, new TestStore(), "fixture-device");
        api.Home = (account, cursor) =>
        {
            Verify(ReferenceEquals(account, TestApi.A), "Home requests use the selected identity");
            return Task.FromResult(cursor switch
            {
                null => new HomePage([recommended, shorts], "next", null),
                "next" => new HomePage([extra], "next", null),
                _ => throw new InvalidOperationException("Unexpected Home cursor.")
            });
        };
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        Verify(controller.View == BrowseView.Home && controller.HomeShelves.SequenceEqual([recommended, shorts]), "signed-in account opens Home by default with the supplied shelves");
        Verify(ReferenceEquals(controller.Videos[0], recommended.Videos[0]), "Home playback uses the displayed card instance");
        var initialHome = api.Home;
        var failedRequests = 0;
        var failure = new TaskCompletionSource<HomePage>();
        api.Home = (_, cursor) =>
        {
            Verify(cursor == "next", "Home append requests the rendered page's continuation");
            failedRequests++;
            return failure.Task;
        };
        var append = controller.LoadHistoryAsync(append: true);
        await controller.LoadHistoryAsync(append: true);
        Verify(failedRequests == 1 && controller.Busy && controller.HomeAppending && !controller.HomeAppendFailed && controller.CanPlayFeed,
            "one Home append runs at a time without blocking playback of existing rows");
        failure.SetException(new YouTubeException("network", "Could not load more rows."));
        await append;
        Verify(controller.HomeAppendFailed && !controller.Busy && !controller.HomeAppending && controller.Continuation == "next" &&
            ReferenceEquals(controller.HomeShelves[0], recommended), "a failed Home append preserves rows and the cursor for an explicit retry");
        api.Home = initialHome;
        await controller.LoadHistoryAsync(append: true);
        Verify(!controller.HomeAppendFailed && ReferenceEquals(controller.HomeShelves[0], recommended), "a successful retry clears the failure without replacing existing rows");
        Verify(controller.HomeShelves.SequenceEqual([recommended, shorts, extra]) && controller.Continuation is null, "Home appends provider shelves and stops a repeated page cursor");
        await controller.LoadHistoryAsync();
        Verify(controller.HomeShelves.SequenceEqual([recommended, shorts]) && controller.Continuation == "next", "Home refresh replaces shelves and resets pagination");
        var nextPage = new TaskCompletionSource<HomePage>();
        api.Home = (_, _) => nextPage.Task;
        using var playback = new PlaybackController<PlaybackHandle>((_, _) => Task.FromResult(new PlaybackHandle()));
        void SyncPlayback() => playback.SetContext(controller.CanPlayFeed, controller.SelectedAccount, controller.View, controller.Videos);
        controller.Changed += SyncPlayback;
        SyncPlayback();
        var pageRequest = controller.LoadHistoryAsync(append: true);
        var selected = recommended.Videos[0];
        await playback.SelectAsync(selected);
        var active = playback.Active;
        Verify(active is not null, "a displayed Home video starts while the next page is pending");
        nextPage.SetResult(new([extra], "next-empty", null));
        await pageRequest;
        Verify(ReferenceEquals(playback.Active, active) && !active!.Disposed && ReferenceEquals(playback.Selected, selected) && playback.Queue.Contains(extra.Videos[0]),
            "a successful Home append extends Up next without interrupting playback started while loading");
        api.Home = (_, _) => Task.FromResult(new HomePage([], "after-empty", null));
        await controller.LoadHistoryAsync(append: true);
        Verify(controller.HomeAppendFailed && controller.Continuation == "after-empty" && controller.HomeShelves.Count == 3 && ReferenceEquals(playback.Active, active),
            "a continuation without rows pauses automatic paging and preserves the next cursor for retry");
        var late = new TaskCompletionSource<HomePage>();
        api.Home = (_, _) => late.Task;
        var pending = controller.LoadHistoryAsync(append: true);
        await controller.NavigateAsync(BrowseView.Settings);
        Verify(playback.Active is null && active!.Disposed, "leaving Home still stops playback and disposes its source");
        late.SetResult(new([extra], "stale", null));
        await pending;
        Verify(controller.HomeShelves.Count == 0 && controller.Videos.Count == 0 && controller.Continuation is null && !controller.Busy && !controller.HomeAppendFailed, "late Home response cannot restore private shelves after navigation");
        Console.WriteLine("PASS: Home default route, card identity, shelf pagination, refresh and stale results.");
    }

    private sealed class PlaybackHandle : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
