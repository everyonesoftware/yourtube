using YourTube.Core;
using YourTube.Playback;

namespace YourTube.App;

// UI-thread state machine; injected resources make cancellation races testable without WinRT.
internal sealed class PlaybackController<T>(Func<string, CancellationToken, Task<T>> create) : IDisposable where T : class, IDisposable
{
    public PlaybackController(IPlaybackResolver resolver, Func<PlaybackMedia, CancellationToken, Task<T>> attach)
        : this(async (id, ct) =>
        {
            var media = await resolver.ResolveAsync(id, ct);
            ct.ThrowIfCancellationRequested();
            if (media.ExpiresAt <= DateTimeOffset.UtcNow) throw new PlaybackException("expired", "The source expired. Retry playback.");
            return await attach(media, ct);
        }) { }

    public HistoryVideo? Selected { get; private set; }
    public T? Active { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<HistoryVideo> Queue => Selected is null ? [] : feed.Where(v => v.VideoId != Selected.VideoId).ToArray();
    public event Action? Changed;
    private object? account;
    private BrowseView view;
    private IReadOnlyList<HistoryVideo> feed = [];
    private bool eligible;
    private bool disposed;
    private long generation;
    private CancellationTokenSource? cancellation;

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
        catch (Exception)
        {
            if (id == generation) Error = "Native playback is unavailable for this video. Retry, or open it on YouTube. Only public, non-DRM videos are supported.";
        }
        finally
        {
            if (id == generation) { Busy = false; Changed?.Invoke(); }
        }
    }
    public Task RetryAsync() => Selected is { } video && !Busy ? SelectAsync(video) : Task.CompletedTask;
    public Task NextAsync() => Queue.FirstOrDefault() is { } video ? SelectAsync(video) : Task.CompletedTask;
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
        var old = Active; Active = null;
        try { old?.Dispose(); }
        finally
        {
            try { pending?.Cancel(); }
            finally { pending?.Dispose(); }
        }
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
