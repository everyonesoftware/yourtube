using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using YourTube.App;
using YourTube.Core;
using YourTube.Playback;
using static YourTube.Playback.NativeValidation.ProofApplication;

namespace YourTube.Playback.NativeValidation;

internal static class NativeLifecycleProof
{
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

    // Gates one genuine AMS download after validated CDN bytes arrive. The exact task returned to
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
