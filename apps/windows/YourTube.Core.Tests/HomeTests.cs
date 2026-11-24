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
    public async Task HomeUsesOnlyPublicationMetadataFromTheCard()
    {
        const string body = """
        {"contents":{"sectionListRenderer":{"contents":[
          {"shelfRenderer":{"title":"Recommended","content":{"horizontalListRenderer":{"items":[
            {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"tile0000001"}},"metadata":{"tileMetadataRenderer":{"title":"Tile","lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":{"simpleText":"Example Channel"}}}]}},{"lineRenderer":{"items":[{"lineItemRenderer":{"text":{"simpleText":"358K views"}}},{"lineItemRenderer":{"text":{"simpleText":"•"}}},{"lineItemRenderer":{"text":{"simpleText":"3 months ago"}}}]}}]}}}},
            {"videoRenderer":{"videoId":"recom000001","publishedTimeText":{"runs":[{"text":"2 days"},{"text":" ago"}]},"publishDate":"2026-11-22"}},
            {"gridVideoRenderer":{"videoId":"decoy000001","shortBylineText":"4 days ago","publishedTimeText":{"runs":false},"publishDate":"2026-02-30","navigationEndpoint":{"publishedTimeText":"7 days ago"}}},
            {"lockupViewModel":{"contentId":"modern00001","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"Example Channel"}}]},{"metadataParts":[{"text":{"content":"400 views"}},{"text":{"content":"Streamed 1 day ago"}}]}]}}}}}}
          ]}}}},
          {"shelfRenderer":{"tvhtml5ShelfRendererType":"TVHTML5_SHELF_RENDERER_TYPE_SHORTS","content":{"horizontalListRenderer":{"items":[
            {"shortsLockupViewModel":{"onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"short000001"}}},"overlayMetadata":{"primaryText":{"content":"Short"},"secondaryText":{"content":"500 views"}}}},
            {"shortsLockupViewModel":{"onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"short000002"}}},"publishedTimeText":{"content":"5 hours ago"}}}
          ]}}}}
        ]}}}
        """;
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHomeAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        var regular = page.Shelves.Single(s => s.Title == "Recommended").Videos;
        Assert.Equal(new string?[] { "3 months ago", "2 days ago", null, "Streamed 1 day ago" }, regular.Select(v => v.PublishedAge));
        Assert.Equal(new DateOnly(2026, 11, 22), regular[1].PublishedDate);
        Assert.Null(regular[0].PublishedDate);
        Assert.Null(regular[2].PublishedDate);
        Assert.Equal(new string?[] { null, "5 hours ago" }, page.Shelves.Single(s => s.Title == "Shorts").Videos.Select(v => v.PublishedAge));
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task TvShortsDoNotTreatAgeOrViewCountAsTheChannel()
    {
        const string body = """
        {"contents":{"sectionListRenderer":{"contents":[{"shelfRenderer":{
          "tvhtml5ShelfRendererType":"TVHTML5_SHELF_RENDERER_TYPE_SHORTS","content":{"horizontalListRenderer":{"items":[
            {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_SHORTS","onSelectCommand":{"reelWatchEndpoint":{"videoId":"short000001"}},"metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":"5 hours ago"}}]}}]}}}},
            {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_SHORTS","shortBylineText":"Shorts Channel","onSelectCommand":{"reelWatchEndpoint":{"videoId":"short000002"}},"metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":"500 views"}},{"lineItemRenderer":{"text":"2 days ago"}}]}}]}}}}
          ]}}}}
        ]}}}
        """;
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHomeAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        var videos = Assert.Single(page.Shelves).Videos;
        Assert.Equal(new (string, string?)[] { ("", "5 hours ago"), ("Shorts Channel", "2 days ago") }, videos.Select(v => (v.Channel, v.PublishedAge)));
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
