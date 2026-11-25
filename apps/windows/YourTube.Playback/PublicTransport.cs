namespace YourTube.Playback;

// Dedicated public-only transport: never shares the account client's handler or defaults.
internal sealed class PublicTransport(HttpMessageHandler handler, TimeSpan? requestBudget = null) : HttpMessageHandler
{
    private static bool IsTerminalProbe(HttpMethod method, HttpResponseMessage response) =>
        response.StatusCode == System.Net.HttpStatusCode.NotFound || response.IsSuccessStatusCode &&
        (method != HttpMethod.Head || response.Content.Headers.ContentLength is null);

    private readonly HttpMessageInvoker inner = new(handler);
    // Reserve most of the normal HttpClient budget for an on-demand retry.
    // Its timer starts before the handler dispatches the first probe.
    private readonly TimeSpan speculativeBudget = (requestBudget ?? PlaybackResolver.RequestTimeout) / 4;
    private sealed class DiscoveryCapture(string id, PlaybackTiming? timing, int limit, TimeSpan probeBudget, CancellationToken token)
    {
        internal string Id { get; } = id;
        internal PlayerMetadata? Latest { get; set; }
        internal Storyboard? Storyboard { get; set; }
        internal bool MeasuresProgress => timing?.TracksStartup == true;
        internal bool CanPrefetch => limit > 1;
        internal ProbeWindow? Window { get; private set; }
        internal ReachabilityMeter? Meter { get; } = timing?.TracksStartup == true ? new(timing, limit) : null;
        internal ReachabilityFallback Fallback { get; set; } = ReachabilityFallback.NoPlayerResponse;
        private readonly object progressLock = new();
        private HashSet<string>? remaining;
        private int total;
        private int probing;
        internal void BeginPlayerResponse(HashSet<string>? candidates, Uri[]? probes, ReachabilityFallback fallback, int repeatedFormatEntries)
        {
            Fallback = fallback;
            remaining = candidates;
            total = candidates?.Count ?? 0;
            Meter?.RecordPlan(probes is not null ? repeatedFormatEntries : null);
            PublishProgress(total > 0 ? new(0, total) : null);
            if (probes is not null) Window = new(probes, limit, probeBudget, token);
        }

        internal async Task DrainAsync()
        {
            if (Window is not { } window) return;
            Window = null;
            await window.DisposeAsync();
        }

        // Checks can complete out of order. Publish their count under the same
        // lock used to deduplicate, using the initiating URL rather than redirects.
        internal void ObserveProbe(Uri origin, HttpMethod method, HttpResponseMessage response)
        {
            lock (progressLock) ObserveProbeCore(origin, method, response);
        }

        private void ObserveProbeCore(Uri origin, HttpMethod method, HttpResponseMessage response)
        {
            if (remaining is null) return;
            if (!IsTerminalProbe(method, response)) return;
            var key = origin.AbsoluteUri;
            if (method == HttpMethod.Get)
            {
                int range = key.LastIndexOf("&range=", StringComparison.Ordinal);
                if (range < 0) range = key.IndexOf("?range=", StringComparison.Ordinal);
                if (range >= 0) key = key[..range];
            }
            // The library's HTTP wrapper adds hl=en to CDN requests if absent.
            // For tail probes it is already stripped with the appended range.
            if (!remaining.Remove(key) &&
                !(method == HttpMethod.Head && (key.EndsWith("&hl=en", StringComparison.Ordinal) || key.EndsWith("?hl=en", StringComparison.Ordinal)) && remaining.Remove(key[..^6])))
            {
                remaining = null;
                PublishProgress(null);
                return;
            }
            PublishProgress(new(total - remaining.Count, total));
        }

        private void PublishProgress(StreamCheckProgress? progress)
        {
            timing?.RecordReachabilityProgress(progress);
            Meter?.CandidateProgress(progress);
        }

        internal void MarkReachability()
        {
            if (timing is not null && Interlocked.Exchange(ref probing, 1) == 0)
                timing.Mark(PlaybackTimingStage.StreamReachabilityStarted);
        }

        internal void Finish()
        {
            // A successful manifest should account for every advertised probe.
            // Keep the timing, but don't retain a misleading partial count.
            if (remaining is { Count: > 0 }) PublishProgress(null);
        }
    }

    // YoutubeExplode 6.6.3 consumes probes serially. Overlap a fixed window of
    // its exact GETs and transfer each response only when the library asks for it.
    // A mismatch disables speculation for this response, not library validation.
    private sealed class ProbeWindow(Uri[] probes, int limit, TimeSpan probeBudget, CancellationToken token)
    {
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly Dictionary<int, Task<HttpResponseMessage?>> pending = new();
        private int consumed;
        private int dispatched;

        internal bool Matches(HttpRequestMessage request) => consumed < probes.Length &&
            request.Method == HttpMethod.Get && request.Headers.Range is null &&
            request.RequestUri?.AbsoluteUri == probes[consumed].AbsoluteUri;

