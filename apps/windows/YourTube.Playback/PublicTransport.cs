namespace YourTube.Playback;

// Dedicated public-only transport: never shares the account client's handler or defaults.
internal sealed class PublicTransport(HttpMessageHandler handler) : HttpMessageHandler
{
    private readonly HttpMessageInvoker inner = new(handler);
    private sealed class MetadataCapture(string id)
    {
        internal string Id { get; } = id;
        internal PlayerMetadata? Latest { get; set; }
    }
    private readonly AsyncLocal<MetadataCapture?> metadataCapture = new();

    internal async Task<(T Result, PlayerMetadata? Metadata)> DiscoverAsync<T>(string videoId, Func<ValueTask<T>> discover)
    {
        var previous = metadataCapture.Value;
        var capture = new MetadataCapture(videoId);
        metadataCapture.Value = capture;
        try { return (await discover(), capture.Latest); }
        finally { metadataCapture.Value = previous; }
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
                if (request.Method == HttpMethod.Head) return response;
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
                    if (publicPlayer && metadataCapture.Value is { } capture)
                        capture.Latest = PlayerMetadata.Read(original, capture.Id);
                    var bytes = publicPlayer ? PlayerStreamFilter.Filter(original) : original;
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
