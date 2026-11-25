using System.Text.Json;


namespace YourTube.Core;

public sealed record SubscribedChannel(string Name, bool HasNewContent)
{
    public Uri? AvatarUri { get; init; }
    internal SubscriptionChannelRoute? Route { get; init; }
    public bool CanLoadVideos => Route is not null;

    public override string ToString() => $"SubscribedChannel {{ Name = {Name}, HasNewContent = {HasNewContent}, CanLoadVideos = {CanLoadVideos} }}";
}
public sealed record SubscriptionsPage(IReadOnlyList<SubscribedChannel> Channels, IReadOnlyList<HistoryVideo> Videos, string? Continuation, string? Message);

internal sealed class SubscriptionChannelRoute(YouTubeClient client, YouTubeAccount account, string reloadToken)
{
    internal const string CursorPrefix = "yourtube-channel:";
    private readonly object gate = new();
    private readonly Dictionary<string, string> providerToHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> handleToProvider = new(StringComparer.Ordinal);

    internal string ReloadToken { get; } = reloadToken;
    internal bool BelongsTo(YouTubeClient requester, YouTubeAccount identity) =>
        ReferenceEquals(client, requester) && ReferenceEquals(account, identity);

    internal string? ResolveCursor(string handle)
    {
        lock (gate) return handleToProvider.GetValueOrDefault(handle);
    }

    internal string? IssueCursor(string? providerToken)
    {
        if (providerToken is null) return null;
        lock (gate)
        {
            // Stable within this route so the UI can detect paging cycles. No provider token leaves the route.
            if (providerToHandle.TryGetValue(providerToken, out var handle)) return handle;
            handle = CursorPrefix + Guid.NewGuid().ToString("N");
            providerToHandle.Add(providerToken, handle);
            handleToProvider.Add(handle, providerToken);
            return handle;
        }
    }
}

public sealed partial class YouTubeClient
{
    public Task<SubscriptionsPage> GetSubscriptionChannelAsync(YouTubeSession session, YouTubeAccount account,
        SubscribedChannel channel, string? continuation = null, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        ValidateSession(session);
        if (channel?.Route is not { } route || !route.BelongsTo(this, account)) throw InvalidArgument();
        var token = continuation is null ? route.ReloadToken : route.ResolveCursor(continuation) ?? throw InvalidArgument();
        cancellationToken.ThrowIfCancellationRequested();
        await BootstrapAsync(cancellationToken);
        var json = await BrowseSubscriptionsAsync(session, account, "continuation", token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var page = ParseVideoPage(FilterSubscriptionRows(SubscriptionChannelContainer(json)), allowTileStatsFirst: true);
        return new SubscriptionsPage([], page.Videos, route.IssueCursor(page.Continuation),
            page.Videos.Count == 0 ? "No videos were returned for this subscribed channel." : null);
    }, cancellationToken);

