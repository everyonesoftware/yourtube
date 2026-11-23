using System.Text.Json;

namespace YourTube.Core;

public sealed record HomeShelf(string Title, IReadOnlyList<HistoryVideo> Videos);
public sealed record HomePage(IReadOnlyList<HomeShelf> Shelves, string? Continuation, string? Message);

public sealed partial class YouTubeClient
{
    public Task<HomePage> GetHomeAsync(YouTubeSession session, YouTubeAccount account, string? continuation = null, CancellationToken cancellationToken = default) => SafeAsync(async () =>
    {
        ValidateSession(session);
        if (account is null || (continuation is not null && string.IsNullOrWhiteSpace(continuation))) throw InvalidArgument();
        cancellationToken.ThrowIfCancellationRequested();
        await BootstrapAsync(cancellationToken);
        var payload = new Dictionary<string, object?> { ["context"] = Context(account.PageId), ["racyCheckOk"] = true, ["contentCheckOk"] = true };
        payload[continuation is null ? "browseId" : "continuation"] = continuation ?? "default";
        var json = await PostAsync("youtubei/v1/browse", payload, cancellationToken, session: session, pageId: account.PageId);
        return ParseHomePage(HistoryContainer(json), continuation is null);
    }, cancellationToken);

    private static HomePage ParseHomePage(JsonElement container, bool initial)
    {
        var rows = container.ValueKind == JsonValueKind.Array ? container : At(container, "contents");
        var shelves = new List<(HomeShelf Shelf, bool Shorts)>();
        var cursor = Find(At(container, "continuations"), "nextContinuationData").Select(n => Required(n, "continuation")).FirstOrDefault();
        foreach (var row in ArrayItems(rows))
        {
            var shelf = At(row, "shelfRenderer");
            var header = At(shelf, "headerRenderer", "shelfHeaderRenderer");
            var avatar = At(header, "avatarLockup", "avatarLockupRenderer");
            var title = TextAt(shelf, "title");
            if (string.IsNullOrWhiteSpace(title)) title = TextAt(avatar, "title");
            var items = At(shelf, "content", "horizontalListRenderer", "items");
            if (items.ValueKind != JsonValueKind.Array) items = At(shelf, "content", "gridRenderer", "items");
            var shortRow = TextAt(shelf, "tvhtml5ShelfRendererType") == "TVHTML5_SHELF_RENDERER_TYPE_SHORTS" ||
                TextAt(At(header, "icon"), "iconType") == "YOUTUBE_SHORTS_FILL_24" || string.Equals(title, "Shorts", StringComparison.OrdinalIgnoreCase);
            if (shortRow) title = "Shorts";
            var videos = ParseVideoCards(HomeCards(items), includeShortTiles: true);
            if (videos.Count > 0) shelves.Add((new(title.Trim(), videos.DistinctBy(video => video.VideoId).ToArray()), shortRow));
            // A horizontal row's continuation is not the vertical Home pager.
            var command = TextAt(At(row, "continuationItemRenderer", "continuationEndpoint", "continuationCommand"), "token");
            if (cursor is null && !string.IsNullOrWhiteSpace(command)) cursor = command;
        }
        if (shelves.Count == 0)
        {
            var grid = ParseVideoCards(HomeCards(At(container, "items")), includeShortTiles: true);
            if (grid.Count > 0) shelves.Add((new("", grid.DistinctBy(video => video.VideoId).ToArray()), false));
        }
        if (initial)
        {
            var recommended = shelves.FindIndex(row => !row.Shorts && string.Equals(row.Shelf.Title, "Recommended", StringComparison.OrdinalIgnoreCase));
            if (recommended < 0) recommended = shelves.FindIndex(row => !row.Shorts && row.Shelf.Title.Length == 0);
            var offset = 0;
            if (recommended >= 0)
            {
                var row = shelves[recommended];
                shelves.RemoveAt(recommended);
                shelves.Insert(0, (row.Shelf with { Title = "Recommended" }, false));
                offset = 1;
            }
            var shorts = shelves.FindIndex(row => row.Shorts);
            if (shorts >= 0)
            {
                var row = shelves[shorts];
                shelves.RemoveAt(shorts);
                shelves.Insert(offset, row);
            }
        }
        var result = shelves.Select(row => row.Shelf.Title.Length == 0 ? row.Shelf with { Title = "More videos" } : row.Shelf).ToArray();
        return new HomePage(result, cursor, result.Length == 0
            ? initial ? "YouTube did not return any Home videos for this account." : "YouTube did not return any videos on this Home page."
            : null);
    }

    // Accept direct video cards only. Ads, playlists and nested teaser commands
    // do not become Home videos even if they contain a valid video identifier.
    private static IEnumerable<(string Kind, JsonElement Value)> HomeCards(JsonElement items) => ArrayItems(items)
        .Where(item => item.ValueKind == JsonValueKind.Object)
        .SelectMany(item => item.EnumerateObject())
        .Where(card => card.Name is "gridVideoRenderer" or "videoRenderer" or "tileRenderer" or "lockupViewModel" or "shortsLockupViewModel")
        .Select(card => (card.Name, card.Value));

    private static IEnumerable<JsonElement> ArrayItems(JsonElement array) => array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
}
