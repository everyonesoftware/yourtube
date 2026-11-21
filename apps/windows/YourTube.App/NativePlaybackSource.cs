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
    private int firstReadRequested;
    private int deferralCompletionFailures;
    private int failed;
    public PlaybackMedia Media { get; }
    public PlaybackTiming? Timing { get; }
    public MediaSource Source { get; private set; } = null!;
    // Stable notification only: never forward WinRT errors, URLs or provider responses.
    public event Action? Failed;
    public bool HasFailed => Volatile.Read(ref failed) != 0;
    public int OutstandingDownloads => Volatile.Read(ref outstandingDownloads);
    public int SuccessfulDownloads => Volatile.Read(ref successfulDownloads);
    public bool TeardownCompleted => closedCleanly && Volatile.Read(ref deferralCompletionFailures) == 0;
    private bool downloadsStoppedCleanly;
    private bool closedCleanly;

    private NativePlaybackSource(IPlaybackResolver resolver, PlaybackMedia media, CancellationToken ct, PlaybackTiming? timing)
    {
        this.resolver = resolver; Media = media;
        Timing = timing;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        token = cancellation.Token;
    }
    public static Task<NativePlaybackSource> CreateAsync(IPlaybackResolver resolver, PlaybackMedia media, CancellationToken ct,
        PlaybackTiming? timing = null, int startupHeight = AutoStartupPreference.DefaultHeight) =>
        CreateAsync(resolver, media, ct, ct, timing: timing, startupHeight: startupHeight);

    public static async Task<NativePlaybackSource> CreateAsync(IPlaybackResolver resolver, PlaybackMedia media, CancellationToken lifetime,
        CancellationToken operation, bool auto = true, PlaybackTiming? timing = null, int startupHeight = AutoStartupPreference.DefaultHeight)
    {
        timing?.Mark(PlaybackTimingStage.NativeSourceStarted);
        var owner = new NativePlaybackSource(resolver, media, lifetime, timing);
        try
        {
            operation.ThrowIfCancellationRequested();
            if (media.ExpiresAt <= DateTimeOffset.UtcNow) throw new PlaybackException("source_expired", "The playback source expired. Retry to request a fresh source.");
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(media.Manifest.AsBuffer()).AsTask(operation).WaitAsync(operation);
            operation.ThrowIfCancellationRequested();
            stream.Seek(0);
            var creationOperation = AdaptiveMediaSource.CreateFromStreamAsync(stream, media.ManifestUri, "application/dash+xml", owner.nativeHttp);
            var creation = creationOperation.AsTask();
            AdaptiveMediaSourceCreationResult result;
            try { result = await creation.WaitAsync(operation); }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                // Cancellation stops waiting, not necessarily native creation. Never adopt its late result.
                _ = DisposeLateSourceAsync(creation);
                Attempt(creationOperation.Cancel);
                throw;
            }
            owner.adaptive = result.MediaSource;
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || owner.adaptive is null)
                throw new PlaybackException("native_source", "Windows could not open this playback source.");
            timing?.Mark(PlaybackTimingStage.NativeSourceCreated);
            owner.adaptive.DownloadRequested += owner.DownloadRequested;
            if (timing is not null)
            {
                Attempt(() => owner.adaptive.DownloadBitrateChanged += owner.DownloadBitrateChanged);
                Attempt(() => owner.adaptive.PlaybackBitrateChanged += owner.PlaybackBitrateChanged);
            }
            if (auto && startupHeight > 0) owner.ApplyAutoStartupPreference(owner.adaptive, startupHeight);
            owner.Source = MediaSource.CreateFromAdaptiveMediaSource(owner.adaptive);
            operation.ThrowIfCancellationRequested();
            return owner;
        }
        catch (OperationCanceledException) { timing?.Mark(PlaybackTimingStage.NativeSourceCanceled); owner.Dispose(); throw; }
        catch { timing?.Mark(PlaybackTimingStage.NativeSourceFailed); owner.Dispose(); throw new PlaybackException("native_source", "Windows could not open this playback source. Retry, or open YouTube."); }
    }

    private void ApplyAutoStartupPreference(AdaptiveMediaSource source, int height)
    {
        // InitialBitrate is a hint. Never restrict the adaptive ladder or make playback depend on setting it.
        try
        {
            if (AutoStartupPreference.Select(true, Media.Qualities, source.AvailableBitrates, height) is { } preferred)
            {
                source.InitialBitrate = preferred.Bitrate;
                Timing?.Mark(PlaybackTimingStage.NativeStartupHintApplied, bitrateBps: preferred.Bitrate, height: preferred.Height);
                return;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException) { }
        Timing?.Mark(PlaybackTimingStage.NativeStartupHintUnavailable);
    }

    private void DownloadBitrateChanged(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadBitrateChangedEventArgs args)
    {
        Attempt(() => Timing?.Mark(PlaybackTimingStage.NativeDownloadBitrateChanged, PositionMs(sender),
            args.NewValue, args.OldValue, reasonCode: (int)args.Reason));
    }

    private void PlaybackBitrateChanged(AdaptiveMediaSource sender, AdaptiveMediaSourcePlaybackBitrateChangedEventArgs args)
    {
        Attempt(() =>
        {
            if (!args.AudioOnly)
                Timing?.Mark(PlaybackTimingStage.NativePlaybackBitrateChanged, PositionMs(sender), args.NewValue, args.OldValue);
        });
    }

    private static long? PositionMs(AdaptiveMediaSource sender)
    {
        try { return sender.GetCorrelatedTimes().Position?.Ticks / TimeSpan.TicksPerMillisecond; }
        catch { return null; } // Correlated position may not exist before the playback clock starts.
    }

    public void MarkCurrentBitrates(long positionMs)
    {
        if (Timing is not { } timing || adaptive is not { } source) return;
        Attempt(() => timing.Mark(PlaybackTimingStage.NativeDownloadBitrateAtFirstProgress, positionMs, source.CurrentDownloadBitrate));
        Attempt(() => timing.Mark(PlaybackTimingStage.NativePlaybackBitrateAtFirstProgress, positionMs, source.CurrentPlaybackBitrate));
    }
    private static async Task DisposeLateSourceAsync(Task<AdaptiveMediaSourceCreationResult> creation)
    {
        try { (await creation.ConfigureAwait(false)).MediaSource?.Dispose(); }
        catch { /* A rejected creation has no source to release. */ }
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
            if (Timing is not null && Interlocked.Exchange(ref firstReadRequested, 1) == 0)
                Timing.Mark(PlaybackTimingStage.NativeFirstReadStarted);
            // Release the WinRT deferral on cancellation even if a read does not cooperate.
            var resource = await resolver.ReadResourceAsync(Media, args.ResourceUri, args.ResourceByteRangeOffset, args.ResourceByteRangeLength, token)
                .WaitAsync(token).ConfigureAwait(false);
            lock (downloadGate)
            {
                if (stopping || HasFailed || token.IsCancellationRequested) return;
                args.Result.ContentType = resource.ContentType;
                args.Result.Buffer = resource.Bytes.AsBuffer();
                args.Result.ExtendedStatus = 0;
                if (Interlocked.Increment(ref successfulDownloads) == 1) Timing?.Mark(PlaybackTimingStage.NativeFirstSuccessfulRead);
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
            Timing?.Mark(PlaybackTimingStage.NativeReadFailed);
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
        if (adaptive is not null && Timing is not null)
        {
            Attempt(() => adaptive.DownloadBitrateChanged -= DownloadBitrateChanged);
            Attempt(() => adaptive.PlaybackBitrateChanged -= PlaybackBitrateChanged);
        }
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
        Timing?.Mark(PlaybackTimingStage.NativeDisposed);
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
