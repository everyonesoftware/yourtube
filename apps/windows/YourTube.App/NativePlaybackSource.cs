using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using Windows.Web.Http;
using HttpRequestMessage = Windows.Web.Http.HttpRequestMessage;
using HttpResponseMessage = Windows.Web.Http.HttpResponseMessage;
using Windows.Web.Http.Filters;
using YourTube.Playback;

namespace YourTube.App;

// Source-link this file in the Windows acceptance harness. No UI or OAuth dependencies.
internal sealed class NativePlaybackSource : IDisposable
{
    private readonly IPlaybackResolver resolver;
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken token;
    private readonly Windows.Web.Http.HttpClient nativeHttp = new(new DenyNetworkFilter());
    private AdaptiveMediaSource? adaptive;
    private readonly object downloadGate = new();
    private bool stopping;
    private bool disposed;
    private int outstandingDownloads;
    private int successfulDownloads;
    private int deferralCompletionFailures;
    private int failed;
    public PlaybackMedia Media { get; }
    public MediaSource Source { get; private set; } = null!;
    // Stable notification only: never forward WinRT errors, URLs or provider responses.
    public event Action? Failed;
    public bool HasFailed => Volatile.Read(ref failed) != 0;
    public int OutstandingDownloads => Volatile.Read(ref outstandingDownloads);
    public int SuccessfulDownloads => Volatile.Read(ref successfulDownloads);
    public bool TeardownCompleted => closedCleanly && Volatile.Read(ref deferralCompletionFailures) == 0;
    private bool downloadsStoppedCleanly;
    private bool closedCleanly;

    private NativePlaybackSource(IPlaybackResolver resolver, PlaybackMedia media, CancellationToken ct)
    {
        this.resolver = resolver; Media = media;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        token = cancellation.Token;
    }
    public static async Task<NativePlaybackSource> CreateAsync(IPlaybackResolver resolver, PlaybackMedia media, CancellationToken ct)
    {
        var owner = new NativePlaybackSource(resolver, media, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (media.ExpiresAt <= DateTimeOffset.UtcNow) throw new PlaybackException("source_expired", "The playback source expired. Retry to request a fresh source.");
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(media.Manifest.AsBuffer()).AsTask(ct);
            stream.Seek(0);
            var result = await AdaptiveMediaSource.CreateFromStreamAsync(stream, media.ManifestUri, "application/dash+xml", owner.nativeHttp).AsTask(ct);
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || result.MediaSource is null)
                throw new PlaybackException("native_source", "Windows could not open this playback source.");
            owner.adaptive = result.MediaSource;
            owner.adaptive.DownloadRequested += owner.DownloadRequested;
            owner.Source = MediaSource.CreateFromAdaptiveMediaSource(owner.adaptive);
            ct.ThrowIfCancellationRequested();
            return owner;
        }
        catch (OperationCanceledException) { owner.Dispose(); throw; }
        catch { owner.Dispose(); throw new PlaybackException("native_source", "Windows could not open this playback source. Retry, or open YouTube."); }
    }
    private async void DownloadRequested(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadRequestedEventArgs args)
    {
        AdaptiveMediaSourceDownloadRequestedDeferral? deferral = null;
        var notify = false;
        Interlocked.Increment(ref outstandingDownloads);
        try
        {
            deferral = args.GetDeferral();
            // Install a failure result before any await; never leave an unhandled native fetch.
            args.Result.Buffer = Array.Empty<byte>().AsBuffer();
            args.Result.ExtendedStatus = unchecked((uint)0x80004005);
            token.ThrowIfCancellationRequested();
            if (Media.ExpiresAt <= DateTimeOffset.UtcNow) throw new OperationCanceledException();
            // Release the WinRT deferral on cancellation even if a read does not cooperate.
            var resource = await resolver.ReadResourceAsync(Media, args.ResourceUri, args.ResourceByteRangeOffset, args.ResourceByteRangeLength, token)
                .WaitAsync(token).ConfigureAwait(false);
            lock (downloadGate)
            {
                if (stopping || HasFailed || token.IsCancellationRequested) return;
                args.Result.ContentType = resource.ContentType;
                args.Result.Buffer = resource.Bytes.AsBuffer();
                args.Result.ExtendedStatus = 0;
                Interlocked.Increment(ref successfulDownloads);
            }
        }
        catch
        {
            lock (downloadGate)
            {
                if (!stopping && !token.IsCancellationRequested && !HasFailed)
                {
                    Volatile.Write(ref failed, 1);
                    notify = true;
                }
            }
        }
        finally
        {
            try { deferral?.Complete(); }
            catch { Interlocked.Increment(ref deferralCompletionFailures); }
            Interlocked.Decrement(ref outstandingDownloads);
        }
        if (notify)
        {
            try { Failed?.Invoke(); } catch { /* An observer must not escape an async WinRT callback. */ }
        }
    }
    // Called on the owner thread before disconnecting or closing the player.
    public void StopDownloads()
    {
        lock (downloadGate)
        {
            if (stopping) return;
            stopping = true;
        }
        downloadsStoppedCleanly = Attempt(() => cancellation.Cancel());
        if (adaptive is not null) downloadsStoppedCleanly &= Attempt(() => adaptive.DownloadRequested -= DownloadRequested);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopDownloads();
        closedCleanly = downloadsStoppedCleanly
            & Attempt(() => Source?.Dispose())
            & Attempt(() => adaptive?.Dispose())
            & Attempt(nativeHttp.Dispose)
            & Attempt(cancellation.Dispose);
        Failed = null;
    }
    internal static bool Attempt(Action action)
    {
        try { action(); return true; }
        catch { return false; } // Continue independent cleanup steps; the proof fails closed.
    }
    // Defense in depth: even malformed/unhandled native requests cannot bypass resolver policy,
    // follow redirects, attach OS cookies/credentials, or fetch a URL directly.
    private sealed class DenyNetworkFilter : IHttpFilter
    {
        public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request) =>
            AsyncInfo.Run<HttpResponseMessage, HttpProgress>((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        public void Dispose() { }
    }
}
