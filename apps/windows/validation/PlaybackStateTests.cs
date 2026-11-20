using YourTube.App;
using YourTube.Core;
using YourTube.Playback;

internal static class PlaybackStateTests
{
    private static readonly HistoryVideo A = new("aaaaaaaaaaa", "A", "Author", "1:00", null);
    private static readonly HistoryVideo B = new("bbbbbbbbbbb", "B", "Author", "2:00", null);
    public static async Task Run()
    {
        var handle = new Handle();
        using var player = new PlaybackController<Handle>((_, _) => Task.FromResult(handle));
        player.SetContext(true, new object(), BrowseView.History, [A, B]);
        await player.SelectAsync(A);
        StateTests.Verify(player.Selected == A && ReferenceEquals(player.Active, handle), "actual current feed selection opens native resource");
        StateTests.Verify(player.Queue.SequenceEqual([B]), "Up next is actual feed excluding current item");
        player.Back();
        StateTests.Verify(handle.Disposed && player.Selected is null, "Back disposes playback and restores browse");
        await player.NextAsync();
        StateTests.Verify(player.Selected is null, "Next cannot activate a hidden player after Back");
        Console.WriteLine("PASS: playback selection, actual-feed queue and Back disposal.");
        await Races();
        await ReplacementRetiresOwnerBeforeCancellation();
        await ResolverContract();
    }
    private static async Task ReplacementRetiresOwnerBeforeCancellation()
    {
        var old = new Handle();
        var replacement = new Handle();
        PlaybackController<Handle> player = null!;
        var retiredBeforeCancellation = false;
        player = new PlaybackController<Handle>((id, ct) =>
        {
            if (id != A.VideoId) return Task.FromResult(replacement);
            ct.Register(() =>
            {
                retiredBeforeCancellation = old.Disposed && player.Active is null;
                player.Fail(old); // An obsolete cancellation callback must not reenter Stop.
            });
            return Task.FromResult(old);
        });
        using var lifetime = player;
        player.SetContext(true, new object(), BrowseView.History, [A, B]);
        await player.SelectAsync(A);
        await player.SelectAsync(B);
        StateTests.Verify(retiredBeforeCancellation, "old native owner is retired and disposed before external cancellation can report failure");
        StateTests.Verify(old.Disposed && player.Active == replacement, "replacement uses the old owner's disposal path");
    }
    private static async Task Races()
    {
        foreach (var invalidation in new[] { "account", "settings", "navigation", "feed", "signout", "back", "close", "reselect" })
        {
            var late = new TaskCompletionSource<Handle>();
            var calls = 0;
            CancellationToken captured = default;
            var account = new object();
            IReadOnlyList<HistoryVideo> feed = [A, B];
            using var player = new PlaybackController<Handle>((_, ct) => { captured = ct; return ++calls == 1 ? late.Task : Task.FromResult(new Handle()); });
            player.SetContext(true, account, BrowseView.History, feed);
            var task = player.SelectAsync(A);
            var oldToken = captured;
            switch (invalidation)
            {
                case "account": player.SetContext(true, new object(), BrowseView.History, feed); break;
                case "settings": player.SetContext(true, account, BrowseView.Settings, feed); break;
                case "navigation": player.SetContext(true, account, BrowseView.Subscriptions, feed); break;
                case "feed": player.SetContext(true, account, BrowseView.History, [B]); break;
                case "signout": player.SetContext(false, null, BrowseView.History, []); break;
                case "back": player.Back(); break;
                case "close": player.Dispose(); break;
                case "reselect": await player.SelectAsync(B); break;
            }
            var stale = new Handle(); late.SetResult(stale); await task;
            StateTests.Verify(stale.Disposed && oldToken.IsCancellationRequested && !player.Busy, "stale native resource disposed and loading reset after " + invalidation);
            StateTests.Verify(player.Selected == (invalidation == "reselect" ? B : null), "stale selection cannot return after " + invalidation);
        }
        var attempts = 0;
        var current = new object();
        using var single = new PlaybackController<Handle>((_, _) => ++attempts == 1 ? Task.FromException<Handle>(new Exception("secret URL")) : Task.FromResult(new Handle()));
        single.SetContext(true, current, BrowseView.History, [A]);
        await single.SelectAsync(B);
        await single.SelectAsync(A with { Title = "stale copy" });
        StateTests.Verify(attempts == 0, "removed and copied cards cannot activate");
        await single.SelectAsync(A);
        StateTests.Verify(single.Error is not null && !single.Error.Contains("secret") && attempts == 1, "safe failure has no automatic retry loop");
        StateTests.Verify(single.Queue.Count == 0, "single video has no invented Up next");
        await single.NextAsync();
        StateTests.Verify(attempts == 1, "single video Next does not loop");
        await single.RetryAsync();
        var resource = single.Active!;
        single.Fail(new Handle());
        StateTests.Verify(single.Active == resource, "old native failure cannot stop replacement");
        single.Fail(resource);
        StateTests.Verify(resource.Disposed && single.Error is not null && attempts == 2, "native error offers explicit bounded retry");
        await single.RetryAsync();
        StateTests.Verify(single.Active is not null && attempts == 3, "single video retry requests fresh source");
        single.Back();
        await single.RetryAsync();
        StateTests.Verify(attempts == 3, "hidden retry cannot resurrect playback");
        Console.WriteLine("PASS: playback cancellation/stale completion matrix, membership, single-item retry and safe failures.");
    }
    private static async Task ResolverContract()
    {
        var resolver = new Resolver();
        var attachments = 0;
        using var player = new PlaybackController<Handle>(resolver, (media, ct) => { attachments++; return Task.FromResult(new Handle()); });
        var account = new object();
        IReadOnlyList<HistoryVideo> feed = [A];
        player.SetContext(true, account, BrowseView.History, feed);
        var pending = player.SelectAsync(A);
        player.Back();
        resolver.Completion.SetResult(Media());
        await pending;
        StateTests.Verify(attachments == 0 && resolver.Token.IsCancellationRequested, "late resolver cannot even create native source after Back");
        StateTests.Verify(resolver.Id == A.VideoId && resolver.Reads == 0, "selection passes only public ID to exact resolver API");
        resolver.Completion = new();
        var ready = player.SelectAsync(A);
        resolver.Completion.SetResult(Media()); await ready;
        StateTests.Verify(attachments == 1 && player.Active is not null, "resolved source attaches once through production factory");
        resolver.Completion = new();
        var expired = player.SelectAsync(A);
        resolver.Completion.SetResult(new PlaybackMedia(A.VideoId, "Expired", "Author", "", TimeSpan.FromMinutes(1), DateTimeOffset.MinValue, [], new Uri("https://www.youtube.com/yourtube-playback.mpd"), []));
        await expired;
        StateTests.Verify(attachments == 1 && player.Active is null && player.Error is not null, "expired resolver result offers retry without creating native source or looping");
        Console.WriteLine("PASS: frozen IPlaybackResolver contract and canceled-resolution native-creation guard.");
    }
    private static PlaybackMedia Media() => new(A.VideoId, "Resolved title", "Resolved author", "Resolved description", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1), [], new Uri("https://www.youtube.com/yourtube-playback.mpd"), []);
    private sealed class Resolver : IPlaybackResolver
    {
        public TaskCompletionSource<PlaybackMedia> Completion = new();
        public CancellationToken Token;
        public string? Id;
        public int Reads;
        public Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken = default) { Id = videoId; Token = cancellationToken; return Completion.Task; }
        public Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken cancellationToken = default) { Reads++; throw new InvalidOperationException("state tests must not read native media"); }
    }
    private sealed class Handle : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
}
