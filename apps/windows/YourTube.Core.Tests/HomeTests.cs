using System.Text.Json;
using Xunit;
using static YourTube.Core.Tests.ClientTests;

namespace YourTube.Core.Tests;

public sealed class HomeTests
{
    // Reduced shapes from the pinned TV Home and section continuation fixtures.
    private const string Rows = """
    [
      {"shelfRenderer":{"title":{"simpleText":"Music"},"content":{"horizontalListRenderer":{"items":[{"videoRenderer":{"videoId":"music000001","title":"Music fixture"}}],"continuations":[{"nextContinuationData":{"continuation":"row-only"}}]}}}},
      {"shelfRenderer":{"tvhtml5ShelfRendererType":"TVHTML5_SHELF_RENDERER_TYPE_SHORTS","content":{"horizontalListRenderer":{"items":[{"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_SHORTS","onSelectCommand":{"reelWatchEndpoint":{"videoId":"short000001"}},"header":{"tileHeaderRenderer":{"thumbnailOverlays":[{"tileMetadataRenderer":{"title":{"simpleText":"Short fixture"}}}]}}}}]}}}},
      {"shelfRenderer":{"headerRenderer":{"shelfHeaderRenderer":{"avatarLockup":{"avatarLockupRenderer":{"title":{"runs":[{"text":"Recommended"}]}}}}},"content":{"horizontalListRenderer":{"items":[{"videoRenderer":{"videoId":"recom000001","title":"Recommendation"}},{"adSlotRenderer":{"videoRenderer":{"videoId":"ad000000001"}}}]}}}},
      {"shelfRenderer":{"content":{"horizontalListRenderer":{"items":[{"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_PLAYLIST","onSelectCommand":{"watchEndpoint":{"videoId":"list0000001"}}}}]}}}}
    ]
    """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeKeepsShelvesAndUsesOnlyThePageCursor(bool append)
    {
        var section = "{\"contents\":" + Rows + ",\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"page-next\"}}]}";
        var body = append ? "{\"continuationContents\":{\"sectionListContinuation\":" + section + "}}"
            : "{\"contents\":{\"tvBrowseRenderer\":{\"content\":{\"tvSurfaceContentRenderer\":{\"content\":{\"sectionListRenderer\":" + section + "}}}}}}";
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHomeAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Brand", null, "fixture-brand"), append ? "page-current" : null);
        Assert.Equal(append ? new[] { "Music", "Shorts", "Recommended" } : ["Recommended", "Shorts", "Music"], page.Shelves.Select(s => s.Title));
        Assert.Equal("recom000001", Assert.Single(page.Shelves.Single(s => s.Title == "Recommended").Videos).VideoId);
        var shortVideo = Assert.Single(page.Shelves.Single(s => s.Title == "Shorts").Videos);
        Assert.Equal("short000001", shortVideo.VideoId);
        Assert.Equal("Short fixture", shortVideo.Title);
        Assert.Equal("page-next", page.Continuation);
        Assert.Null(page.Message);
        var request = transport.Requests[2];
        Assert.Equal("https://www.youtube.com/youtubei/v1/browse", request.Url);
        Assert.Equal("Bearer fixture-access", request.Auth);
        Assert.Equal("fixture-brand", request.Page);
        Assert.Equal("https://www.youtube.com/tv", request.Referer);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("fixture-brand", json.RootElement.GetProperty("context").GetProperty("user").GetProperty("onBehalfOfUser").GetString());
        Assert.Equal(append ? "page-current" : "default", json.RootElement.GetProperty(append ? "continuation" : "browseId").GetString());
        Assert.False(json.RootElement.TryGetProperty(append ? "browseId" : "continuation", out _));
    }

    [Fact]
    public async Task UntitledVideoRowDoesNotInventShortsOrUseRowCursor()
    {
        const string body = """
        {"contents":{"sectionListRenderer":{"contents":[{"shelfRenderer":{"content":{"horizontalListRenderer":{"items":[{"videoRenderer":{"videoId":"recom000001"}}],"continuations":[{"nextContinuationData":{"continuation":"shelf-only"}}]}}}}]}}}
        """;
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHomeAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Equal("Recommended", Assert.Single(page.Shelves).Title);
        Assert.Null(page.Continuation);
        Assert.Null(transport.Requests[2].Page);
    }
}