        internal async Task<HttpResponseMessage?> ReadAsync(PublicTransport transport, CancellationToken requestToken)
        {
            while (dispatched < probes.Length && dispatched - consumed < limit)
            {
                requestToken.ThrowIfCancellationRequested();
                lifetime.Token.ThrowIfCancellationRequested();
                var index = dispatched++;
                pending.Add(index, SendAsync(transport, probes[index], lifetime.Token));
            }

            var response = await pending[consumed].WaitAsync(requestToken);
            pending.Remove(consumed++);
            return response;
        }

        private async Task<HttpResponseMessage?> SendAsync(PublicTransport transport, Uri uri, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(probeBudget);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            try { return await transport.SendCoreAsync(request, timeout.Token, speculative: true); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            catch (SpeculativeBodyTooLargeException) { return null; }
        }

        internal async Task DisposeAsync()
        {
            try
            {
                await lifetime.CancelAsync();
            }
            finally
            {
                foreach (var task in pending.Values)
                {
                    try { (await task)?.Dispose(); }
                    catch (Exception) { } // Never replace the discovery result with abandoned probe errors.
                }
                lifetime.Dispose();
            }
        }
    }
    private sealed class SpeculativeBodyTooLargeException : Exception { }
    private readonly AsyncLocal<DiscoveryCapture?> discoveryCapture = new();

    internal async Task<(T Result, PlayerMetadata? Metadata, Storyboard? Storyboard)> DiscoverAsync<T>(string videoId, PlaybackTiming? timing,
        int limit, CancellationToken token, Func<ValueTask<T>> discover)
    {
        var previous = discoveryCapture.Value;
        var capture = new DiscoveryCapture(videoId, timing, limit, speculativeBudget, token);
        discoveryCapture.Value = capture;
        try
        {
            var result = await discover();
            await capture.DrainAsync();
            token.ThrowIfCancellationRequested();
            capture.Finish();
            return (result, capture.Latest, capture.Storyboard);
        }
        finally
        {
            try { await capture.DrainAsync(); }
            finally { discoveryCapture.Value = previous; }
        }
    }
    internal Task<byte[]?> ReadStoryboardSheetAsync(Storyboard storyboard, int sheet, CancellationToken token) =>
        StoryboardSheet.ReadAsync(inner, storyboard, sheet, token);

