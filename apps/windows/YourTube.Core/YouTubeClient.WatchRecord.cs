using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourTube.Core;

public sealed partial class YouTubeClient
{
    public async Task RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        if (!Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z") || duration <= TimeSpan.Zero || position < TimeSpan.Zero || position > duration)
            throw InvalidArgument();
        await WatchStageAsync("watch_discovery_failed", async () => { await BootstrapAsync(cancellationToken); return true; }, cancellationToken);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');
        var player = await WatchStageAsync("watch_lookup_failed", () => PostAsync("youtubei/v1/player", new
        {
            context = Context(account.PageId), videoId, cpn = nonce
        }, cancellationToken, session: session, pageId: account.PageId), cancellationToken);
        var status = await WatchStageAsync("watch_lookup_failed", () => Task.FromResult(Required(player.GetProperty("playabilityStatus"), "status")), cancellationToken);
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

    private static async Task<T> WatchStageAsync<T>(string code, Func<Task<T>> action, CancellationToken ct)
    {
        try { return await SafeAsync(action, ct); }
        catch (YouTubeException) { throw new YouTubeException(code, "The experimental watch-history request did not complete this step."); }
    }

    private static string WatchTarget(JsonElement player, string videoId, TimeSpan duration, TimeSpan position, string nonce)
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

        string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return $"https://www.youtube.com/api/stats/playback?ns=yt&ver=2&docid={videoId}&len={Seconds(duration)}&cmt={Seconds(position)}&cpn={nonce}" +
            $"&ei={Uri.EscapeDataString(values["ei"])}&vm={Uri.EscapeDataString(values["vm"])}&of={Uri.EscapeDataString(values["of"])}";
    }
}
