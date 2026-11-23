using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourTube.Core;

public sealed record WatchLookupResult(string Status, bool TrackingUsable, bool PlaybackStartField = false, bool WatchTimeField = false);
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
        var baseline = await InspectAsync(false);
        var candidate = await InspectAsync(true);
        return new(baseline, candidate);

        async Task<WatchLookupResult> InspectAsync(bool tvFallback)
        {
            return await InspectWatchLookupAsync(session, account, videoId, tvFallback, null, cancellationToken);
        }
    }

    internal async Task<WatchLookupResult> InspectTvWatchContextAsync(YouTubeSession session, YouTubeAccount account, string videoId, int signatureTimestamp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateSession(session);
        if (!IsWatchVideoId(videoId) || signatureTimestamp <= 0) throw InvalidArgument();
        await PrepareWatchLookupAsync(ct);
        return await InspectWatchLookupAsync(session, account, videoId, true, signatureTimestamp, ct);
    }

    private async Task<WatchLookupResult> InspectWatchLookupAsync(YouTubeSession session, YouTubeAccount account, string videoId, bool tvFallback, int? signatureTimestamp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var player = await LookupWatchPlayerAsync(session, account, videoId, WatchNonce(), ct, tvFallback, signatureTimestamp);
        ct.ThrowIfCancellationRequested();
        var status = await ReadWatchStatusAsync(player, ct);
        var result = new WatchLookupResult(
            status is "OK" or "LOGIN_REQUIRED" or "UNPLAYABLE" or "ERROR" or "AGE_CHECK_REQUIRED" or "CONTENT_CHECK_REQUIRED" or "LIVE_STREAM_OFFLINE" ? status : "OTHER",
            false, HasTrackingField(player, "videostatsPlaybackUrl"), HasTrackingField(player, "videostatsWatchtimeUrl"));
        if (status != "OK") return result;
        try
        {
            await WatchStageAsync("watch_tracking_unusable", () => Task.FromResult(WatchTrackingValues(player, videoId)), ct);
            return result with { TrackingUsable = true };
        }
        catch (YouTubeException error) when (error.Code == "watch_tracking_unusable") { return result; }
    }

    public sealed class WatchRecord
    {
        internal WatchRecord(YouTubeClient owner, YouTubeAccount account, TimeSpan duration, string videoId, string nonce, (string Ei, string Vm, string Of) tracking)
        {
            Owner = owner;
            Account = account;
            Duration = duration;
            VideoId = videoId;
            Nonce = nonce;
            Tracking = tracking;
        }

        internal YouTubeClient Owner { get; }
        internal YouTubeAccount Account { get; }
        internal TimeSpan Duration { get; }
        internal string VideoId { get; }
        internal string Nonce { get; }
        internal (string Ei, string Vm, string Of) Tracking { get; }
        public override string ToString() => "YourTubeClient.WatchRecord [redacted]";
    }

    public async Task RecordWatchProgressAsync(YouTubeSession session, YouTubeAccount account, WatchRecord record, TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        // The caller owns the sign-in lifetime. A refreshed (even rotated) session must use the
        // same selected account instance; page IDs alone do not identify the owner across logins.
        if (record is null || !ReferenceEquals(this, record.Owner) || !ReferenceEquals(account, record.Account) ||
            start < TimeSpan.Zero || end <= start || end > record.Duration)
            throw InvalidArgument();
        var startSeconds = WatchSeconds(start);
        var endSeconds = WatchSeconds(end);
        if (startSeconds == endSeconds) throw InvalidArgument();

        var target = $"https://www.youtube.com/api/stats/watchtime?ns=yt&ver=2&docid={record.VideoId}&len={WatchSeconds(record.Duration)}" +
            $"&st={startSeconds}&et={endSeconds}&cmt={endSeconds}" + WatchTrackingQuery(record);
        var accepted = await SendWatchTrackingAsync(session, account, target, "watch_progress_unconfirmed", cancellationToken);
        if (!accepted) throw new YouTubeException("watch_progress_rejected", "YouTube returned an unsuccessful status for the watch-progress request.");
    }

    public async Task<WatchRecord> RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        if (account is null || !IsWatchVideoId(videoId) || duration <= TimeSpan.Zero || position < TimeSpan.Zero || position > duration)
            throw InvalidArgument();
        await PrepareWatchLookupAsync(cancellationToken);
        var timestamp = await PlayerContextDiscovery.DiscoverAsync(http, videoId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (timestamp is null)
            throw new YouTubeException("watch_context_unavailable", "The public YouTube player context could not be found.");
        var nonce = WatchNonce();
        var player = await LookupWatchPlayerAsync(session, account, videoId, nonce, cancellationToken, tvFallback: true, signatureTimestamp: timestamp);
        var status = await ReadWatchStatusAsync(player, cancellationToken);
        if (status != "OK") throw new YouTubeException("watch_lookup_not_playable", "The YouTube player lookup did not return a playable video.");
        var tracking = await WatchStageAsync("watch_tracking_unusable", () => Task.FromResult(WatchTrackingValues(player, videoId)), cancellationToken);
        var record = new WatchRecord(this, account, duration, videoId, nonce, tracking);
        var target = $"https://www.youtube.com/api/stats/playback?ns=yt&ver=2&docid={videoId}&len={WatchSeconds(duration)}&cmt={WatchSeconds(position)}" + WatchTrackingQuery(record);
        var accepted = await SendWatchTrackingAsync(session, account, target, "watch_record_unconfirmed", cancellationToken);
        if (!accepted) throw new YouTubeException("watch_record_rejected", "YouTube returned an unsuccessful status for the watch-history request.");
        return record;
    }

    private Task<bool> SendWatchTrackingAsync(YouTubeSession session, YouTubeAccount account, string target, string unconfirmedCode, CancellationToken cancellationToken) =>
        WatchStageAsync(unconfirmedCode, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri("https://www.youtube.com/tv");
            if (account.PageId is not null) request.Headers.Add("X-Goog-PageId", account.PageId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return response.IsSuccessStatusCode;
        }, cancellationToken);

    private static bool HasTrackingField(JsonElement player, string name) =>
        player.TryGetProperty("playbackTracking", out var tracking) && tracking.ValueKind == JsonValueKind.Object &&
        tracking.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object &&
        field.TryGetProperty("baseUrl", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    internal static bool IsWatchVideoId(string videoId) => Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z");
    private static string WatchNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');
    private Task PrepareWatchLookupAsync(CancellationToken ct) =>
        WatchStageAsync("watch_discovery_failed", async () => { await BootstrapAsync(ct); return true; }, ct);

    private Task<JsonElement> LookupWatchPlayerAsync(YouTubeSession session, YouTubeAccount account, string videoId, string nonce, CancellationToken ct, bool tvFallback = false, int? signatureTimestamp = null)
    {
        var context = tvFallback ? TvFallbackContext(account.PageId) : Context(account.PageId);
        object payload = signatureTimestamp is int timestamp
            ? new
            {
                context, videoId, cpn = nonce,
                playbackContext = new
                {
                    contentPlaybackContext = new { html5Preference = "HTML5_PREF_WANTS", lactMilliseconds = 60000, signatureTimestamp = timestamp },
                    devicePlaybackCapabilities = new { supportsVp9Encoding = true, supportXhr = false }
                }
            }
            : new { context, videoId, cpn = nonce };
        return WatchStageAsync("watch_lookup_failed", () => PostAsync("youtubei/v1/player", payload,
            ct, session: session, pageId: account.PageId, tvFallback: tvFallback), ct);
    }

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

    private static string WatchSeconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string WatchTrackingQuery(WatchRecord record) => $"&cpn={record.Nonce}" +
        $"&ei={Uri.EscapeDataString(record.Tracking.Ei)}&vm={Uri.EscapeDataString(record.Tracking.Vm)}&of={Uri.EscapeDataString(record.Tracking.Of)}";

    private static (string Ei, string Vm, string Of) WatchTrackingValues(JsonElement player, string videoId)
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
        return (values["ei"], values["vm"], values["of"]);
    }
}
