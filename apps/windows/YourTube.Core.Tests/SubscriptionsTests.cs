using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static string WithChannelReloads()
    {
        var response = JsonNode.Parse(Initial)!;
        var tabs = response["contents"]!["tvBrowseRenderer"]!["content"]!["tvSecondaryNavRenderer"]!["sections"]![0]!["tvSecondaryNavSectionRenderer"]!["tabs"]!;
        void AddReload(int index, string renderer, string token)
        {
            var tab = tabs[index]![renderer]!;
            tab["content"] ??= new JsonObject();
            tab["content"]!["tvSurfaceContentRenderer"] ??= new JsonObject();
            tab["content"]!["tvSurfaceContentRenderer"]!["continuation"] = new JsonObject
            {
                ["reloadContinuationData"] = new JsonObject { ["continuation"] = token }
            };
        }
        AddReload(0, "tabRenderer", "wrong-aggregate-reload");
        AddReload(1, "tabRenderer", "channel-first");
        AddReload(2, "expandableTabRenderer", "channel-second+/=");
        tabs[2]!["expandableTabRenderer"]!["title"] = "Fixture Channel";
        return response.ToJsonString();
    }

    [Fact]
    public async Task ReloadSelectsTheActualTabEvenWhenTitlesAndBrowseIdsMatch()
    {
        const string channelBody = """
        {"continuationContents":{"sectionListContinuation":{"contents":[{"shelfRenderer":{"content":{"horizontalListRenderer":{"items":[
          {"videoRenderer":{"videoId":"ccccccccccc","title":{"simpleText":"Channel upload"},"shortBylineText":{"simpleText":"Not the tab title"}}}
        ]}}}}]}}}
        """;
        using var transport = new Transport(Html, Script, WithChannelReloads(), channelBody);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        var account = new YouTubeAccount("Brand", "@brand", "brand-page");
        var aggregate = await client.GetSubscriptionsAsync(session, account);
        Assert.Equal(new[] { "Fixture Channel", "Fixture Channel" }, aggregate.Channels.Select(c => c.Name));
        Assert.All(aggregate.Channels, c => Assert.True(c.CanLoadVideos));
        Assert.DoesNotContain("channel-second", aggregate.Channels[1].ToString());

        var page = await client.GetSubscriptionChannelAsync(session, account, aggregate.Channels[1]);
        Assert.Empty(page.Channels);
        var video = Assert.Single(page.Videos);
        Assert.Equal("ccccccccccc", video.VideoId);
        Assert.Equal("Not the tab title", video.Channel);
        Assert.Null(page.Continuation);
        Assert.Null(page.Message);
        Assert.Equal(4, transport.Requests.Count);
        var request = transport.Requests[^1];
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://www.youtube.com/youtubei/v1/browse", request.Url);
        Assert.Equal("https://www.youtube.com/tv", request.Referer);
        Assert.Equal("Bearer fixture-access", request.Auth);
        Assert.Equal("brand-page", request.Page);
        using var doc = JsonDocument.Parse(request.Body);
        var json = doc.RootElement;
        Assert.Equal("channel-second+/=", json.GetProperty("continuation").GetString());
        Assert.False(json.TryGetProperty("params", out _));
        Assert.False(json.TryGetProperty("browseId", out _));
        Assert.True(json.GetProperty("racyCheckOk").GetBoolean());
        Assert.True(json.GetProperty("contentCheckOk").GetBoolean());
        Assert.Equal("TVHTML5", json.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString());
        Assert.Equal("brand-page", json.GetProperty("context").GetProperty("user").GetProperty("onBehalfOfUser").GetString());
    }

    [Fact]
    public async Task ChannelPageCursorIsStableForRetryAndCannotCrossTabAccountOrAggregate()
    {
        const string response = """
        {"continuationContents":{"gridContinuation":{"items":[{"videoRenderer":{"videoId":"ccccccccccc"}}],
         "continuations":[{"nextContinuationData":{"continuation":"provider-page+/="}}]}}}
        """;
        using var transport = new Transport(Html, Script, WithChannelReloads(), response, response, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        var account = new YouTubeAccount("Owner", null, null);
        var aggregate = await client.GetSubscriptionsAsync(session, account);
        var channel = aggregate.Channels[0];
        var first = await client.GetSubscriptionChannelAsync(session, account, channel);
        var secondTab = await client.GetSubscriptionChannelAsync(session, account, aggregate.Channels[1]);
        Assert.NotNull(first.Continuation);
        Assert.NotNull(secondTab.Continuation);
        Assert.NotEqual(first.Continuation, secondTab.Continuation);
        Assert.DoesNotContain("provider-page", first.Continuation);

        foreach (var rejected in new Func<Task>[]
        {
            () => client.GetSubscriptionChannelAsync(session, account, aggregate.Channels[1], first.Continuation),
            () => client.GetSubscriptionChannelAsync(session, account with { }, channel, first.Continuation),
            () => client.GetSubscriptionChannelAsync(session, account, channel, aggregate.Continuation),
            () => client.GetSubscriptionChannelAsync(session, account, channel, "provider-page+/="),
            () => client.GetSubscriptionChannelAsync(session, account, new SubscribedChannel(channel.Name, true)),
            () => client.GetSubscriptionsAsync(session, account, first.Continuation)
        })
            Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<YouTubeException>(rejected)).Code);
        Assert.Equal(5, transport.Requests.Count);

        var next = await client.GetSubscriptionChannelAsync(session, account, channel, first.Continuation);
        Assert.Equal(first.Continuation, next.Continuation); // UI can detect repetition without reading provider tokens.
        Assert.Equal(6, transport.Requests.Count);
        Assert.Null(transport.Requests[^1].Page);
        using var doc = JsonDocument.Parse(transport.Requests[^1].Body);
        Assert.Equal("provider-page+/=", doc.RootElement.GetProperty("continuation").GetString());
        Assert.False(doc.RootElement.GetProperty("context").GetProperty("user").TryGetProperty("onBehalfOfUser", out _));
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("body-reload")]
    [InlineData("browse-only")]
    public async Task ChannelResultsUseOnlyTheContinuationBody(string kind)
    {
        var response = JsonNode.Parse("""
        {"contents":{"tvBrowseRenderer":{"content":{"tvSurfaceContentRenderer":{"content":{"gridRenderer":{"items":[
          {"videoRenderer":{"videoId":"aaaaaaaaaaa"}},
          {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"wrong-browse"}}}}
        ]}}}}}}}
        """)!;
        // Both header and ordinary browse content have parseable cards and cursors.
        response["onResponseReceivedActions"] = JsonNode.Parse("""
        [{"reloadContinuationItemsCommand":{"slot":"RELOAD_CONTINUATION_SLOT_HEADER","continuationItems":[
           {"videoRenderer":{"videoId":"decoy000001"}},
           {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"wrong-header"}}}}
        ]}}]
        """);
        var body = JsonNode.Parse("""
        [{"videoRenderer":{"videoId":"ccccccccccc"}},
         {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"selected-body"}}}}]
        """);
        if (kind == "grid") response["continuationContents"] = new JsonObject { ["gridContinuation"] = new JsonObject { ["items"] = body } };
        if (kind == "body-reload") response["onResponseReceivedActions"]!.AsArray().Add(new JsonObject
        {
            ["reloadContinuationItemsCommand"] = new JsonObject
            {
                ["slot"] = "RELOAD_CONTINUATION_SLOT_BODY", ["continuationItems"] = body
            }
        });
        using var transport = new Transport(Html, Script, WithChannelReloads(), response.ToJsonString(), "{\"continuationContents\":{}}");
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        var account = new YouTubeAccount("Owner", null, null);
        var channel = (await client.GetSubscriptionsAsync(session, account)).Channels[0];
        if (kind == "browse-only")
        {
            Assert.Equal("invalid_response", (await Assert.ThrowsAsync<YouTubeException>(
                () => client.GetSubscriptionChannelAsync(session, account, channel))).Code);
            Assert.Equal(4, transport.Requests.Count);
            return;
        }
        var page = await client.GetSubscriptionChannelAsync(session, account, channel);
        Assert.Equal("ccccccccccc", Assert.Single(page.Videos).VideoId);
        Assert.NotNull(page.Continuation);
        var empty = await client.GetSubscriptionChannelAsync(session, account, channel, page.Continuation);
        Assert.Empty(empty.Videos);
        Assert.Null(empty.Continuation);
        Assert.Equal("No videos were returned for this subscribed channel.", empty.Message);
        using var doc = JsonDocument.Parse(transport.Requests[^1].Body);
        Assert.Equal("selected-body", doc.RootElement.GetProperty("continuation").GetString());
    }

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
