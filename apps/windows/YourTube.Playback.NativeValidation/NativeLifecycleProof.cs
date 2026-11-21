using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using YourTube.App;
using YourTube.Core;
using YourTube.Playback;
using static YourTube.Playback.NativeValidation.ProofApplication;

namespace YourTube.Playback.NativeValidation;

internal static class NativeLifecycleProof
{
    public static async Task<object> CheckQualityHandoffAsync(MediaPlayerElement element, FixtureResolver resolver, Action<string> stage)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var id = FixtureResolver.PrimaryId;
        var card = new HistoryVideo(id, "Quality fixture", "YourTube validation", "", null);
        using var controller = new PlaybackController<NativePlaybackSession>(async (_, lifetime) =>
            new(await NativePlaybackSource.CreateAsync(resolver, await resolver.ResolveAsync(id, lifetime), lifetime), element));
        controller.SetContext(true, new object(), BrowseView.History, [card]);
        await controller.SelectAsync(card).WaitAsync(token);
        var previous = controller.Active ?? throw new InvalidOperationException("Missing quality fixture session.");
        var oldPlayer = previous.Prepare(muted: true);
        previous.Open();
        oldPlayer.Play();
        await Until(() => oldPlayer.PlaybackSession.Position.TotalSeconds > 2 && oldPlayer.PlaybackSession.NaturalVideoHeight == 180, previous, token);
        oldPlayer.PlaybackSession.PlaybackRate = 2;

        stage("quality-rollback-after-attachment");
        var faultInjected = false;
        var faultPosition = TimeSpan.Zero;
        await SwitchAsync(previous, 90, (prior, next, operation, _) => next.ConfirmHandoffAsync(prior, operation, () =>
        {
            faultInjected = ReferenceEquals(element.MediaPlayer, next.Player);
            faultPosition = prior.Player.PlaybackSession.Position;
            throw new InvalidOperationException("Controlled attach failure.");
        }));
        Require(faultInjected && ReferenceEquals(controller.Active, previous) && ReferenceEquals(element.MediaPlayer, oldPlayer)
            && controller.QualityError is not null && controller.Error is null
            && oldPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && oldPlayer.PlaybackSession.Position >= faultPosition + TimeSpan.FromMilliseconds(100));

        stage("quality-select-90p-while-playing");
        var manual = await ChangeAsync(previous, 90);
        var session = manual.Player.PlaybackSession;
        await Until(() => session.NaturalVideoHeight == 90 && session.PlaybackState == MediaPlaybackState.Playing, manual, token);
        Require(session.PlaybackRate == 2 && manual.Item.AudioTracks.Count > 0 && previous.TeardownCompleted);
        var manualHeight = session.NaturalVideoHeight;

        stage("quality-return-to-auto-while-paused");
        manual.Player.Pause();
        await Until(() => session.PlaybackState == MediaPlaybackState.Paused, manual, token);
        var pausedAt = session.Position;
        var auto = await ChangeAsync(manual, null);
        var adaptive = auto.Player.PlaybackSession;
        await Until(() => adaptive.NaturalVideoHeight is 90 or 180 && adaptive.PlaybackState == MediaPlaybackState.Paused && auto.Item.AudioTracks.Count > 0, auto, token);
        Require((adaptive.Position - pausedAt).Duration() <= TimeSpan.FromSeconds(1) && auto.Media.Qualities.Count == 2);
        auto.Player.Play();
        await Until(() => adaptive.PlaybackState == MediaPlaybackState.Playing && adaptive.Position > pausedAt + TimeSpan.FromSeconds(1), auto, token);
        return new { startingHeight = 180, manualHeight, autoHeight = adaptive.NaturalVideoHeight, preservedFastRate = true, autoStayedPaused = true, autoContinued = true, rollbackResumed = true };

        async Task<NativePlaybackSession> ChangeAsync(NativePlaybackSession old, int? height)
        {
            await SwitchAsync(old, height, (prior, next, operation, accept) => next.ConfirmHandoffAsync(prior, operation, () =>
            {
                Require(next.Player.PlaybackSession.Position + TimeSpan.FromSeconds(1) >= prior.Player.PlaybackSession.Position);
                accept();
            }));
            var active = controller.Active;
            Require(active is not null && !ReferenceEquals(old, active) && controller.QualityHeight == height && controller.QualityError is null && ReferenceEquals(element.MediaPlayer, active.Player));
            return active!;
        }

