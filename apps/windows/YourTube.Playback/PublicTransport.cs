namespace YourTube.Playback;

// Dedicated public-only transport: never shares the account client's handler or defaults.
internal sealed class PublicTransport(HttpMessageHandler handler) : HttpMessageHandler
{
    private readonly HttpMessageInvoker inner = new(handler);
    private sealed class DiscoveryCapture(string id, PlaybackTiming? timing)
    {
        internal string Id { get; } = id;
        internal PlayerMetadata? Latest { get; set; }
        internal bool MeasuresProgress => timing?.TracksStartup == true;
        private HashSet<string>? remaining;
        private int total;
        private int probing;
        internal void BeginPlayerResponse(HashSet<string>? candidates)
        {
            remaining = candidates;
            total = candidates?.Count ?? 0;
            timing?.RecordReachabilityProgress(total > 0 ? new(0, total) : null);
        }

        // The pinned library probes serially. Use the initiating URL (not the
        // redirect target) and count the terminal result, not each HTTP request.
        internal void ObserveProbe(Uri origin, HttpMethod method, HttpResponseMessage response)
        {
            if (remaining is null) return;
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) return;
            if (method == HttpMethod.Head && response.IsSuccessStatusCode && response.Content.Headers.ContentLength is not null) return;
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
                BeginPlayerResponse(null);
                return;
            }
            timing?.RecordReachabilityProgress(new(total - remaining.Count, total));
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
            if (remaining is { Count: > 0 }) timing?.RecordReachabilityProgress(null);
        }
    }
    private readonly AsyncLocal<DiscoveryCapture?> discoveryCapture = new();

    internal async Task<(T Result, PlayerMetadata? Metadata)> DiscoverAsync<T>(string videoId, PlaybackTiming? timing, Func<ValueTask<T>> discover)
    {
        var previous = discoveryCapture.Value;
        var capture = new DiscoveryCapture(videoId, timing);
        discoveryCapture.Value = capture;
        try
        {
            var result = await discover();
            capture.Finish();
            return (result, capture.Latest);
        }
        finally { discoveryCapture.Value = previous; }
    }
    internal static bool IsCdn(Uri u) => u.IsAbsoluteUri && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Fragment.Length == 0 && u.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsYouTube(Uri u) => u.IsAbsoluteUri && u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Fragment.Length == 0 && u.Host is "www.youtube.com" or "youtube.com";
    internal static PlaybackException Unsafe() => new("unsafe_resource", "The playback resource is not permitted.");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var origin = request.RequestUri ?? throw Unsafe();
        var uri = origin; bool cdn = IsCdn(uri);
        Dictionary<string, string>? originalQuery = null;
        if (!cdn && !IsYouTube(uri) || cdn && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) throw Unsafe();
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
                if (cdn && redirects == 0) discoveryCapture.Value?.MarkReachability();
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
                    if (!cancellationToken.IsCancellationRequested) discoveryCapture.Value?.ObserveProbe(origin, request.Method, response);
                    return response;
                }
                // Buffer only bounded responses, including library validity probes and JSON.
                try
                {
                    const int limit = 16 * 1024 * 1024;
                    if (response.Content.Headers.ContentLength > limit || response.Content.Headers.ContentEncoding.Any()) throw Unsafe();
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var output = new MemoryStream(); byte[] buffer = new byte[65536]; int n;
                    while ((n = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    { if (output.Length + n > limit) throw Unsafe(); output.Write(buffer, 0, n); }
                    var original = output.ToArray();
                    bool publicPlayer = !cdn && response.IsSuccessStatusCode && request.Method == HttpMethod.Post && uri.AbsolutePath == "/youtubei/v1/player";
                    // The pinned stream client uses the last successful transport response,
                    // including on a retry. Never carry details forward from an earlier response.
                    var capture = discoveryCapture.Value;
                    var bytes = original;
                    if (publicPlayer)
                    {
                        if (capture is not null) capture.Latest = PlayerMetadata.Read(original, capture.Id);
                        bytes = PlayerStreamFilter.Filter(original, capture?.MeasuresProgress == true, out var candidates);
                        capture?.BeginPlayerResponse(candidates);
                    }
                    if (cdn && !cancellationToken.IsCancellationRequested) capture?.ObserveProbe(origin, request.Method, response);
                    var content = new ByteArrayContent(bytes);
                    foreach (var h in response.Content.Headers)
                        if (ReferenceEquals(bytes, original) || !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    response.Content.Dispose(); response.Content = content; return response;
                }
                catch { response.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("network_error", "The public playback request failed."); }
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
