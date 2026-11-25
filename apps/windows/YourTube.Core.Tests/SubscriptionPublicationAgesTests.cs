using System.Text.Json;
using System.Text.Json.Nodes;
using YourTube.Core;
using Xunit;
using static YourTube.Core.Tests.ClientTests;

namespace YourTube.Core.Tests;

public sealed class SubscriptionPublicationAgesTests
{
    [Theory]
    [InlineData("Home")]
    [InlineData("History")]
    [InlineData("All Subscriptions")]
    public async Task SharedFeedsKeepALoneAgeLikeTileByline(string feed)
    {
        const string grid = """
        {"continuationContents":{"gridContinuation":{"items":[
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00001"}},
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[
              {"lineItemRenderer":{"text":{"simpleText":"2 weeks ago"}}}
            ]}}]}}}}
        ]}}}
        """;
        using var transport = new Transport(Html, Script, grid);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Owner", null, null);
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        var videos = feed switch
        {
            "Home" => Assert.Single((await client.GetHomeAsync(session, account, "page-current")).Shelves).Videos,
            "History" => (await client.GetHistoryAsync(session, account, "page-current")).Videos,
            "All Subscriptions" => (await client.GetSubscriptionsAsync(session, account, "page-current")).Videos,
            _ => throw new ArgumentOutOfRangeException(nameof(feed))
        };

        var video = Assert.Single(videos);
        Assert.Equal(("2 weeks ago", (string?)null, (DateOnly?)null), (video.Channel, video.PublishedAge, video.PublishedDate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelGridReadsPublicationFromRecognizableStatsRows(bool append)
    {
        // The channel-grid response is synthetic. Its tile text items use the same
        // runs, view count, and separator structure as the pinned TV subscriptions fixtures.
        const string grid = """
        {"continuationContents":{"gridContinuation":{"items":[
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00001"}},
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[
              {"lineItemRenderer":{"badge":{"metadataBadgeRenderer":{"label":"New"}}}},
              {"lineItemRenderer":{"text":{"simpleText":"1.2K views"}}},
              {"lineItemRenderer":{"text":{"simpleText":"•"}}},
              {"lineItemRenderer":{"text":{"runs":[{"text":"2 hours"},{"text":" ago"}]}}}
            ]}}]}}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00002"}},
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[
              {"lineItemRenderer":{"text":{"simpleText":"2 weeks ago"}}}
            ]}}]}}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00003"}},
            "publishedTimeText":{"simpleText":"Just now"},"publishDate":"2026-11-20",
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[
              {"lineItemRenderer":{"text":{"simpleText":"30 views"}}},
              {"lineItemRenderer":{"text":{"simpleText":"3 days ago"}}}
            ]}}]}}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00004"}},
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[
              {"lineItemRenderer":{"text":{"simpleText":"31 watching"}}},
              {"lineItemRenderer":{"text":{"runs":false}}},
              {"lineItemRenderer":{"text":{"simpleText":"Premieres in 2 days"}}}
            ]}}]}},"navigationEndpoint":{"publishedTimeText":{"simpleText":"6 days ago"}}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"upload00005"}},
            "metadata":{"tileMetadataRenderer":{"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":"2 weeks ago"}}]}},
              {"lineRenderer":{"items":[{"lineItemRenderer":{"text":"400 views"}},{"lineItemRenderer":{"text":"3 weeks ago"}}]}}
            ]}}}}
        ]}}}
        """;
        const string firstPage = """
        {"continuationContents":{"gridContinuation":{"items":[],"continuations":[{"nextContinuationData":{"continuation":"channel-next"}}]}}}
        """;
        var responses = append ? new[] { Html, Script, SubscriptionsWithReload(), firstPage, grid }
            : new[] { Html, Script, SubscriptionsWithReload(), grid };
        using var transport = new Transport(responses);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Brand", "@brand", "brand-page");
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        var subscriptions = await client.GetSubscriptionsAsync(session, account);
        var channel = subscriptions.Channels[0];
        var cursor = append ? (await client.GetSubscriptionChannelAsync(session, account, channel)).Continuation : null;
        if (append) Assert.NotNull(cursor);
        var page = await client.GetSubscriptionChannelAsync(session, account, channel, cursor);

        Assert.Equal(new[] { "upload00001", "upload00002", "upload00003", "upload00004", "upload00005" }, page.Videos.Select(v => v.VideoId));
        Assert.Equal(new string?[] { "2 hours ago", null, "Just now", null, "3 weeks ago" }, page.Videos.Select(v => v.PublishedAge));
        Assert.Equal(new[] { "", "2 weeks ago", "", "", "2 weeks ago" }, page.Videos.Select(v => v.Channel));
        Assert.Equal(new DateOnly?[] { null, null, new(2026, 11, 20), null, null }, page.Videos.Select(v => v.PublishedDate));
        Assert.Equal(append ? 5 : 4, transport.Requests.Count);
        Assert.Equal("brand-page", transport.Requests[^1].Page);
        using var requestBody = JsonDocument.Parse(transport.Requests[^1].Body);
        Assert.Equal(append ? "channel-next" : "channel-reload", requestBody.RootElement.GetProperty("continuation").GetString());
    }

    private static string SubscriptionsWithReload()
    {
        var response = JsonNode.Parse(SubscriptionsTests.Initial)!;
        var tabs = response["contents"]!["tvBrowseRenderer"]!["content"]!["tvSecondaryNavRenderer"]!["sections"]![0]!["tvSecondaryNavSectionRenderer"]!["tabs"]!;
        tabs[1]!["tabRenderer"]!["content"] = JsonNode.Parse("""
            {"tvSurfaceContentRenderer":{"continuation":{"reloadContinuationData":{"continuation":"channel-reload"}}}}
            """);
        return response.ToJsonString();
    }
}