    internal static bool IsCdn(Uri u) => u.IsAbsoluteUri && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Fragment.Length == 0 && u.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsYouTube(Uri u) => u.IsAbsoluteUri && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Fragment.Length == 0 && u.Host is "www.youtube.com" or "youtube.com";
    internal static PlaybackException Unsafe() => new("unsafe_resource", "The playback resource is not permitted.");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var capture = discoveryCapture.Value;
        if (capture?.Window is { } window)
        {
            if (window.Matches(request))
            {
                // A speculative timeout or oversized response should not make a
                // stream fail before the library has made its normal request.
                if (await window.ReadAsync(this, cancellationToken) is { } response) return response;
                return await SendCoreAsync(request, cancellationToken, retry: true);
            }
            if (request.RequestUri is { } uri && IsCdn(uri)) capture.Fallback = ReachabilityFallback.RequestMismatch;
            await capture.DrainAsync();
        }
        return await SendCoreAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        bool speculative = false, bool retry = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var origin = request.RequestUri ?? throw Unsafe();
        var uri = origin; bool cdn = IsCdn(uri);
        Dictionary<string, string>? originalQuery = null;
        if (!cdn && !IsYouTube(uri) || cdn && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) throw Unsafe();
        var capture = discoveryCapture.Value;
        var meter = capture?.Meter;
        bool tail = meter is not null && cdn && request.Method == HttpMethod.Get && ReachabilityMeter.IsTail(origin);
        long? probeStarted = null;
        bool sample = false;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                using var outgoing = new HttpRequestMessage(request.Method, uri);
                if (cdn)
                {
                    outgoing.Headers.Range = request.Headers.Range;
                    outgoing.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                    outgoing.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                }
                else
                {
                    foreach (var header in request.Headers)
                        if (header.Key != "Authorization" && header.Key != "Proxy-Authorization") outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    outgoing.Content = request.Content; // library owns the request body
                }
                HttpResponseMessage response;
                // Only a CDN dispatch during library discovery is a reachability check.
                if (cdn && redirects == 0)
                {
                    capture?.MarkReachability();
                    if (capture is not null) probeStarted = meter?.Dispatch(request.Method, tail, speculative, retry, capture.Fallback);
                }
                else if (cdn) meter?.RedirectDispatched();
                else if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/youtubei/v1/player") meter?.PlayerDispatched();
                try { response = await inner.SendAsync(outgoing, cancellationToken); }
                finally { if (!cdn) outgoing.Content = null; }
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var target = response.Headers.Location;
                    response.Dispose();
                    if (!cdn || redirects >= 4 || target is null || !Uri.TryCreate(uri, target, out var next) || !IsCdn(next)) throw Unsafe();
                    // Bind every hop to the initiating path and query. The method and any
                    // HTTP Range stay on the original request; query range is protected below.
                    if (next.AbsolutePath != origin.AbsolutePath) throw Unsafe();
                    originalQuery ??= ReadBoundQuery(origin);
                    var nextQuery = ReadBoundQuery(next);
                    if (originalQuery.Count != nextQuery.Count || originalQuery.Any(p => !nextQuery.TryGetValue(p.Key, out var value) || value != p.Value)) throw Unsafe();
                    uri = next; continue;
                }
                if (request.Method == HttpMethod.Head)
                {
                    if (!cancellationToken.IsCancellationRequested) capture?.ObserveProbe(origin, request.Method, response);
                    sample = meter is not null && !cancellationToken.IsCancellationRequested && IsTerminalProbe(request.Method, response);
                    return response;
                }
                // Buffer only bounded responses, including library validity probes and JSON.
                try
                {
                    const int ordinaryLimit = 16 * 1024 * 1024;
                    const int speculativeLimit = 4096; // Two media bytes, or a small CDN error body.
                    int limit = speculative ? speculativeLimit : ordinaryLimit;
                    if (response.Content.Headers.ContentEncoding.Any() || response.Content.Headers.ContentLength > ordinaryLimit) throw Unsafe();
                    if (response.Content.Headers.ContentLength > limit) throw new SpeculativeBodyTooLargeException();
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var output = new MemoryStream(); byte[] buffer = new byte[speculative ? speculativeLimit + 1 : 65536]; int n;
                    while ((n = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        if (output.Length + n > limit)
                        {
                            if (speculative) throw new SpeculativeBodyTooLargeException();
                            throw Unsafe();
                        }
                        output.Write(buffer, 0, n);
                    }
                    var original = output.ToArray();
                    bool publicPlayer = !cdn && response.IsSuccessStatusCode && request.Method == HttpMethod.Post && uri.AbsolutePath == "/youtubei/v1/player";
                    // The pinned stream client uses the last successful transport response,
                    // including on a retry. Never carry details forward from an earlier response.
                    var bytes = original;
                    if (publicPlayer)
                    {
                        if (capture is not null)
                        {
                            capture.Latest = PlayerMetadata.Read(original, capture.Id, out var storyboard);
                            capture.Storyboard = storyboard;
                        }
                        bytes = PlayerStreamFilter.Filter(original, capture?.MeasuresProgress == true, capture?.CanPrefetch == true,
                            out var candidates, out var probes, out var fallback, out var repeatedFormatEntries);
                        capture?.BeginPlayerResponse(candidates, probes, fallback, repeatedFormatEntries);
                    }
                    if (cdn && !cancellationToken.IsCancellationRequested) capture?.ObserveProbe(origin, request.Method, response);
                    var content = new ByteArrayContent(bytes);
                    foreach (var h in response.Content.Headers)
                        if (ReferenceEquals(bytes, original) || !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    response.Content.Dispose(); response.Content = content;
                    sample = tail && !cancellationToken.IsCancellationRequested && IsTerminalProbe(request.Method, response);
                    return response;
                }
                catch { response.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) when (speculative || cancellationToken.IsCancellationRequested) { throw; }
        catch (SpeculativeBodyTooLargeException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("network_error", "The public playback request failed."); }
        finally
        {
            if (probeStarted is long started) meter!.Complete(started, sample);
        }
    }

    private static Dictionary<string, string> ReadBoundQuery(Uri uri)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        if (uri.Query.Length <= 1) return bound;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in uri.Query[1..].Split('&'))
        {
            int separator = field.IndexOf('=');
            if (separator <= 0 || field.Contains(';')) throw Unsafe();
            string name = Uri.UnescapeDataString(field[..separator]);
            if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || !names.Add(name)) throw Unsafe();
            bound.Add(name, field[(separator + 1)..]);
        }
        // Allow observed edge-routing changes, but not fields covered by the media
        // signature. The sparams list itself stays bound, as do unknown fields and the range.
        var signedNames = bound.TryGetValue("sparams", out var signed)
            ? Uri.UnescapeDataString(signed).Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in bound.Keys.Where(name => IsRoutingField(name) && !signedNames.Contains(name)).ToArray())
            bound.Remove(name);
        return bound;
    }

    private static bool IsRoutingField(string name) => name is
        "mh" or "mm" or "mn" or "ms" or "mt" or "mv" or "mvi" or "pl" or "initcwndbps" or
        "lsig" or "lsparams" or "redirect_counter" or "cms_redirect" or "req_id" or "rm" or "rms" or "rr" or
        "cm2rm" or "rrc" or "cmsv" or "fvip" or "met" or "fexp";

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