        Task SwitchAsync(NativePlaybackSession old, int? height, Func<NativePlaybackSession, NativePlaybackSession, CancellationToken, Action, Task> confirm) =>
            controller.ChangeQualityAsync(old, height, async (_, selected, lifetime, operation) =>
            {
                var media = await resolver.ResolveQualityAsync(id, selected, operation);
                var next = new NativePlaybackSession(await NativePlaybackSource.CreateAsync(resolver, media, lifetime, operation), element);
                try { await next.PrepareHandoffAsync(operation); return next; }
                catch { next.Dispose(); throw; }
            }, confirm).WaitAsync(token);
    }

    public static async Task<object> RunAsync(MediaPlayerElement element, IPlaybackResolver resolver,
        IReadOnlyDictionary<string, PlaybackMedia> mediaById, Action<string> stage)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var fault = new HeldReadResolver(resolver);
        var pending = new HeldReadResolver(resolver);
        var cards = mediaById.Values.Select(media => new HistoryVideo(media.VideoId, media.Title, media.Author, "", null)).ToArray();
        using var controller = new PlaybackController<NativePlaybackSession>(async (id, ct) =>
        {
            var transport = id == cards[0].VideoId ? fault : id == cards[1].VideoId ? pending : resolver;
            var media = mediaById[id];
            return new NativePlaybackSession(await NativePlaybackSource.CreateAsync(transport, media, ct), element);
        });
        controller.SetContext(true, new object(), BrowseView.History, cards);

        async Task<NativePlaybackSession> SelectAsync(HistoryVideo card)
        {
            await controller.SelectAsync(card).WaitAsync(token);
            var active = controller.Active ?? throw new InvalidOperationException("No native session.");
            active.Failed += () => controller.Fail(active);
            var player = active.Prepare(muted: true);
            active.Open();
            if (!active.HasFailed) player.Play();
            return active;
        }

        try
        {
            stage("callback-failure");
            var doomed = await SelectAsync(cards[0]);
            await Until(() => fault.Ready && doomed.OutstandingDownloads > 0, doomed, token);
            fault.Fail();
            await Until(() => controller.Active is null && controller.Error is not null && doomed.OutstandingDownloads == 0, null, token);
            var callbackFailureLatched = doomed.CallbackFailed;
            var failureTeardown = doomed.TeardownCompleted && element.MediaPlayer is null;
            Require(callbackFailureLatched && failureTeardown);

            stage("replace-pending-callback");
            var old = await SelectAsync(cards[1]);
            await Until(() => pending.Ready && old.OutstandingDownloads > 0, old, token);
            var pendingAtReplacement = !pending.Released && old.OutstandingDownloads > 0;
            var replacement = await SelectAsync(cards[2]);
            var pendingReadCanceled = pending.ReadToken.IsCancellationRequested;
            await Until(() => old.OutstandingDownloads == 0, replacement, token);
            var retiredCallbackDrained = old.OutstandingDownloads == 0 && !pending.Released && old.TeardownCompleted;
            Require(pendingAtReplacement && pendingReadCanceled && retiredCallbackDrained);

            stage("replacement-after-late-read");
            pending.Release(); // Complete the obsolete resolver Task successfully with real in-memory bytes.
            var session = replacement.Player.PlaybackSession;
            var baseline = session.Position.TotalSeconds;
            bool IsAdvancing() => session.PlaybackState == MediaPlaybackState.Playing && session.Position.TotalSeconds >= baseline + 1;
            await Until(IsAdvancing, replacement, token);
            var replacementAdvanced = IsAdvancing() && replacement.SuccessfulDownloads > 0 && ReferenceEquals(element.MediaPlayer, replacement.Player);
            var lateResultIgnored = pending.CompletedSuccessfully && old.SuccessfulDownloads == 0 && !old.HasFailed &&
                ReferenceEquals(controller.Active, replacement) && controller.Error is null;
            controller.Back();
            await Until(() => replacement.OutstandingDownloads == 0, replacement, token);
            var replacementTeardown = replacement.TeardownCompleted && element.MediaPlayer is null && controller.Active is null;
            Require(replacementAdvanced && lateResultIgnored && replacementTeardown);
            return new { callbackFailureLatched, failureTeardown, pendingAtReplacement, pendingReadCanceled, retiredCallbackDrained, lateResultIgnored, replacementAdvanced, replacementTeardown };
        }
        finally
        {
            fault.Cancel();
            pending.Cancel();
        }
    }

    // Gates one genuine AMS download after validated track bytes arrive. The exact task returned to
    // the adapter ignores cancellation, so it can complete successfully after replacement.
    private sealed class HeldReadResolver(IPlaybackResolver inner) : IPlaybackResolver
    {
        private readonly TaskCompletionSource<PlaybackResource> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PlaybackResource> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public CancellationToken ReadToken { get; private set; }
        public bool Ready => ready.Task.IsCompletedSuccessfully;
        public bool Released => completion.Task.IsCompleted;
        public bool CompletedSuccessfully => completion.Task.IsCompletedSuccessfully;
        public void Release() => completion.SetResult(ready.Task.Result);
        public void Fail() => completion.SetException(new InvalidOperationException("Injected native download failure."));
        public void Cancel() => completion.TrySetCanceled();
        public Task<PlaybackMedia> ResolveAsync(string id, CancellationToken ct = default) => inner.ResolveAsync(id, ct);
        public Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref reads) != 1) return WaitForCancellation(ct);
            _ = FetchAsync(media, uri, offset, length, ct);
            return completion.Task;
        }

        private async Task FetchAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken ct)
        {
            try
            {
                var resource = await inner.ReadResourceAsync(media, uri, offset, length, ct).ConfigureAwait(false);
                ReadToken = ct;
                ready.SetResult(resource);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); }
            catch { completion.TrySetException(new InvalidOperationException("The controlled native download did not reach its gate.")); }
        }

        private static async Task<PlaybackResource> WaitForCancellation(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException();
        }
    }
}
