using System.Net;
using System.Text;
using System.Text.Json;
using YourTube.Core;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class ClientTests
{
    internal const string Html = "<script id=\"base-js\" src=\"/s/tv/base.js\"></script>\"INNERTUBE_CLIENT_VERSION\":\"7.test\",\"visitorData\":\"visitor\"";
    internal const string Script = "clientId:\"first.apps.googleusercontent.com\",secret:\"test_secret\";clientId:\"wrong.apps.googleusercontent.com\",secret:\"wrong\"";
    internal const string Device = "{\"user_code\":\"TEST-CODE\",\"device_code\":\"private-device\",\"verification_url\":\"https://www.youtube.com/activate\",\"expires_in\":1800,\"interval\":5}";
    internal const string Token = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
    internal sealed class Clock
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T00:00:00Z");
        public List<double> Delays { get; } = [];
        public Task Delay(TimeSpan time, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Delays.Add(time.TotalSeconds); Now += time; return Task.CompletedTask; }
        public YouTubeClient Client(HttpClient http) => new(http, () => Now, Delay);
    }
    internal sealed class Transport : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth, string? Page, string Agent, string? Referer, string Method)> Requests { get; } = [];
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<Dictionary<string, string>> RequestHeaders { get; } = [];
        public Transport(params string[] responses) { foreach (var body in responses) Responses.Enqueue((HttpStatusCode.OK, body)); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestHeaders.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-Goog-PageId", out var p) ? p.Single() : null, request.Headers.UserAgent.ToString(), request.Headers.Referrer?.AbsoluteUri, request.Method.Method));
            var next = Responses.Dequeue();
            return new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") };
        }
    }
    [Fact]
    public async Task SignInBootstrapsFirstPublicTvCredentialAndIssuesDeviceAuthorization()
    {
        using var transport = new Transport(Html, Script, Device);
        using var http = new HttpClient(transport);
        var before = DateTimeOffset.UtcNow;
        var authorization = await new YouTubeClient(http).BeginSignInAsync("device-uuid");
        Assert.Equal("TEST-CODE", authorization.UserCode);
        Assert.Equal("private-device", authorization.DeviceCode);
        Assert.Equal(5, authorization.IntervalSeconds);
        Assert.InRange(authorization.ExpiresAt, before.AddSeconds(1800), DateTimeOffset.UtcNow.AddSeconds(1800));
        Assert.Equal("https://www.youtube.com/activate", authorization.VerificationUri.AbsoluteUri);
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal("https://www.youtube.com/tv", transport.Requests[0].Url);
        Assert.Equal("https://www.youtube.com/s/tv/base.js", transport.Requests[1].Url);
        var body = JsonDocument.Parse(transport.Requests[2].Body).RootElement;
        Assert.Equal("first.apps.googleusercontent.com", body.GetProperty("client_id").GetString());
        Assert.Equal("device-uuid", body.GetProperty("device_id").GetString());
        Assert.Equal("ytlr::", body.GetProperty("model_name").GetString());
        Assert.Equal("http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content", body.GetProperty("scope").GetString());
    }
    [Theory]
    [InlineData("html", "bootstrap_failed")]
    [InlineData("script", "bootstrap_failed")]
    [InlineData("external", "bootstrap_failed")]
    [InlineData("device", "invalid_response")]
    [InlineData("uri", "invalid_response")]
    [InlineData("error", "auth_required")]
    [InlineData("http", "service_unavailable")]
    public async Task SignInRejectsUnsafeOrMalformedResponsesWithoutLeakingBodies(string failure, string expected)
    {
        var html = failure == "html" ? "private-response" : failure == "external" ? Html.Replace("/s/tv/base.js", "https://evil.example/base.js") : Html;
        var script = failure == "script" ? "private-response" : Script;
        var device = failure == "device" ? "private-response" : failure == "uri" ? Device.Replace("https://www.youtube.com/activate", "https://evil.example/activate") : failure == "error" ? "{\"error\":\"access_denied\",\"error_description\":\"private-response\"}" : Device;
        using var transport = new Transport(html, script, device);
        if (failure == "http") { transport.Responses.Clear(); transport.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, "private-response")); }
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeClient(http).BeginSignInAsync("uuid"));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.DoesNotContain("evil.example", error.ToString());
        if (failure == "external") Assert.Single(transport.Requests);
    }
    [Fact]
    public async Task PollingWaitsForIntervalAndRetriesPendingUntilAuthorized()
    {
        using var transport = new Transport(Html, Script, Device, "{\"error\":\"authorization_pending\"}", Token);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var auth = await client.BeginSignInAsync("uuid");
        var session = await client.CompleteSignInAsync(auth);
        Assert.Equal(new double[] { 5, 5 }, clock.Delays);
        Assert.Equal("test-access", session.AccessToken);
        Assert.Equal("test-refresh", session.RefreshToken);
        Assert.Equal(clock.Now.AddSeconds(3600), session.ExpiresAt);
        var body = JsonDocument.Parse(transport.Requests[3].Body).RootElement;
        Assert.Equal("private-device", body.GetProperty("code").GetString());
        Assert.Equal("test_secret", body.GetProperty("client_secret").GetString());
        Assert.Equal("http://oauth.net/grant_type/device/1.0", body.GetProperty("grant_type").GetString());
        Assert.Equal(5, transport.Requests.Count);
    }
    [Fact]
    public async Task SlowDownPermanentlyAddsFiveSecondsToPollingInterval()
    {
        using var transport = new Transport(Html, Script, "{\"error\":\"slow_down\"}", "{\"error\":\"authorization_pending\"}", Token);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var auth = new DeviceAuthorization("test", new Uri("https://www.youtube.com/activate"), clock.Now.AddHours(1), 5, "test");
        await clock.Client(http).CompleteSignInAsync(auth);
        Assert.Equal(new double[] { 5, 10, 10 }, clock.Delays);
    }
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    [InlineData(7, 1)]
    public async Task ExpiredAuthorizationNeverSendsAnotherPoll(int lifetime, int expectedPolls)
    {
        using var transport = new Transport(Html, Script, "{\"error\":\"authorization_pending\"}", Token);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var auth = new DeviceAuthorization("test", new Uri("https://www.youtube.com/activate"), clock.Now.AddSeconds(lifetime), 5, "test");
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).CompleteSignInAsync(auth));
        Assert.Equal("auth_required", error.Code);
        Assert.Equal(expectedPolls, transport.Requests.Count(r => r.Url.EndsWith("/token")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshUsesRuntimeCredentialAndPreservesOrRotatesRefreshToken(bool rotated)
    {
        using var transport = new Transport(Html, Script, rotated ? Token : Token.Replace("\"refresh_token\":\"test-refresh\",", ""));
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var session = await clock.Client(http).RefreshSessionAsync("original-refresh");
        Assert.Equal("test-access", session.AccessToken);
        Assert.Equal(rotated ? "test-refresh" : "original-refresh", session.RefreshToken);
        Assert.Equal(clock.Now.AddHours(1), session.ExpiresAt);
        var body = JsonDocument.Parse(transport.Requests[2].Body).RootElement;
        Assert.Equal("original-refresh", body.GetProperty("refresh_token").GetString());
        Assert.Equal("refresh_token", body.GetProperty("grant_type").GetString());
        Assert.Equal("test_secret", body.GetProperty("client_secret").GetString());
        Assert.Null(transport.Requests[2].Auth);
    }
    [Fact]
    public async Task AccountPhotosBelongToTheirOwnIdentity()
    {
        const string accounts = """
        {"contents":[
          {"accountItem":{"accountName":{"simpleText":"Owner"},"accountPhoto":{"thumbnails":[{"url":17},{"url":"https://lh3.googleusercontent.com/fixture-owner"}]}}},
          {"accountItem":{"accountName":{"simpleText":"Brand"},"accountPhoto":{"thumbnails":[{"url":"//yt3.ggpht.com/fixture-brand"}]}}},
          {"accountItem":{"accountName":{"simpleText":"No photo"},"decoy":{"accountPhoto":{"thumbnails":[{"url":"https://lh3.googleusercontent.com/decoy"}]}}}}
        ]}
        """;
        using var transport = new Transport(Html, Script, accounts);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var result = await clock.Client(http).GetAccountsAsync(new("test-access", "test-refresh", clock.Now.AddHours(1)));
        Assert.Equal(new Uri("https://lh3.googleusercontent.com/fixture-owner"), result[0].AvatarUri);
        Assert.Equal(new Uri("https://yt3.ggpht.com/fixture-brand"), result[1].AvatarUri);
        Assert.Null(result[2].AvatarUri);
        Assert.DoesNotContain("fixture-owner", result[0].ToString());
    }
    [Fact]
    public async Task AccountsReadsOwnerAndBrandIdentitiesWithBearerOnlyOnPrivateRequest()
    {
        // Synthetic values; field paths follow pinned upstream AccountInt.java, not live account data.
        const string accounts = """
        {"contents":[{"accountSectionListRenderer":{"contents":[{"accountItemSectionRenderer":{"contents":[
          {"accountItem":{"accountName":{"simpleText":"Fixture Owner"},"accountByline":{"simpleText":"@fixture-owner"},"serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[{"accountStateToken":{"obfuscatedGaiaId":"fixture-owner-gaia"}},{"pageIdToken":{"pageId":"fixture-owner-page"}}]}}}},
          {"accountItem":{"accountName":{"runs":[{"text":"Fixture Brand"}]},"accountByline":{"simpleText":"@fixture-brand"},"serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[{"accountStateToken":{"obfuscatedGaiaId":"fixture-brand-gaia"}},{"pageIdToken":{"pageId":"fixture-brand-page"}},{"pageIdToken":{"pageId":"fixture-second-page"}}]}}}},
          {"accountItem":{"accountName":{"simpleText":"Fixture Default"},"serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[{"accountStateToken":{"obfuscatedGaiaId":"fixture-default-gaia"}}]}},"unrelated":{"pageIdToken":{"pageId":"fixture-unrelated-page"}}}},
          {"accountItem":{"accountName":{"simpleText":"Fixture Disabled"},"isDisabled":true}}
        ]}}]}}]}
        """;
        using var transport = new Transport(Html, Script, accounts);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("test-access", "test-refresh", clock.Now.AddHours(1));
        var result = await client.GetAccountsAsync(session);
        Assert.Equal(new[] { new YouTubeAccount("Fixture Owner", "@fixture-owner", "fixture-owner-page"), new YouTubeAccount("Fixture Brand", "@fixture-brand", "fixture-brand-page"), new YouTubeAccount("Fixture Default", null, null) }, result);
        foreach (var account in result)
        {
            transport.Responses.Enqueue((HttpStatusCode.OK, "{\"contents\":{}}"));
            await client.GetHistoryAsync(session, account);
            var request = transport.Requests[^1];
            Assert.Equal(account.PageId, request.Page);
            using var historyBody = JsonDocument.Parse(request.Body);
            var user = historyBody.RootElement.GetProperty("context").GetProperty("user");
            if (account.PageId is null) Assert.False(user.TryGetProperty("onBehalfOfUser", out _));
            else Assert.Equal(account.PageId, user.GetProperty("onBehalfOfUser").GetString());
        }
        Assert.Equal("https://www.youtube.com/youtubei/v1/account/accounts_list", transport.Requests[2].Url);
        Assert.Equal("Bearer test-access", transport.Requests[2].Auth);
        Assert.All(transport.Requests.Take(2), r => Assert.Null(r.Auth));
        var body = JsonDocument.Parse(transport.Requests[2].Body).RootElement;
        Assert.True(body.GetProperty("accountReadMask").GetProperty("returnOwner").GetBoolean());
        Assert.True(body.GetProperty("accountReadMask").GetProperty("returnBrandAccounts").GetBoolean());
        Assert.Equal("TVHTML5", body.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString());
        Assert.Equal("7.test", body.GetProperty("context").GetProperty("client").GetProperty("clientVersion").GetString());
    }
    [Fact]
    public async Task HistoryReadsCardsInOrderAndSendsSelectedAccount()
    {
        const string history = """
        {"contents":{"tvBrowseRenderer":{"content":{"gridRenderer":{"items":[
          {"gridVideoRenderer":{"videoId":"abcdefghijk","title":{"runs":[{"text":"First "},{"text":"video"}]},"shortBylineText":{"simpleText":"Channel"},"lengthText":{"simpleText":"4:32"},"thumbnailOverlays":[{"thumbnailOverlayResumePlaybackRenderer":{"percentDurationWatched":42}}]}},
          {"videoRenderer":{"videoId":"ABCDEFGHIJK","title":{"simpleText":"Second"},"longBylineText":{"runs":[{"text":"Other"}]}}}
        ],"continuations":[{"nextContinuationData":{"continuation":"next-page"}}]}}}}}
        """;
        using var transport = new Transport(Html, Script, history);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var result = await clock.Client(http).GetHistoryAsync(new("test-access", "refresh", clock.Now.AddHours(1)), new("Brand", "@brand", "456"));
        Assert.Equal(new[] { new HistoryVideo("abcdefghijk", "First video", "Channel", "4:32", 42), new HistoryVideo("ABCDEFGHIJK", "Second", "Other", "", null) }, result.Videos);
        Assert.Equal("next-page", result.Continuation);
        Assert.Null(result.Message);
        Assert.Equal("https://www.youtube.com/youtubei/v1/browse", transport.Requests[2].Url);
        Assert.Equal("Bearer test-access", transport.Requests[2].Auth);
        Assert.Equal("456", transport.Requests[2].Page);
        var body = JsonDocument.Parse(transport.Requests[2].Body).RootElement;
        Assert.Equal("FEhistory", body.GetProperty("browseId").GetString());
        Assert.Equal("456", body.GetProperty("context").GetProperty("user").GetProperty("onBehalfOfUser").GetString());
    }
    [Theory]
    [InlineData("{\"continuationContents\":{\"gridContinuation\":{\"items\":[],\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"second-next\"}}]}}}")]
    [InlineData("{\"onResponseReceivedActions\":[{\"appendContinuationItemsAction\":{\"continuationItems\":[{\"continuationItemRenderer\":{\"continuationEndpoint\":{\"continuationCommand\":{\"token\":\"second-next\"}}}}]}}]}")]
    public async Task ContinuationRequestsDoNotRestartHistoryOrLeakPreviousAccountHeader(string response)
    {
        using var transport = new Transport(Html, Script, "{\"contents\":{}}", response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("test-access", "refresh", clock.Now.AddHours(1));
        await client.GetHistoryAsync(session, new("Brand", null, "456"));
        var page = await client.GetHistoryAsync(session, new("Owner", null, null), "opaque-page+/%=");
        Assert.Equal("second-next", page.Continuation);
        Assert.Null(transport.Requests[3].Page);
        var body = JsonDocument.Parse(transport.Requests[3].Body).RootElement;
        Assert.Equal("opaque-page+/%=", body.GetProperty("continuation").GetString());
        Assert.False(body.TryGetProperty("browseId", out _));
        Assert.False(body.GetProperty("context").GetProperty("user").TryGetProperty("onBehalfOfUser", out _));
        Assert.Equal(4, transport.Requests.Count);
    }
    [Fact]
    public async Task HistoryReadsTvTilesAndIgnoresPlaylistAndInvalidCards()
    {
        const string history = """
        {"contents":{"items":[
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","metadata":{"tileMetadataRenderer":{"title":{"simpleText":"TV title"},"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":{"runs":[{"text":"TV channel","navigationEndpoint":{"browseEndpoint":{"browseId":"UCtest"}}}]}}}]}}]}},"onSelectCommand":{"watchEndpoint":{"videoId":"abcdefghijk"}},"header":{"tileHeaderRenderer":{"thumbnailOverlays":[{"thumbnailOverlayResumePlaybackRenderer":{"percentDurationWatched":150}},{"thumbnailOverlayTimeStatusRenderer":{"text":{"simpleText":"12:34"}}}]}}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_PLAYLIST","onSelectCommand":{"watchEndpoint":{"videoId":"playlist123"}}}},
          {"gridVideoRenderer":{"videoId":"bad/url","title":{"simpleText":"Invalid"}}},
          {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","metadata":{"tileMetadataRenderer":{"title":{"simpleText":"Unavailable tile"}}}}},
          {"gridVideoRenderer":{"title":{"simpleText":"Unavailable"}}}
        ]}}
        """;
        using var transport = new Transport(Html, Script, history);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Equal(new[] { new HistoryVideo("abcdefghijk", "TV title", "TV channel", "12:34", 100) }, page.Videos);
        Assert.Null(page.Continuation);
    }
    [Theory]
    [InlineData("{\"contents\":{\"messageRenderer\":{\"text\":{\"simpleText\":\"History is paused\"}}}}", "No history videos were returned. History may be empty or paused.")]
    [InlineData("{\"continuationContents\":{\"gridContinuation\":{\"items\":[]}}}", "No history videos were returned. History may be empty or paused.")]
    public async Task EmptyHistoryHasNonMisleadingSafeMessage(string response, string message)
    {
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Owner", null, null));
        Assert.Empty(page.Videos);
        Assert.Equal(message, page.Message);
    }
    [Theory]
    [InlineData("{}", "invalid_response")]
    [InlineData("{\"unexpected\":\"private-response\"}", "invalid_response")]
    [InlineData("{\"contents\":{},\"responseContext\":{\"mainAppWebResponseContext\":{\"loggedOut\":true}}}", "auth_required")]
    [InlineData("{\"contents\":{\"tvSignInRenderer\":{}}}", "auth_required")]
    public async Task HistoryDoesNotPresentUnknownOrLoggedOutResponsesAsEmptyHistory(string response, string expected)
    {
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Owner", null, null)));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
    }
    [Theory]
    [InlineData("<script id=\"base-js\" src=\"/s/tv/base.js\"></script>")]
    [InlineData("a.src = '/s/tv/m=base';")]
    [InlineData("a.src = '/s/tv/main.js'; a.id = 'base-js'")]
    public async Task TvBootstrapSupportsUpstreamScriptFormsAndUsesTvUserAgent(string html)
    {
        using var transport = new Transport(html, Script, Device);
        using var http = new HttpClient(transport);
        await new YouTubeClient(http).BeginSignInAsync("uuid");
        Assert.All(transport.Requests, r => Assert.Contains("Cobalt/", r.Agent));
    }
    [Theory]
    [InlineData(400, "invalid_grant", "auth_required")]
    [InlineData(400, "access_denied", "auth_required")]
    [InlineData(400, "expired_token", "auth_required")]
    [InlineData(400, "invalid_client", "bootstrap_failed")]
    [InlineData(400, "unsupported_grant_type", "request_failed")]
    [InlineData(429, "rate_limit_exceeded", "rate_limited")]
    public async Task ProviderErrorsExposeOnlyStableSafeCategories(int status, string provider, string expected)
    {
        using var transport = new Transport(Html, Script);
        transport.Responses.Enqueue(((HttpStatusCode)status, JsonSerializer.Serialize(new { error = provider, error_description = "private-response" })));
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeClient(http).RefreshSessionAsync("test-refresh"));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
    }
    [Fact]
    public async Task HistorySupportsModernLockupCardsAndShorts()
    {
        const string response = """
        {"contents":{"items":[
          {"lockupViewModel":{"contentId":"abcdefghijk","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"title":{"content":"Modern title"},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"Modern channel"}}]}]}}}},"contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailOverlayBadgeViewModel":{"thumbnailBadges":[{"thumbnailBadgeViewModel":{"text":"3:21"}}]}},{"thumbnailBottomOverlayViewModel":{"progressBar":{"thumbnailOverlayProgressBarViewModel":{"startPercent":33}}}}]}}}},
          {"shortsLockupViewModel":{"overlayMetadata":{"primaryText":{"content":"Short title"}},"onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"ABCDEFGHIJK"}}}}}
        ]}}
        """;
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("owner", null, null));
        Assert.Equal(new[] { new HistoryVideo("abcdefghijk", "Modern title", "Modern channel", "3:21", 33), new HistoryVideo("ABCDEFGHIJK", "Short title", "", "", null) }, page.Videos);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("fixture-continuation")]
    public async Task HistoryUsesUpstreamTvRefererForInitialAndContinuationRequests(string? continuation)
    {
        // BrowseApi.kt declares the same Referer for getBrowseResultTV/getContinuationResultTV.
        using var transport = new Transport(Html, Script, "{\"contents\":{}}");
        using var http = new HttpClient(transport);
        var clock = new Clock();
        await clock.Client(http).GetHistoryAsync(new("fixture-access", "fixture-refresh", clock.Now.AddHours(1)), new("Fixture Owner", null, null), continuation);
        Assert.Equal("https://www.youtube.com/tv", transport.Requests[2].Referer);
        Assert.All(transport.Requests.Take(2), request => Assert.Null(request.Auth));
    }
    [Theory]
    [InlineData("context.user.enableSafetyMode", "false")]
    [InlineData("context.user.lockedSafetyMode", "false")]
    [InlineData("context.client.browserName", "\"Cobalt\"")]
    [InlineData("context.client.browserVersion", "\"22.lts.3.306369-gold\"")]
    [InlineData("racyCheckOk", "true")]
    [InlineData("contentCheckOk", "true")]
    public async Task HistoryIncludesActiveUpstreamTvBrowseEnvelopeFields(string path, string expectedJson)
    {
        // Active path: BrowseApiHelper -> PostDataHelper -> AppClient.TV.browseTemplate.
        // These are fixed protocol flags/browser identity, not evidence of live server requirements.
        using var transport = new Transport(Html, Script, "{\"contents\":{}}", "{\"continuationContents\":{}}");
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("fixture-access", "fixture-refresh", clock.Now.AddHours(1));
        await client.GetHistoryAsync(session, new("Fixture Brand", null, "fixture-brand-page"));
        await client.GetHistoryAsync(session, new("Fixture Owner", null, null), "fixture-continuation");
        foreach (var request in transport.Requests.Skip(2))
        {
            using var document = JsonDocument.Parse(request.Body);
            var field = document.RootElement;
            foreach (var key in path.Split('.'))
            {
                Assert.True(field.TryGetProperty(key, out var child), $"Missing upstream TV browse field: {path}");
                field = child;
            }
            Assert.Equal(expectedJson, field.GetRawText());
        }
    }
    [Fact]
    public async Task HistoryUsesPinnedUpstreamTvBrowseContext()
    {
        using var transport = new Transport(Html, Script, "{\"contents\":{}}");
        using var http = new HttpClient(transport);
        var clock = new Clock();
        await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("owner", null, null));
        var body = JsonDocument.Parse(transport.Requests[2].Body).RootElement;
        var client = body.GetProperty("context").GetProperty("client");
        Assert.Equal("WATCH", client.GetProperty("clientScreen").GetString());
        Assert.Equal("TV_APP_QUALITY_FULL_ANIMATION", client.GetProperty("tvAppInfo").GetProperty("appQuality").GetString());
        Assert.True(client.GetProperty("tvAppInfo").GetProperty("zylonLeftNav").GetBoolean());
        Assert.Contains("Cobalt/", client.GetProperty("userAgent").GetString());
        Assert.Equal("visitor", client.GetProperty("visitorData").GetString());
    }
    [Fact]
    public async Task HistoryScopesTvCardsAndContinuationToActiveGrid()
    {
        const string response = """
        {"header":{"continuationCommand":{"token":"wrong-header"},"videoRenderer":{"videoId":"ABCDEFGHIJK"}},
         "contents":{"tvBrowseRenderer":{"content":{"tvSurfaceContentRenderer":{
           "header":{"continuationCommand":{"token":"wrong-filter"},"videoRenderer":{"videoId":"12345678901"}},
           "content":{"gridRenderer":{"items":[
             {"videoRenderer":{"videoId":"abcdefghijk","title":{"simpleText":"History"}}},
             {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"history-next"}}}}
           ]}}}}}}}
        """;
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("owner", null, null));
        Assert.Equal("abcdefghijk", Assert.Single(page.Videos).VideoId);
        Assert.Equal("history-next", page.Continuation);
    }
    [Theory]
    [InlineData("gridContinuation")]
    [InlineData("horizontalListContinuation")]
    [InlineData("playlistVideoListContinuation")]
    [InlineData("sectionListContinuation")]
    [InlineData("tvSurfaceContentContinuation.gridRenderer")]
    [InlineData("tvSurfaceContentContinuation.sectionListRenderer")]
    [InlineData("onResponseReceivedActions")]
    [InlineData("onResponseReceivedEndpoints")]
    public async Task HistoryContinuationScopesItemsAndTokensTogether(string shape)
    {
        const string items = """
        [{"videoRenderer":{"videoId":"abcdefghijk"}},
         {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"history-next"}}}}]
        """;
        const string noise = """
        {"videoRenderer":{"videoId":"ABCDEFGHIJK"},"continuationCommand":{"token":"wrong-filter"}}
        """;
        var list = "{\"header\":" + noise + ",\"" + (shape.Contains("sectionList") || shape == "playlistVideoListContinuation" ? "contents" : "items") + "\":" + items + "}";
        string response;
        if (shape.StartsWith("onResponse"))
            response = "{\"" + shape + "\":[{\"unrelatedAction\":" + noise + "},{\"appendContinuationItemsAction\":{\"header\":" + noise + ",\"continuationItems\":" + items + "}}]}";
        else if (shape.Contains('.'))
            response = "{\"continuationContents\":{\"tvSurfaceContentContinuation\":{\"header\":" + noise + ",\"content\":{\"" + shape.Split('.')[1] + "\":" + list + "}}}}";
        else
            response = "{\"continuationContents\":{\"" + shape + "\":" + list + "},\"header\":" + noise + "}";
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("owner", null, null), "previous");
        Assert.Equal("abcdefghijk", Assert.Single(page.Videos).VideoId);
        Assert.Equal("history-next", page.Continuation);
    }
    [Fact]
    public async Task HistoryIgnoresUnselectedTabsAndTheirContinuationTokens()
    {
        const string response = """
        {"contents":{"tabs":[
          {"tabRenderer":{"selected":false,"content":{"gridVideoRenderer":{"videoId":"ABCDEFGHIJK","title":{"simpleText":"Not history"}},"continuations":[{"nextContinuationData":{"continuation":"wrong"}}]}}},
          {"tabRenderer":{"selected":true,"content":{"gridVideoRenderer":{"videoId":"abcdefghijk","title":{"simpleText":"History"}},"continuations":[{"nextContinuationData":{"continuation":"correct"}}]}}}
        ]}}
        """;
        using var transport = new Transport(Html, Script, response);
        using var http = new HttpClient(transport);
        var clock = new Clock();
        var page = await clock.Client(http).GetHistoryAsync(new("access", "refresh", clock.Now.AddHours(1)), new("owner", null, null));
        Assert.Equal("abcdefghijk", Assert.Single(page.Videos).VideoId);
        Assert.Equal("correct", page.Continuation);
    }
}
