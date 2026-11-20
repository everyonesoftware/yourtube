using System.Net;
using System.Text.Json;
using YourTube.Core;
using Xunit;
using static YourTube.Core.Tests.ClientTests;

namespace YourTube.Core.Tests;

public sealed class SubscriptionsTests
{
    // Synthetic, reduced structural fixture following the pinned BrowseResultTV model.
    internal const string Initial = """
    {"header":{"videoRenderer":{"videoId":"decoy000000","title":{"simpleText":"Not a subscription"}}},"contents":{"tvBrowseRenderer":{"content":{"tvSecondaryNavRenderer":{"sections":[{"tvSecondaryNavSectionRenderer":{"tabs":[
      {"tabRenderer":{"title":"All","endpoint":{"browseEndpoint":{"browseId":"FEsubscriptions"}},"selected":true,"content":{"tvSurfaceContentRenderer":{"header":{"continuations":[{"nextContinuationData":{"continuation":"wrong-header"}}]},"content":{"sectionListRenderer":{"contents":[{"gridRenderer":{"items":[{"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"aaaaaaaaaaa"}},"metadata":{"tileMetadataRenderer":{"title":{"simpleText":"Latest fixture"},"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":{"simpleText":"Fixture Channel"}}}]}}]}}}}]}}],"continuations":[{"nextContinuationData":{"continuation":"next-page"}}]}}}}}},
      {"tabRenderer":{"title":"Fixture Channel","endpoint":{"browseEndpoint":{"browseId":"FEsubscriptions","params":"opaque-channel"}},"selected":false,"thumbnail":{"thumbnails":[]},"presentationStyle":{"style":"NEW_CONTENT"},"content":{"gridRenderer":{"items":[{"videoRenderer":{"videoId":"bbbbbbbbbbb"}}]}}}},
      {"expandableTabRenderer":{"title":"Second channel","endpoint":{"browseEndpoint":{"browseId":"FEsubscriptions","params":"opaque-second-channel"}},"thumbnail":{"thumbnails":[]}}}
    ]}}]}}}}}
    """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestFeedExcludesMostRelevantShelfWhenChronologicalRowsExist(bool continuation)
    {
        var response = System.Text.Json.Nodes.JsonNode.Parse(Initial)!;
        var section = response["contents"]!["tvBrowseRenderer"]!["content"]!["tvSecondaryNavRenderer"]!["sections"]![0]!["tvSecondaryNavSectionRenderer"]!["tabs"]![0]!["tabRenderer"]!["content"]!["tvSurfaceContentRenderer"]!["content"]!["sectionListRenderer"]!;
        static object Card(string id) => new { videoRenderer = new { videoId = id, title = new { simpleText = id } } };
        section["contents"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new object[] {
            new { gridRenderer = new { items = new[] { Card("decoy000001"), Card("decoy000002"), Card("decoy000003"), Card("decoy000004") } } },
            new { shelfRenderer = new { content = new { horizontalListRenderer = new { items = new[] { Card("latest00001"), Card("latest00002"), Card("latest00003") } } } } }
        }));
        var body = continuation ? "{\"continuationContents\":{\"sectionListContinuation\":" + section.ToJsonString() + "}}" : response.ToJsonString();
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null), continuation ? "page2" : null);
        Assert.Equal(new[] { "latest00001", "latest00002", "latest00003" }, page.Videos.Select(v => v.VideoId));
        Assert.Equal("next-page", page.Continuation);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("sectionListContinuation")]
    [InlineData("onResponseReceivedActions")]
    [InlineData("onResponseReceivedEndpoints")]
    public async Task ChronologicalRowsPreserveTrailingCommandCursor(string envelope)
    {
        var response = System.Text.Json.Nodes.JsonNode.Parse(Initial)!;
        var section = response["contents"]!["tvBrowseRenderer"]!["content"]!["tvSecondaryNavRenderer"]!["sections"]![0]!["tvSecondaryNavSectionRenderer"]!["tabs"]![0]!["tabRenderer"]!["content"]!["tvSurfaceContentRenderer"]!["content"]!["sectionListRenderer"]!;
        section.AsObject().Remove("continuations");
        section["contents"] = System.Text.Json.Nodes.JsonNode.Parse("""
        [
          {"gridRenderer":{"items":[
            {"videoRenderer":{"videoId":"decoy000001"}},
            {"videoRenderer":{"videoId":"decoy000002"}},
            {"videoRenderer":{"videoId":"decoy000003"}},
            {"videoRenderer":{"videoId":"decoy000004"}}
          ],"continuations":[{"nextContinuationData":{"continuation":"wrong-shelf"}}]}},
          {"shelfRenderer":{"content":{"horizontalListRenderer":{"items":[
            {"videoRenderer":{"videoId":"latest00001"}},
            {"videoRenderer":{"videoId":"latest00002"}},
            {"videoRenderer":{"videoId":"latest00003"}}
          ]}}}},
          {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"next-command-page"}}}}
        ]
        """);
        var body = envelope switch
        {
            "initial" => response.ToJsonString(),
            "sectionListContinuation" => "{\"continuationContents\":{\"sectionListContinuation\":" + section.ToJsonString() + "}}",
            _ => "{\"header\":{\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"wrong-header\"}}]},\"" + envelope + "\":[{\"appendContinuationItemsAction\":{\"continuationItems\":" + section["contents"]!.ToJsonString() + "}}]}"
        };
        using var transport = new Transport(Html, Script, body);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null), envelope == "initial" ? null : "page2");
        Assert.Equal(new[] { "latest00001", "latest00002", "latest00003" }, page.Videos.Select(v => v.VideoId));
        Assert.Equal("next-command-page", page.Continuation);
    }

    [Theory]
    [InlineData("gridContinuation")]
    [InlineData("sectionListContinuation")]
    [InlineData("horizontalListContinuation")]
    public async Task ContinuationUsesOpaqueTokenWithoutRestartOrChannelNavigation(string kind)
    {
        var response = "{\"continuationContents\":{\"" + kind + "\":{\"items\":[{\"videoRenderer\":{\"videoId\":\"ccccccccccc\",\"title\":{\"simpleText\":\"Older fixture\"}}}],\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"last-page\"}}]}}}";
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null), "opaque+/=token");
        Assert.Empty(page.Channels);
        Assert.Equal("ccccccccccc", Assert.Single(page.Videos).VideoId);
        Assert.Equal("last-page", page.Continuation);
        Assert.Null(transport.Requests[^1].Page);
        using var doc = JsonDocument.Parse(transport.Requests[^1].Body);
        Assert.Equal("opaque+/=token", doc.RootElement.GetProperty("continuation").GetString());
        Assert.False(doc.RootElement.TryGetProperty("browseId", out _));
        Assert.False(doc.RootElement.GetProperty("context").GetProperty("user").TryGetProperty("onBehalfOfUser", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingAccountOrBlankContinuationIsRejectedBeforeNetwork(string? token)
    {
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), token is null ? null! : new("Owner", null, null), token));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task EmptyTabListIsHonestEmptyRatherThanSchemaFailure()
    {
        using var transport = new Transport(Html, Script, "{\"contents\":{\"tvBrowseRenderer\":{\"content\":{\"tvSecondaryNavRenderer\":{\"sections\":[{\"tvSecondaryNavSectionRenderer\":{\"tabs\":[]}}]}}}}}");
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Empty(page.Channels);
        Assert.Empty(page.Videos);
        Assert.Null(page.Continuation);
        Assert.Contains("No subscriptions", page.Message);
    }

    [Fact]
    public async Task MissingAggregateBrowseIdUsesFirstTvTabLikePinnedUpstream()
    {
        using var transport = new Transport(Html, Script, Initial.Replace("FEsubscriptions", "fixture-no-aggregate-browse-id"));
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Equal("aaaaaaaaaaa", Assert.Single(page.Videos).VideoId);
        Assert.Equal(2, page.Channels.Count);
    }

    [Theory]
    [InlineData("{}", "invalid_response")]
    [InlineData("{\"contents\":{\"tvBrowseRenderer\":{\"content\":{\"tvSecondaryNavRenderer\":{\"sections\":[{\"tvSecondaryNavSectionRenderer\":{\"tabs\":[{\"unknownRenderer\":{}}]}}]}}}}}", "invalid_response")]
    [InlineData("{\"error\":\"invalid_token\",\"private\":\"do-not-display\"}", "auth_required")]
    [InlineData("{\"responseContext\":{\"mainAppWebResponseContext\":{\"loggedOut\":true}}}", "auth_required")]
    public async Task UnknownOrAuthenticatedFailureNeverBecomesSuccessfulEmpty(string response, string code)
    {
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null)));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("do-not-display", error.ToString());
    }

    [Fact]
    public async Task ChannelTabsMayShareFeSubscriptionsBrowseIdAndChannelNames()
    {
        // The pinned real TV fixtures use FEsubscriptions for every tab, not UC IDs.
        var response = Initial.Replace("Second channel", "Fixture Channel");
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Equal(new[] { "Fixture Channel", "Fixture Channel" }, page.Channels.Select(c => c.Name));
        Assert.Equal("aaaaaaaaaaa", Assert.Single(page.Videos).VideoId);
        Assert.Equal("next-page", page.Continuation);
    }

    [Fact]
    public async Task InitialTvBrowseLoadsBothChannelsAndLatestVideosForSelectedIdentity()
    {
        using var transport = new Transport(Html, Script, Initial);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetSubscriptionsAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Brand", "@brand", "brand-page"));
        Assert.Equal(new[] { new SubscribedChannel("Fixture Channel", true), new SubscribedChannel("Second channel", false) }, page.Channels);
        Assert.Equal("aaaaaaaaaaa", Assert.Single(page.Videos).VideoId);
        Assert.Equal("Latest fixture", page.Videos[0].Title);
        Assert.Equal("next-page", page.Continuation);
        Assert.Null(page.Message);
        var request = transport.Requests[^1];
        Assert.Equal("https://www.youtube.com/youtubei/v1/browse", request.Url);
        Assert.Equal("https://www.youtube.com/tv", request.Referer);
        Assert.Equal("Bearer fixture-access", request.Auth);
        Assert.Equal("brand-page", request.Page);
        using var doc = JsonDocument.Parse(request.Body);
        var body = doc.RootElement;
        Assert.Equal("FEsubscriptions", body.GetProperty("browseId").GetString());
        Assert.Equal("brand-page", body.GetProperty("context").GetProperty("user").GetProperty("onBehalfOfUser").GetString());
        Assert.Equal("TVHTML5", body.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString());
        Assert.True(body.GetProperty("racyCheckOk").GetBoolean());
        Assert.True(body.GetProperty("contentCheckOk").GetBoolean());
        Assert.False(body.TryGetProperty("continuation", out _));
    }
}
