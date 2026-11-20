using System.Text.Json;


namespace YourTube.Core;

public sealed record SubscribedChannel(string Name, bool HasNewContent);
public sealed record SubscriptionsPage(IReadOnlyList<SubscribedChannel> Channels, IReadOnlyList<HistoryVideo> Videos, string? Continuation, string? Message);

public sealed partial class YouTubeClient
{
    public Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        ValidateSession(session);
        if (account is null || (continuation is not null && string.IsNullOrWhiteSpace(continuation))) throw InvalidArgument();
        cancellationToken.ThrowIfCancellationRequested();
        await BootstrapAsync(cancellationToken);
        var payload = new Dictionary<string, object?> { ["context"] = Context(account.PageId), ["racyCheckOk"] = true, ["contentCheckOk"] = true };
        payload[continuation is null ? "browseId" : "continuation"] = continuation ?? "FEsubscriptions";
        var json = await PostAsync("youtubei/v1/browse", payload, cancellationToken, session: session, pageId: account.PageId);
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
            channels.Add(new(name, TextAt(At(tab, "presentationStyle"), "style") == "NEW_CONTENT"));
        }
        var page = ParseVideoPage(FilterSubscriptionRows(ActiveHistoryContent(At(aggregate, "content"))));
        return new SubscriptionsPage(channels, page.Videos, page.Continuation, page.Videos.Count == 0 ? "No subscription videos were returned." : null);
    }, cancellationToken);

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
