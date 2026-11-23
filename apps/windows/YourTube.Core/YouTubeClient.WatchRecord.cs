using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourTube.Core;

public sealed record WatchLookupResult(string Status, bool TrackingUsable);
public sealed record WatchLookupComparison(WatchLookupResult Baseline, WatchLookupResult Candidate);

public sealed partial class YouTubeClient
{
    // Nonsecret TV_DOWNGRADED profile from the pinned MediaServiceCore reference.
    private const string TvFallbackVersion = "5.20260901";
    private const string TvFallbackAgent = "Mozilla/5.0 (DirectFB; Linux x86_64) Cobalt/4.13031-qa (unlike Gecko) Starboard/1";

    public async Task<WatchLookupComparison> CompareWatchLookupsAsync(YouTubeSession session, YouTubeAccount account, string videoId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        if (!IsWatchVideoId(videoId)) throw InvalidArgument();
        await PrepareWatchLookupAsync(cancellationToken);
        var nonce = WatchNonce();
        var baseline = await InspectAsync(false);
        var candidate = await InspectAsync(true);
        return new(baseline, candidate);

        async Task<WatchLookupResult> InspectAsync(bool tvFallback)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var player = await LookupWatchPlayerAsync(session, account, videoId, nonce, cancellationToken, tvFallback);
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ReadWatchStatusAsync(player, cancellationToken);
            if (status != "OK")
                return new(status is "LOGIN_REQUIRED" or "UNPLAYABLE" or "ERROR" or "AGE_CHECK_REQUIRED" or "CONTENT_CHECK_REQUIRED" or "LIVE_STREAM_OFFLINE" ? status : "OTHER", false);
            try
            {
                await WatchStageAsync("watch_tracking_unusable", () => Task.FromResult(WatchTrackingValues(player, videoId)), cancellationToken);
                return new("OK", true);
            }
            catch (YouTubeException error) when (error.Code == "watch_tracking_unusable") { return new("OK", false); }
        }
    }

    public async Task RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        if (!IsWatchVideoId(videoId) || duration <= TimeSpan.Zero || position < TimeSpan.Zero || position > duration)
            throw InvalidArgument();
        await PrepareWatchLookupAsync(cancellationToken);
        var nonce = WatchNonce();
        var player = await LookupWatchPlayerAsync(session, account, videoId, nonce, cancellationToken);
        var status = await ReadWatchStatusAsync(player, cancellationToken);
        if (status != "OK") throw new YouTubeException("watch_lookup_not_playable", "The YouTube player lookup did not return a playable video.");
        var target = await WatchStageAsync("watch_tracking_unusable", () => Task.FromResult(WatchTarget(player, videoId, duration, position, nonce)), cancellationToken);
        var accepted = await WatchStageAsync("watch_record_unconfirmed", async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri("https://www.youtube.com/tv");
            if (account.PageId is not null) request.Headers.Add("X-Goog-PageId", account.PageId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }, cancellationToken);
        if (!accepted) throw new YouTubeException("watch_record_rejected", "YouTube returned an unsuccessful status for the watch-history request.");
    }

    private static bool IsWatchVideoId(string videoId) => Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z");
    private static string WatchNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');
    private Task PrepareWatchLookupAsync(CancellationToken ct) =>
        WatchStageAsync("watch_discovery_failed", async () => { await BootstrapAsync(ct); return true; }, ct);

    private Task<JsonElement> LookupWatchPlayerAsync(YouTubeSession session, YouTubeAccount account, string videoId, string nonce, CancellationToken ct, bool tvFallback = false) =>
        WatchStageAsync("watch_lookup_failed", () => PostAsync("youtubei/v1/player", new
        {
            context = tvFallback ? TvFallbackContext(account.PageId) : Context(account.PageId), videoId, cpn = nonce
        }, ct, session: session, pageId: account.PageId, tvFallback: tvFallback), ct);

    private object TvFallbackContext(string? pageId) => new
    {
        client = new { clientName = "TVHTML5", clientVersion = TvFallbackVersion, clientScreen = "WATCH", userAgent = TvFallbackAgent, acceptLanguage = "en", acceptRegion = "US", utcOffsetMinutes = "0", visitorData },
        user = TvUser(pageId)
    };

    private static Task<string> ReadWatchStatusAsync(JsonElement player, CancellationToken ct) =>
        WatchStageAsync("watch_lookup_failed", () => Task.FromResult(Required(player.GetProperty("playabilityStatus"), "status")), ct);

    private static async Task<T> WatchStageAsync<T>(string code, Func<Task<T>> action, CancellationToken ct)
    {
        try { return await SafeAsync(action, ct); }
        catch (YouTubeException) { throw new YouTubeException(code, "The experimental watch-history request did not complete this step."); }
    }

    private static string WatchTarget(JsonElement player, string videoId, TimeSpan duration, TimeSpan position, string nonce)
    {
        var values = WatchTrackingValues(player, videoId);
        string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return $"https://www.youtube.com/api/stats/playback?ns=yt&ver=2&docid={videoId}&len={Seconds(duration)}&cmt={Seconds(position)}&cpn={nonce}" +
            $"&ei={Uri.EscapeDataString(values["ei"])}&vm={Uri.EscapeDataString(values["vm"])}&of={Uri.EscapeDataString(values["of"])}";
    }

    private static Dictionary<string, string> WatchTrackingValues(JsonElement player, string videoId)
    {
        if (Required(player.GetProperty("videoDetails"), "videoId") != videoId)
            throw Invalid();
        var url = Required(player.GetProperty("playbackTracking").GetProperty("videostatsWatchtimeUrl"), "baseUrl");
        if (url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host is not ("www.youtube.com" or "s.youtube.com" or "youtube.com") || uri.AbsolutePath != "/api/stats/watchtime" || uri.Fragment.Length != 0)
            throw Invalid();

        // Only copy the opaque values used by the pinned TrackingApi. Never request the returned URL.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var split = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(split[0]);
            if (name is not ("ei" or "vm" or "of")) continue;
            if (split.Length != 2 || !values.TryAdd(name, Uri.UnescapeDataString(split[1].Replace('+', ' ')))) throw Invalid();
        }
        if (values.Count != 3 || values.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))) throw Invalid();
        return values;
    }
}