    public Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        ValidateSession(session);
        if (account is null || (continuation is not null && (string.IsNullOrWhiteSpace(continuation) ||
            continuation.StartsWith(SubscriptionChannelRoute.CursorPrefix, StringComparison.Ordinal)))) throw InvalidArgument();
        cancellationToken.ThrowIfCancellationRequested();
        await BootstrapAsync(cancellationToken);
        var json = await BrowseSubscriptionsAsync(session, account, continuation is null ? "browseId" : "continuation",
            continuation ?? "FEsubscriptions", cancellationToken);
        if (continuation is not null)
        {
            var continued = ParseVideoPage(FilterSubscriptionRows(HistoryContainer(json)));
            return new SubscriptionsPage([], continued.Videos, continued.Continuation, continued.Videos.Count == 0 ? "No more subscription videos were returned." : null);
        }
        var tabs = SubscriptionTabs(json);
        if (tabs.Count == 0) return new SubscriptionsPage([], [], null, "No subscriptions were returned for this account.");
        // Every TV tab may use FEsubscriptions with different opaque params. The
        // first matching tab is the aggregate feed; channel membership comes from
        // the tab list/thumbnail distinction, NOT from UC IDs or video bylines.
        var aggregate = tabs.FirstOrDefault(tab => TextAt(At(tab, "endpoint", "browseEndpoint"), "browseId") == "FEsubscriptions");
        if (aggregate.ValueKind == JsonValueKind.Undefined) aggregate = tabs[0];
        var channels = new List<SubscribedChannel>();
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            if (index == 0 && At(tab, "thumbnail").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            var name = TextAt(tab, "title");
            if (name.Length == 0) throw Invalid();
            channels.Add(new(name, TextAt(At(tab, "presentationStyle"), "style") == "NEW_CONTENT")
            {
                AvatarUri = ReadSubscriptionAvatar(tab),
                Route = tab.Equals(aggregate) || ChannelReloadToken(tab) is not { } reload ? null : new(this, account, reload)
            });
        }
        var page = ParseVideoPage(FilterSubscriptionRows(ActiveHistoryContent(At(aggregate, "content"))));
        return new SubscriptionsPage(channels, page.Videos, page.Continuation, page.Videos.Count == 0 ? "No subscription videos were returned." : null);
    }, cancellationToken);

    private Task<JsonElement> BrowseSubscriptionsAsync(YouTubeSession session, YouTubeAccount account,
        string selector, string value, CancellationToken cancellationToken) => PostAsync("youtubei/v1/browse", new Dictionary<string, object?>
    {
        ["context"] = Context(account.PageId), ["racyCheckOk"] = true, ["contentCheckOk"] = true, [selector] = value
    }, cancellationToken, session: session, pageId: account.PageId);

    private static JsonElement SubscriptionChannelContainer(JsonElement response)
    {
        // This request is always a continuation. Do not let a co-present browse snapshot
        // supply aggregate videos or a cursor, even when that snapshot is parseable.
        if (!response.TryGetProperty("contents", out _)) return HistoryContainer(response);
        var continuationEnvelope = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "continuationContents", "onResponseReceivedActions", "onResponseReceivedEndpoints" })
            if (response.TryGetProperty(key, out var value)) continuationEnvelope.Add(key, value);
        return HistoryContainer(JsonSerializer.SerializeToElement(continuationEnvelope));
    }

    private static Uri? ReadSubscriptionAvatar(JsonElement tab)
    {
        var thumbnails = At(tab, "thumbnail", "thumbnails");
        if (thumbnails.ValueKind != JsonValueKind.Array) return null;
        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            if (At(thumbnail, "url") is not { ValueKind: JsonValueKind.String } url) continue;
            var candidate = url.GetString()!;
            if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
            // Match the avatar transport allowlist without broadening video thumbnail hosts.
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                uri.IdnHost is "lh3.googleusercontent.com" or "yt3.ggpht.com" or "yt3.googleusercontent.com")
                return uri;
        }
        return null;
    }

    private static string? ChannelReloadToken(JsonElement tab)
    {
        // BrowseHelper.TabRenderer.getReloadToken -> WatchNextHelper.ContinuationItem.getContinuationToken.
        var continuation = At(tab, "content", "tvSurfaceContentRenderer", "continuation");
        foreach (var key in new[] { "nextContinuationData", "nextRadioContinuationData", "reloadContinuationData" })
        {
            var token = At(continuation, key, "continuation");
            if (token.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(token.GetString())) return token.GetString();
        }
        return null;
    }

    private static JsonElement FilterSubscriptionRows(JsonElement container)
    {
        // BrowseHelper.List<Shelf>.getItems: three-item chronological rows win over
        // the long Most relevant/Shorts shelf. Do not sort or invent upload dates.
        var rows = container.ValueKind == JsonValueKind.Array ? container : At(container, "contents");
        if (rows.ValueKind != JsonValueKind.Array) return container;
        static int RowSize(JsonElement row)
        {
            foreach (var path in new[] {
                new[] { "gridRenderer", "items" },
                new[] { "shelfRenderer", "content", "horizontalListRenderer", "items" },
                new[] { "shelfRenderer", "content", "gridRenderer", "items" } })
            {
                var items = At(row, path);
                if (items.ValueKind == JsonValueKind.Array) return items.GetArrayLength();
            }
            return 0;
        }
        if (!rows.EnumerateArray().Any(row => RowSize(row) == 3)) return container;
        // Pagination belongs to the chosen container, independently of which video rows win.
        var filtered = new Dictionary<string, object> { ["contents"] = rows.EnumerateArray()
            .Where(row => RowSize(row) == 3 || At(row, "continuationItemRenderer").ValueKind == JsonValueKind.Object).ToArray() };
        var continuations = At(container, "continuations");
        if (continuations.ValueKind != JsonValueKind.Undefined) filtered["continuations"] = continuations;
        return JsonSerializer.SerializeToElement(filtered);
    }

    private static IReadOnlyList<JsonElement> SubscriptionTabs(JsonElement response)
    {
        // BrowseHelper.getTabs: only the first subscriptions secondary-nav section.
        var sections = At(response, "contents", "tvBrowseRenderer", "content", "tvSecondaryNavRenderer", "sections");
        if (sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() == 0) throw Invalid();
        var tabs = At(sections[0], "tvSecondaryNavSectionRenderer", "tabs");
        if (tabs.ValueKind != JsonValueKind.Array) throw Invalid();
        var result = tabs.EnumerateArray().Select(t => t.TryGetProperty("tabRenderer", out var tab) ? tab : At(t, "expandableTabRenderer"))
            .Where(t => t.ValueKind == JsonValueKind.Object).ToArray();
        if (result.Length != tabs.GetArrayLength()) throw Invalid();
        return result;
    }
}
