using YourTube.App;
using YourTube.Core;

internal static class SearchStateTests
{
    public static async Task Run()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<IReadOnlyList<HistoryVideo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int publicRequests = 0;
        var owner = new object();
        var other = new object();
        var first = new HistoryVideo("abcdefghijk", "Northern Lights", "Field Notes", "1:00", 42);
        var second = new HistoryVideo("abcdefghij0", "Field recording", "Morning Show", "2:00", null);
        var third = new HistoryVideo("abcdefghij1", "Aurora", "Field Notes", "3:00", null);
        using var search = new ScopedSearch(async (_, _) => { publicRequests++; entered.TrySetResult(); return await reply.Task; });
        search.Bind(owner, BrowseView.History, [first, second]);
        await search.SubmitAsync(" FIELD lights ");
        Check(search.Videos.SequenceEqual([first]) && ReferenceEquals(search.Videos[0], first), "History only matches loaded titles/bylines, retaining progress and card ownership");
        using (var playback = new PlaybackController<SearchPlayback>((_, _) => Task.FromResult(new SearchPlayback())))
        {
            var append = new TaskCompletionSource<IReadOnlyList<HistoryVideo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task ReceiveAppendAsync()
            {
                search.Bind(owner, BrowseView.History, await append.Task);
                playback.SetContext(true, owner, BrowseView.History, search.Videos);
            }
            var appendTask = ReceiveAppendAsync();
            playback.SetContext(true, owner, BrowseView.History, search.Videos);
            await playback.SelectAsync(first);
            var active = playback.Active;
            Check(active is not null, "search result opens playback while the feed is appending");
            append.SetResult([first, second, third]);
            await appendTask;
            Check(ReferenceEquals(playback.Selected, first) && ReferenceEquals(playback.Active, active), "a late nonmatching page must not close Theater");
        }
        search.Bind(owner, BrowseView.Subscriptions, [second]);
        Check(!search.Active, "new route discards previous query");
        await search.SubmitAsync("Field");
        Check(search.Videos.SequenceEqual([second]) && publicRequests == 0, "subscription filtering never sends the query outside the app");
        search.Bind(owner, BrowseView.Subscriptions, [third]);
        Check(search.Videos.SequenceEqual([third]), "channel change projects the same query against the new feed only");
        search.Bind(owner, BrowseView.Home, [first]);
        var pending = search.SubmitAsync("Aurora");
        await entered.Task;
        Check(publicRequests == 1 && search.Busy, "Home explicitly uses public search");
        search.Bind(other, BrowseView.Home, []);
        reply.SetResult([third]);
        await pending;
        Check(!search.Active && !search.Busy && search.Videos.Count == 0, "late public results cannot cross an identity change");
        await search.SubmitAsync("Aurora");
        Check(search.Active && search.Videos.SequenceEqual([third]), "current public results are usable and not limited to Home feed");
        search.Clear();
        Check(!search.Active && search.Videos.Count == 0, "clear restores browse mode");
        Console.WriteLine("PASS: scoped search matches membership, never sends private-feed queries, and rejects late results.");
    }

    private sealed class SearchPlayback : IDisposable { public void Dispose() { } }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
