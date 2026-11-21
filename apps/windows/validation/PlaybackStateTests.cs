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
        await QualitySwitchCommitsOnlyWhenPrepared();
        await QualityFailuresPreserveCommittedPlayback();
        await QualityRaces();
        await QualityConfirmationFailure();
        await QualityConfirmationAfterBack();
        await QualityDeadline();
        Console.WriteLine("PASS: quality commit, rollback, lifetime transfer and stale completion rejection.");
    }
    private static async Task QualitySwitchCommitsOnlyWhenPrepared()
    {
        var original = new Handle();
        var candidate = new Handle();
        var ready = new TaskCompletionSource<Handle>();
        var originalToken = default(CancellationToken);
        var candidateToken = default(CancellationToken);
        using var player = new PlaybackController<Handle>((_, ct) => { originalToken = ct; return Task.FromResult(original); });
        var changes = new List<(bool Busy, int? Height, Handle? Active)>();
        player.Changed += () => changes.Add((player.QualityBusy, player.QualityHeight, player.Active));
        player.SetContext(true, new object(), BrowseView.History, [A, B]);
        await player.SelectAsync(A);
        changes.Clear();
        using var retiring = originalToken.Register(() => player.Fail(original));
        var operation = player.ChangeQualityAsync(original, 720, (old, height, ct) =>
        {
            StateTests.Verify(old == original && height == 720, "preparation receives exact owner and requested height");
            candidateToken = ct;
            return ready.Task;
        }, (old, next) => StateTests.Verify(player.Active == old && next == candidate && !old.Disposed,
            "commit attaches the candidate before retiring the old owner"));
        StateTests.Verify(player.QualityBusy && !player.Busy && player.QualityHeight is null && player.Active == original && !original.Disposed,
            "pending quality leaves initial playback and Auto selected");
        ready.SetResult(candidate);
        await operation;
        StateTests.Verify(player.Active == candidate && player.QualityHeight == 720 && !player.QualityBusy && player.Error is null,
            "successful quality commits the candidate and height despite old failure callback");
        StateTests.Verify(original.Disposed && originalToken.IsCancellationRequested && !candidateToken.IsCancellationRequested,
            "quality commit retires only the old lifetime");
        StateTests.Verify(changes.SequenceEqual([(true, null, original), (false, 720, candidate)]), "quality publishes pending and committed states");
        player.Back();
        StateTests.Verify(candidate.Disposed && candidateToken.IsCancellationRequested && player.QualityHeight is null,
            "Back retires the committed candidate lifetime and resets the choice");
    }
    private static async Task QualityFailuresPreserveCommittedPlayback()
    {
        var original = new Handle();
        var active = new Handle();
        var activeToken = default(CancellationToken);
        using var player = new PlaybackController<Handle>((_, _) => Task.FromResult(original));
        player.SetContext(true, new object(), BrowseView.History, [A]);
        await player.SelectAsync(A);
        await player.ChangeQualityAsync(original, 720, (_, _, ct) => { activeToken = ct; return Task.FromResult(active); }, (_, _) => { });

        foreach (var failOnCommit in new[] { false, true })
        {
            var candidate = new Handle();
            var token = default(CancellationToken);
            var commits = 0;
            await player.ChangeQualityAsync(active, null, (_, _, ct) =>
            {
                token = ct;
                return failOnCommit ? Task.FromResult(candidate) : Task.FromException<Handle>(new Exception("secret URL"));
            }, (_, _) => { commits++; throw new Exception("secret URL"); });
            StateTests.Verify(player.Active == active && player.QualityHeight == 720 && !active.Disposed && !activeToken.IsCancellationRequested,
                "failed quality change preserves the exact owner and committed manual choice");
            StateTests.Verify(token.IsCancellationRequested && commits == (failOnCommit ? 1 : 0) && candidate.Disposed == failOnCommit,
                "failed quality cancels only its own token and disposes any returned candidate");
            StateTests.Verify(!player.QualityBusy && player.Error is null && player.QualityError == "Couldn’t change video quality. Try again.",
                "quality failure is separate, sanitized, and retryable");
        }

        var ready = new TaskCompletionSource<Handle>();
        var unexpected = 0;
        Task<Handle> Unexpected(Handle _, int? __, CancellationToken ___) { unexpected++; return Task.FromResult(new Handle()); }
        await player.ChangeQualityAsync(original, 360, Unexpected, (_, _) => { });
        await player.ChangeQualityAsync(active, 720, Unexpected, (_, _) => { });
        await player.ChangeQualityAsync(active, 0, Unexpected, (_, _) => { });
        var retry = player.ChangeQualityAsync(active, null, (_, _, _) => ready.Task, (_, _) => { });
        await player.ChangeQualityAsync(active, 360, Unexpected, (_, _) => { });
        StateTests.Verify(unexpected == 0 && player.QualityBusy && player.QualityError is null, "stale, unchanged, invalid and overlapping menu selections are ignored");
        var automatic = new Handle();
        ready.SetResult(automatic);
        await retry;
        StateTests.Verify(player.Active == automatic && player.QualityHeight is null && player.QualityError is null,
            "explicit retry can commit Auto after a failure");
    }

    private static async Task QualityConfirmationFailure()
    {
        foreach (var restoreFails in new[] { false, true })
        {
            var original = new Handle();
            var candidate = new Handle();
            using var player = new PlaybackController<Handle>((_, _) => Task.FromResult(original));
            player.SetContext(true, new object(), BrowseView.History, [A]);
            await player.SelectAsync(A);
            var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = player.ChangeQualityAsync(original, 720, (_, _, _, _) => Task.FromResult(candidate), (_, _, _, _) => confirmation.Task);
            StateTests.Verify(player.Active == original && !original.Disposed && !candidate.Disposed && player.QualityBusy && player.QualityHeight is null,
                "pending attach/start confirmation retains the old owner and choice");
            confirmation.SetException(restoreFails ? new PlaybackController<Handle>.QualityRollbackException() : new InvalidOperationException());
            await operation;
            StateTests.Verify(candidate.Disposed && !player.QualityBusy, "confirmation failure retires the candidate");
            if (restoreFails)
                StateTests.Verify(original.Disposed && player.Active is null && player.Error is not null && player.QualityError is null,
                    "failed restoration must surface a playback failure, not claim the old player is recoverable");
            else
                StateTests.Verify(player.Active == original && !original.Disposed && player.Error is null && player.QualityError is not null,
                    "successful rollback preserves the old player and offers another quality choice");
        }
    }

    private static async Task QualityConfirmationAfterBack()
    {
        var original = new Handle();
        var candidate = new Handle();
        using var player = new PlaybackController<Handle>((_, _) => Task.FromResult(original));
        player.SetContext(true, new object(), BrowseView.History, [A]);
        await player.SelectAsync(A);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = player.ChangeQualityAsync(original, 720, (_, _, _, _) => Task.FromResult(candidate), async (_, _, _, accept) =>
        {
            await finish.Task;
            accept();
        });
        player.Back();
        finish.SetResult();
        await operation;
        StateTests.Verify(original.Disposed && candidate.Disposed && player.Active is null && player.Selected is null &&
            !player.QualityBusy && player.QualityHeight is null && player.QualityError is null,
            "late confirmation cannot restore playback or publish an error after Back");
    }

    private static async Task QualityDeadline()
    {
        foreach (var timeoutDuringCreation in new[] { true, false })
        {
            var original = new Handle();
            var candidate = new Handle();
            using var player = new PlaybackController<Handle>((_, _) => Task.FromResult(original));
            player.SetContext(true, new object(), BrowseView.History, [A]);
            await player.SelectAsync(A);
            CancellationToken lifetime = default, deadline = default, confirmationToken = default;
            var confirmations = 0;
            var operation = player.ChangeQualityAsync(original, 720, async (_, _, owner, operation) =>
            {
                lifetime = owner;
                deadline = operation;
                if (timeoutDuringCreation) await Task.Delay(Timeout.Infinite, operation);
                return candidate;
            }, async (_, _, operation, _) =>
            {
                confirmations++;
                confirmationToken = operation;
                await Task.Delay(Timeout.Infinite, operation);
            }, TimeSpan.FromMilliseconds(250));
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            StateTests.Verify(timeoutDuringCreation || confirmationToken == deadline, "one deadline spans creation and confirmation");
            StateTests.Verify(deadline.IsCancellationRequested && lifetime.IsCancellationRequested && confirmations == (timeoutDuringCreation ? 0 : 1),
                "deadline ends quality change during the blocked native stage");
            StateTests.Verify(candidate.Disposed != timeoutDuringCreation && player.Active == original && !original.Disposed &&
                !player.QualityBusy && player.QualityHeight is null && player.Error is null && player.QualityError is not null,
                "quality timeout keeps old playback and disposes any created candidate");
        }
    }

    private static async Task QualityRaces()
    {
        foreach (var invalidation in new[] { "next", "back", "failure", "settings", "close" })
        {
            var original = new Handle();
            var replacement = new Handle();
            var ready = new TaskCompletionSource<Handle>();
            var token = default(CancellationToken);
            var commits = 0;
            var account = new object();
            IReadOnlyList<HistoryVideo> feed = [A, B];
            using var player = new PlaybackController<Handle>((id, _) => Task.FromResult(id == A.VideoId ? original : replacement));
            player.SetContext(true, account, BrowseView.History, feed);
            await player.SelectAsync(A);
            var change = player.ChangeQualityAsync(original, 360, (_, _, ct) => { token = ct; return ready.Task; }, (_, _) => commits++);
            using var obsolete = token.Register(() => player.Fail(original));
            switch (invalidation)
            {
                case "next": await player.NextAsync(); break;
                case "back": player.Back(); break;
                case "failure": player.Fail(original); break;
                case "settings": player.SetContext(true, account, BrowseView.Settings, feed); break;
                case "close": player.Dispose(); break;
            }

            var notifications = 0;
            player.Changed += () => notifications++;
            var late = new Handle();
            ready.SetResult(late);
            await change;
            StateTests.Verify(original.Disposed && token.IsCancellationRequested && late.Disposed && commits == 0,
                "late quality preparation cannot attach after " + invalidation);
            StateTests.Verify(player.Active == (invalidation == "next" ? replacement : null) && player.QualityHeight is null
                && !player.QualityBusy && player.QualityError is null && notifications == 0 && (player.Error is not null) == (invalidation == "failure"),
                "late quality preparation cannot alter current state after " + invalidation);
        }
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
        bool failAttachment = false;
        using var player = new PlaybackController<Handle>(resolver, (media, ct) =>
        {
            attachments++;
            if (failAttachment) throw new PlaybackException("unsafe_resource", "secret URL");
            return Task.FromResult(new Handle());
        });
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
        resolver.Completion = new();
        var unavailable = player.SelectAsync(A);
        resolver.Completion.SetException(new PlaybackException("unsafe_resource", "secret URL"));
        await unavailable;
        StateTests.Verify(attachments == 1 && player.Active is null && player.Error == "Couldn’t load the video stream. Retry, or open it on YouTube.", "stream failure does not imply a visibility restriction or expose details");
        resolver.Completion = new();
        var unsupported = player.SelectAsync(A);
        resolver.Completion.SetException(new PlaybackException("unsupported_media", "secret URL"));
        await unsupported;
        StateTests.Verify(attachments == 1 && player.Error == "No compatible stream was found for native playback. Retry, or open it on YouTube.", "unsupported stream is reported before native attachment");
        failAttachment = true;
        resolver.Completion = new();
        var attachFailed = player.SelectAsync(A);
        resolver.Completion.SetResult(Media());
        await attachFailed;
        StateTests.Verify(attachments == 2 && player.Error == "Couldn’t start native playback. Retry, or open it on YouTube.", "attachment failure is not mistaken for resolver failure with the same error code");
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
    private static Task ChangeQualityAsync(this PlaybackController<Handle> player, Handle owner, int? height,
        Func<Handle, int?, CancellationToken, Task<Handle>> prepare, Action<Handle, Handle> attach) =>
        player.ChangeQualityAsync(owner, height, (old, choice, lifetime, _) => prepare(old, choice, lifetime),
            (old, candidate, _, accept) => { attach(old, candidate); accept(); return Task.CompletedTask; });

    private sealed class Handle : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
}
