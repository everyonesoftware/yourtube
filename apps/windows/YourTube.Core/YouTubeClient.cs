using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourTube.Core;

public sealed partial class YouTubeClient
{
    private readonly HttpClient http;
    private const string UserAgent = "Mozilla/5.0 (Linux armeabi-v7a; Android 7.1.2; Fire OS 6.0) Cobalt/22.lts.3.306369-gold (unlike Gecko) v8/8.8.278.8-jit gles Starboard/13, Amazon_ATV_mediatek8695_2019/NS6294 (Amazon, AFTMM, Wireless) com.amazon.firetv.youtube/22.3.r2.v66.0";
    private string? clientId;
    private string? clientSecret;
    private string clientVersion = "7.20260901.15.00";
    private string? visitorData;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public YouTubeClient(HttpClient http) : this(http, () => DateTimeOffset.UtcNow, Task.Delay) { }
    internal YouTubeClient(HttpClient http, Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay)
    { this.http = http; this.now = now; this.delay = delay; }
    private static YouTubeException Invalid() => new("invalid_response", "YouTube returned an unsupported response.");
    private static YouTubeException BootstrapFailed() => new("bootstrap_failed", "YouTube TV client discovery failed. Try again later.");
    private static YouTubeException AuthRequired() => new("auth_required", "Sign in to YouTube again.");
    private static YouTubeException InvalidArgument() => new("invalid_argument", "The request is missing required information.");
    private void ValidateSession(YouTubeSession session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken) || session.ExpiresAt <= now()) throw AuthRequired();
    }
    private static async Task<T> SafeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { throw new YouTubeException("timeout", "YouTube did not respond in time."); }
        catch (HttpRequestException) { throw new YouTubeException("service_unavailable", "YouTube could not be reached. Try again later."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or RegexMatchTimeoutException or OverflowException) { throw Invalid(); }
    }
    private static string Required(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw Invalid();
        return value.GetString()!;
    }
    private static int Positive(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var value) || !value.TryGetInt32(out var number) || number <= 0) throw Invalid();
        return number;
    }
    private static bool AllowedActivation(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "youtube.com" or "www.youtube.com" or "google.com" or "www.google.com" or "accounts.google.com";
    private static string Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value;
    private async Task<string> GetPublicAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
    private async Task BootstrapAsync(CancellationToken ct)
    {
        if (clientId is not null) return;
        var html = await GetPublicAsync(new Uri("https://www.youtube.com/tv"), ct);
        var path = Match(html, "id=\"base-js\" src=\"([^\"]+)\"");
        if (path.Length == 0) path = Match(html, "\\.src = '(.*?m=base)'");
        if (path.Length == 0) path = Match(html, "\\.src = '(.*?)'; .\\.id = 'base-js'");
        if (string.IsNullOrWhiteSpace(path)) throw BootstrapFailed();
        var scriptUri = new Uri(new Uri("https://www.youtube.com"), path);
        if (scriptUri.Scheme != "https" || scriptUri.Host != "www.youtube.com" || !scriptUri.IsDefaultPort || scriptUri.UserInfo.Length > 0) throw BootstrapFailed();
        var script = await GetPublicAsync(scriptUri, ct);
        var credentials = Regex.Match(script, "clientId:\"([-\\w]+\\.apps\\.googleusercontent\\.com)\",\\s*[$\\w]+:\"(\\w+)\"", RegexOptions.None, TimeSpan.FromSeconds(2));
        if (!credentials.Success) throw BootstrapFailed();
        clientId = credentials.Groups[1].Value;
        clientSecret = credentials.Groups[2].Value;
        var version = Match(html, "\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"");
        if (version.Length > 0) clientVersion = version;
        visitorData = Match(html, "\"visitorData\":\"([^\"]+)\"");
    }
    private async Task<JsonElement> PostAsync(string path, object payload, CancellationToken ct, bool polling = false, YouTubeSession? session = null, string? pageId = null, bool tvPlayerHeaders = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/" + path) { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (path == "youtubei/v1/browse") request.Headers.Referrer = new Uri("https://www.youtube.com/tv");
        if (path == "youtubei/v1/player" && tvPlayerHeaders)
        {
            request.Headers.Add("X-Youtube-Client-Name", "7"); // TVHTML5 in the pinned MediaServiceCore client map.
            request.Headers.Add("X-Youtube-Client-Version", clientVersion);
            if (!string.IsNullOrWhiteSpace(visitorData)) request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }
        if (session is not null) request.Headers.Authorization = new("Bearer", session.AccessToken);
        if (pageId is not null) request.Headers.Add("X-Goog-PageId", pageId);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw AuthRequired();
        if ((int)response.StatusCode == 429) throw new YouTubeException("rate_limited", "YouTube is rate limiting requests. Try again later.");
        if ((int)response.StatusCode >= 500) throw new YouTubeException("service_unavailable", "YouTube is temporarily unavailable.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            if (polling && error.ValueKind == JsonValueKind.String && error.GetString() is "authorization_pending" or "slow_down") return document.RootElement.Clone();
            if (error.ValueKind == JsonValueKind.String)
            {
                if (error.GetString() is "invalid_grant" or "access_denied" or "expired_token" or "invalid_token" or "authorization_required") throw AuthRequired();
                if (error.GetString() == "invalid_client") throw BootstrapFailed();
            }
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is "UNAUTHENTICATED" or "PERMISSION_DENIED") throw AuthRequired();
            throw new YouTubeException("request_failed", "YouTube rejected the request.");
        }
        if (!response.IsSuccessStatusCode) throw new YouTubeException("request_failed", "YouTube rejected the request.");
        if (session is not null && (Find(document.RootElement, "loggedOut").Any(v => v.ValueKind == JsonValueKind.True) || Find(document.RootElement, "tvSignInRenderer").Any())) throw AuthRequired();
        return document.RootElement.Clone();
    }
    public Task<DeviceAuthorization> BeginSignInAsync(string deviceId, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw InvalidArgument();
        await BootstrapAsync(cancellationToken);
        var json = await PostAsync("o/oauth2/device/code", new { client_id = clientId, device_id = deviceId, model_name = "ytlr::", scope = "http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content" }, cancellationToken);
        var uri = new Uri(Required(json, json.TryGetProperty("verification_url", out _) ? "verification_url" : "verification_uri"));
        if (!AllowedActivation(uri)) throw Invalid();
        return new DeviceAuthorization(Required(json, "user_code"), uri, now().AddSeconds(Positive(json, "expires_in")), Positive(json, "interval"), Required(json, "device_code"));
    }, cancellationToken);
    public Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        if (authorization is null || authorization.IntervalSeconds <= 0 || string.IsNullOrWhiteSpace(authorization.DeviceCode)) throw InvalidArgument();
        if (authorization.ExpiresAt <= now()) throw AuthRequired();
        await BootstrapAsync(cancellationToken);
        var interval = authorization.IntervalSeconds;
        while (true)
        {
            var remaining = authorization.ExpiresAt - now();
            if (remaining <= TimeSpan.Zero) throw AuthRequired();
            if (remaining <= TimeSpan.FromSeconds(interval))
            {
                await delay(remaining, cancellationToken);
                throw AuthRequired();
            }
            await delay(TimeSpan.FromSeconds(interval), cancellationToken);
            if (authorization.ExpiresAt <= now()) throw AuthRequired();
            var json = await PostAsync("o/oauth2/token", new { code = authorization.DeviceCode, client_id = clientId, client_secret = clientSecret, grant_type = "http://oauth.net/grant_type/device/1.0" }, cancellationToken, polling: true);
            if (json.TryGetProperty("error", out var error))
            {
                if (error.GetString() == "slow_down") interval = checked(interval + 5);
                continue;
            }
            return Session(json);
        }
    }, cancellationToken);
    public Task<YouTubeSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw InvalidArgument();
        await BootstrapAsync(cancellationToken);
        var json = await PostAsync("o/oauth2/token", new { refresh_token = refreshToken, client_id = clientId, client_secret = clientSecret, grant_type = "refresh_token" }, cancellationToken);
        return Session(json, refreshToken);
    }, cancellationToken);
    private YouTubeSession Session(JsonElement json, string? previousRefresh = null)
    {
        if (json.TryGetProperty("token_type", out var type) && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))) throw Invalid();
        return new(Required(json, "access_token"), json.TryGetProperty("refresh_token", out _) || previousRefresh is null ? Required(json, "refresh_token") : previousRefresh, now().AddSeconds(Positive(json, "expires_in")));
    }
    private object Context(string? pageId = null) => new
    {
        client = new { clientName = "TVHTML5", clientVersion, clientScreen = "WATCH", userAgent = UserAgent, browserName = "Cobalt", browserVersion = "22.lts.3.306369-gold", hl = "en", gl = "US", acceptLanguage = "en", acceptRegion = "US", utcOffsetMinutes = "0", visitorData, tvAppInfo = new { appQuality = "TV_APP_QUALITY_FULL_ANIMATION", zylonLeftNav = true }, webpSupport = false, animatedWebpSupport = true },
        user = TvUser(pageId)
    };
    private static Dictionary<string, object> TvUser(string? pageId)
    {
        var user = new Dictionary<string, object> { ["enableSafetyMode"] = false, ["lockedSafetyMode"] = false };
        if (pageId is not null) user["onBehalfOfUser"] = pageId;
        return user;
    }
    private static IEnumerable<JsonElement> Find(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in root.EnumerateObject())
            {
                if (child.Name == "tabRenderer" && child.Value.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.False) continue;
                if (child.Name == property) yield return child.Value;
                foreach (var result in Find(child.Value, property)) yield return result;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var child in root.EnumerateArray())
                foreach (var result in Find(child, property)) yield return result;
    }
    private static string Text(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind != JsonValueKind.Object) return "";
        if (value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (value.TryGetProperty("simpleText", out var simple)) return simple.GetString() ?? "";
        if (value.TryGetProperty("runs", out var runs)) return string.Concat(runs.EnumerateArray().Select(r => r.TryGetProperty("text", out var text) ? text.GetString() : ""));
        return "";
    }
    private static string TextAt(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var text) ? Text(text) : "";
    public Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken cancellationToken = default) => SafeAsync<IReadOnlyList<YouTubeAccount>>(async () =>
    {
        ValidateSession(session);
        await BootstrapAsync(cancellationToken);
        var json = await PostAsync("youtubei/v1/account/accounts_list", new { context = Context(), accountReadMask = new { returnOwner = true, returnBrandAccounts = true, returnPersonaAccounts = false } }, cancellationToken, session: session);
        if (!json.TryGetProperty("contents", out _)) throw Invalid();
        var accounts = new List<YouTubeAccount>();
        foreach (var item in Find(json, "accountItem"))
        {
            if (item.TryGetProperty("isDisabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) continue;
            var name = TextAt(item, "accountName");
            if (name.Length == 0) continue;
            var handle = TextAt(item, "accountByline");
            // AccountInt.java selects the first pageIdToken from this identity's supportedTokens.
            // An accountStateToken's obfuscatedGaiaId is not an identity page ID.
            JsonElement pageId = default;
            if (item.TryGetProperty("serviceEndpoint", out var endpoint) &&
                endpoint.TryGetProperty("selectActiveIdentityEndpoint", out var identity) &&
                identity.TryGetProperty("supportedTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                pageId = tokens.EnumerateArray()
                    .Where(token => token.TryGetProperty("pageIdToken", out _))
                    .Select(token => token.GetProperty("pageIdToken"))
                    .Where(token => token.TryGetProperty("pageId", out _))
                    .Select(token => token.GetProperty("pageId"))
                    .FirstOrDefault();
            }
            accounts.Add(new(name, handle.Length == 0 ? null : handle, pageId.ValueKind == JsonValueKind.String ? pageId.GetString() : null));
        }
        return accounts;
    }, cancellationToken);
    private static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (var key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return default;
        }
        return value;
    }
    private static JsonElement HistoryContainer(JsonElement response)
    {
        if (response.TryGetProperty("contents", out var content)) return ActiveHistoryContent(content);
        if (response.TryGetProperty("continuationContents", out var continuation))
        {
            // WatchNextHelper.kt selects these continuation containers, never the response envelope.
            foreach (var key in new[] { "horizontalListContinuation", "gridContinuation", "playlistVideoListContinuation", "sectionListContinuation" })
                if (continuation.TryGetProperty(key, out var list)) return list;
            if (continuation.TryGetProperty("tvSurfaceContentContinuation", out var surface))
                return ActiveHistoryContent(At(surface, "content"));
            if (continuation.ValueKind == JsonValueKind.Object && !continuation.EnumerateObject().Any()) return continuation;
            throw Invalid();
        }
        foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints" })
        {
            var actions = At(response, key);
            if (actions.ValueKind != JsonValueKind.Array) continue;
            foreach (var action in actions.EnumerateArray())
            {
                var items = At(action, "appendContinuationItemsAction", "continuationItems");
                if (items.ValueKind == JsonValueKind.Array) return items;
            }
        }
        throw Invalid();
    }
    private static JsonElement ActiveHistoryContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object) throw Invalid();
        if (content.TryGetProperty("tvBrowseRenderer", out var browse))
            return ActiveHistoryContent(At(browse, "content"));
        if (content.TryGetProperty("tvSurfaceContentRenderer", out var surface))
            return ActiveHistoryContent(At(surface, "content"));
        if (content.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                var renderer = At(tab, "tabRenderer");
                if (At(renderer, "selected").ValueKind == JsonValueKind.True)
                    return ActiveHistoryContent(At(renderer, "content"));
            }
            throw Invalid();
        }
        foreach (var key in new[] { "gridRenderer", "sectionListRenderer", "playlistVideoListRenderer", "horizontalListRenderer" })
            if (content.TryGetProperty(key, out var list)) return list;
        if (content.TryGetProperty("twoColumnRenderer", out var columns))
            return ActiveHistoryContent(At(columns, "rightColumn"));
        // Retain the existing synthetic legacy roots, without accepting arbitrary renderer feeds.
        if (!content.EnumerateObject().Any() || content.TryGetProperty("items", out _) ||
            content.TryGetProperty("messageRenderer", out _) || content.TryGetProperty("videoRenderer", out _) ||
            content.TryGetProperty("gridVideoRenderer", out _)) return content;
        throw Invalid();
    }
    private static IEnumerable<(string Kind, JsonElement Value)> Cards(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in root.EnumerateObject())
            {
                if (child.Name == "tabRenderer" && child.Value.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.False) continue;
                if (child.Name is "gridVideoRenderer" or "videoRenderer" or "tileRenderer" or "lockupViewModel" or "shortsLockupViewModel") yield return (child.Name, child.Value);
                else foreach (var result in Cards(child.Value)) yield return result;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var child in root.EnumerateArray()) foreach (var result in Cards(child)) yield return result;
    }
    public Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        ValidateSession(session);
        await BootstrapAsync(cancellationToken);
        // AppClient.TV.browseTemplate carries these flags for history and continuation alike.
        var payload = new Dictionary<string, object?> { ["context"] = Context(account.PageId), ["racyCheckOk"] = true, ["contentCheckOk"] = true };
        payload[continuation is null ? "browseId" : "continuation"] = continuation ?? "FEhistory";
        var json = await PostAsync("youtubei/v1/browse", payload, cancellationToken, session: session, pageId: account.PageId);
        return ParseVideoPage(HistoryContainer(json));
    }, cancellationToken);

    private static HistoryPage ParseVideoPage(JsonElement container)
    {
        var items = At(container, "items");
        if (items.ValueKind == JsonValueKind.Undefined) items = At(container, "contents");
        if (items.ValueKind == JsonValueKind.Undefined) items = container; // append arrays and legacy direct-card fixtures
        var videos = new List<HistoryVideo>();
        foreach (var (kind, card) in Cards(items))
        {
            var tile = kind == "tileRenderer";
            var lockup = kind == "lockupViewModel";
            var shorts = kind == "shortsLockupViewModel";
            if (tile && TextAt(card, "contentType") != "TILE_CONTENT_TYPE_VIDEO") continue;
            if (lockup && TextAt(card, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO") continue;
            var id = tile ? card.TryGetProperty("onSelectCommand", out var select) ? Find(select, "videoId").FirstOrDefault() : default
                : shorts ? Find(card, "videoId").FirstOrDefault()
                : card.TryGetProperty(lockup ? "contentId" : "videoId", out var directId) ? directId : default;
            if (id.ValueKind != JsonValueKind.String || !Regex.IsMatch(id.GetString()!, "^[A-Za-z0-9_-]{11}$", RegexOptions.None, TimeSpan.FromSeconds(2))) continue;
            var title = tile ? Find(card, "tileMetadataRenderer").Select(n => TextAt(n, "title")).FirstOrDefault() ?? "" : TextAt(card, "title");
            if (lockup) title = Find(card, "lockupMetadataViewModel").Select(n => TextAt(n, "title")).FirstOrDefault() ?? "";
            if (shorts) title = Find(card, "primaryText").Select(Text).FirstOrDefault() ?? "";
            var channel = TextAt(card, "shortBylineText");
            if (channel.Length == 0) channel = TextAt(card, "longBylineText");
            if (tile) channel = Find(card, "lineItemRenderer").Select(n => TextAt(n, "text")).FirstOrDefault() ?? "";
            if (lockup) channel = Find(card, "metadataParts").SelectMany(n => n.EnumerateArray()).Select(n => TextAt(n, "text")).FirstOrDefault() ?? "";
            var percent = Find(card, "percentDurationWatched").FirstOrDefault();
            if (lockup) percent = Find(card, "startPercent").FirstOrDefault();
            var duration = tile ? Find(card, "thumbnailOverlayTimeStatusRenderer").Select(n => TextAt(n, "text")).FirstOrDefault() ?? "" : TextAt(card, "lengthText");
            if (lockup) duration = Find(card, "thumbnailBadgeViewModel").Select(n => TextAt(n, "text")).FirstOrDefault() ?? "";
            videos.Add(new(id.GetString()!, title, channel, duration, percent.ValueKind == JsonValueKind.Number && percent.TryGetInt32(out var watched) ? Math.Clamp(watched, 0, 100) : null));
        }
        var next = Find(At(container, "continuations"), "nextContinuationData").Select(n => Required(n, "continuation")).FirstOrDefault();
        next ??= Find(items, "nextContinuationData").Select(n => Required(n, "continuation")).FirstOrDefault();
        next ??= Find(items, "continuationItemRenderer").SelectMany(n => Find(n, "continuationCommand")).Select(n => Required(n, "token")).FirstOrDefault();
        return new HistoryPage(videos, next, videos.Count == 0 ? "No history videos were returned. History may be empty or paused." : null);
    }
}
