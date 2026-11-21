using YourTube.Core;
using YourTube.Playback;

namespace YourTube.App;

// UI-thread state machine; injected resources make cancellation races testable without WinRT.
internal sealed class PlaybackController<T>(Func<string, CancellationToken, Task<T>> create) : IDisposable where T : class, IDisposable
{
    public PlaybackController(IPlaybackResolver resolver, Func<PlaybackMedia, CancellationToken, Task<T>> attach)
        : this(async (id, ct) =>
        {
            PlaybackMedia media;
            try
            {
                media = await resolver.ResolveAsync(id, ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                throw new StreamLoadFailure(error is PlaybackException { Code: "unsupported_media" }
                    ? "No compatible stream was found for native playback. Retry, or open it on YouTube."
                    : "Couldn’t load the video stream. Retry, or open it on YouTube.");
            }
            if (media.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new StreamLoadFailure("The video stream expired. Retry, or open it on YouTube.");
            return await attach(media, ct);
        }) { }

    private sealed class StreamLoadFailure(string message) : Exception(message);

    public HistoryVideo? Selected { get; private set; }
    public T? Active { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public bool QualityBusy => qualityCancellation is not null;
    public int? QualityHeight { get; private set; }
    public string? QualityError { get; private set; }
    public IReadOnlyList<HistoryVideo> Queue => Selected is null ? [] : feed.Where(v => v.VideoId != Selected.VideoId).ToArray();
    public event Action? Changed;
    private object? account;
    private BrowseView view;
    private IReadOnlyList<HistoryVideo> feed = [];
    private bool eligible;
    private bool disposed;
    private long generation;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? qualityCancellation;

    public void SetContext(bool eligible, object? account, BrowseView view, IReadOnlyList<HistoryVideo> feed)
    {
        if (disposed) return;
        if (!eligible || !ReferenceEquals(this.account, account) || this.view != view || !ReferenceEquals(this.feed, feed)) Back();
        this.eligible = eligible && account is not null && view != BrowseView.Settings;
        this.account = account; this.view = view; this.feed = feed;
    }
    private bool CanSelect(HistoryVideo video) => !disposed && eligible && feed.Any(v => ReferenceEquals(v, video)) && BrowserPolicy.VideoUri(video.VideoId) is not null;
    public async Task SelectAsync(HistoryVideo video)
    {
        if (!CanSelect(video)) return;
        Stop();
        Selected = video; Busy = true; Error = null;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var id = generation;
        Changed?.Invoke();
        try
        {
            var resource = await create(video.VideoId, token);
            if (id != generation || !CanSelect(video)) { resource.Dispose(); return; }
            Active = resource;
        }
        catch (Exception error)
        {
            if (id == generation) Error = error is StreamLoadFailure streamError
                ? streamError.Message
                : "Couldn’t start native playback. Retry, or open it on YouTube.";
        }
        finally
        {
            if (id == generation) { Busy = false; Changed?.Invoke(); }
        }
    }
    public Task RetryAsync() => Selected is { } video && !Busy ? SelectAsync(video) : Task.CompletedTask;
    public Task NextAsync() => Queue.FirstOrDefault() is { } video ? SelectAsync(video) : Task.CompletedTask;

    internal sealed class QualityRollbackException : Exception;

    // Preparation must not disturb the visible player. Confirmation owns native rollback and calls
    // accept synchronously only after the new attachment is ready; until then the old owner stays active.
    public async Task ChangeQualityAsync(T expected, int? height, Func<T, int?, CancellationToken, CancellationToken, Task<T>> prepare,
        Func<T, T, CancellationToken, Action, Task> confirm, TimeSpan? timeout = null)
    {
        if (Busy || QualityBusy || Selected is not { } video || !CanSelect(video)
            || !ReferenceEquals(Active, expected) || height is <= 0 || height == QualityHeight) return;

        var id = generation;
        var pending = new CancellationTokenSource();
        var token = pending.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        qualityCancellation = pending;
        QualityError = null;
        var oldCancellation = cancellation;
        T? candidate = null;
        bool OwnsSwitch() => id == generation && ReferenceEquals(qualityCancellation, pending) && ReferenceEquals(Active, expected);
        try
        {
            Changed?.Invoke();
            if (!OwnsSwitch()) return;
            var operation = deadline.Token;
            candidate = await prepare(expected, height, token, operation);
            if (ReferenceEquals(candidate, expected)) candidate = null;
            if (!OwnsSwitch()) return;
            operation.ThrowIfCancellationRequested();
            if (candidate is null) throw new InvalidOperationException();
            await confirm(expected, candidate, operation, () =>
            {
                if (!OwnsSwitch()) throw new OperationCanceledException();
                operation.ThrowIfCancellationRequested();

                // Transfer ownership before disposal/cancellation can report an old native failure.
                cancellation = pending;
                qualityCancellation = null;
                Active = candidate;
                QualityHeight = height;
                candidate = null;
            });
            if (candidate is not null) throw new InvalidOperationException();
        }
        catch (QualityRollbackException)
        {
            try { candidate?.Dispose(); }
            finally
            {
                candidate = null;
                if (OwnsSwitch()) Fail(expected);
            }
            return;
        }
        catch (Exception)
        {
            if (OwnsSwitch()) QualityError = "Couldn’t change video quality. Try again.";
            return;
        }
        finally
        {
            try { candidate?.Dispose(); }
            finally
            {
                if (OwnsSwitch())
                {
                    qualityCancellation = null;
                    try { Cancel(pending); }
                    finally { if (id == generation && !QualityBusy) Changed?.Invoke(); }
                }
            }
        }

        try { expected.Dispose(); }
        finally
        {
            try { Cancel(oldCancellation); }
            finally { if (id == generation && ReferenceEquals(cancellation, pending)) Changed?.Invoke(); }
        }
    }

    public void Fail(T source)
    {
        if (!ReferenceEquals(source, Active)) return;
        Stop(); Busy = false;
        Error = "Playback stopped or its source expired. Retry to request a fresh source, or open YouTube.";
        Changed?.Invoke();
    }
    private void Stop()
    {
        generation++;
        var pending = cancellation; cancellation = null;
        var switching = qualityCancellation; qualityCancellation = null;
        var old = Active; Active = null;
        QualityHeight = null; QualityError = null;
        try { old?.Dispose(); }
        finally
        {
            try { Cancel(switching); }
            finally { Cancel(pending); }
        }
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        finally { source?.Dispose(); }
    }
    public void Back()
    {
        var notify = Selected is not null || Active is not null || Busy || Error is not null;
        Stop(); Selected = null; Busy = false; Error = null;
        if (notify) Changed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; eligible = false; Back(); feed = []; account = null; Changed = null;
    }
}
