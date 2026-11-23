using System.Text.Json;
using YourTube.Core;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class WatchRecordTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("brand-page")]
    public async Task ComparisonUsesPinnedTvFallbackWithSameIdentityAndNeverSendsTrackingRequests(string? page)
    {
        const string denied = "{\"playabilityStatus\":{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"private-marker\"}}";
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, denied,
            Player("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque"));
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var result = await clock.Client(http).CompareWatchLookupsAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, page), Video);
        Assert.Equal("LOGIN_REQUIRED", result.Baseline.Status);
        Assert.False(result.Baseline.TrackingUsable);
        Assert.Equal("OK", result.Candidate.Status);
        Assert.True(result.Candidate.TrackingUsable);
        Assert.Equal(4, transport.Requests.Count);
        const string fallbackAgent = "Mozilla/5.0 (DirectFB; Linux x86_64) Cobalt/4.13031-qa (unlike Gecko) Starboard/1";
        using var baselineBody = JsonDocument.Parse(transport.Requests[2].Body);
        var nonce = baselineBody.RootElement.GetProperty("cpn").GetString();
        Assert.Matches("^[A-Za-z0-9_-]{16}$", nonce!);
        for (int i = 2; i <= 3; i++)
        {
            var request = transport.Requests[i];
            Assert.Equal("https://www.youtube.com/youtubei/v1/player", request.Url);
            Assert.Equal("Bearer access", request.Auth);
            Assert.Equal(page, request.Page);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(Video, body.RootElement.GetProperty("videoId").GetString());
            var requestNonce = body.RootElement.GetProperty("cpn").GetString();
            Assert.Matches("^[A-Za-z0-9_-]{16}$", requestNonce!);
            if (i == 3) Assert.NotEqual(nonce, requestNonce);
            var client = body.RootElement.GetProperty("context").GetProperty("client");
            Assert.Equal("TVHTML5", client.GetProperty("clientName").GetString());
            Assert.Equal(i == 2 ? "7.test" : "5.20260901", client.GetProperty("clientVersion").GetString());
            if (i == 3)
            {
                Assert.Equal(fallbackAgent, client.GetProperty("userAgent").GetString());
                Assert.Equal(fallbackAgent, request.Agent);
                Assert.Equal("https://www.youtube.com/tv", request.Referer);
                Assert.False(client.TryGetProperty("tvAppInfo", out _));
                Assert.False(client.TryGetProperty("browserVersion", out _));
                Assert.False(body.RootElement.TryGetProperty("playbackContext", out _));
            }
            var user = body.RootElement.GetProperty("context").GetProperty("user");
            Assert.Equal(page, user.TryGetProperty("onBehalfOfUser", out var identity) ? identity.GetString() : null);
        }
        Assert.Null(transport.RequestHeaders[2].GetValueOrDefault("X-Youtube-Client-Name"));
        Assert.Equal("7", transport.RequestHeaders[3]["X-Youtube-Client-Name"]);
        Assert.Equal("5.20260901", transport.RequestHeaders[3]["X-Youtube-Client-Version"]);
        Assert.Equal("visitor", transport.RequestHeaders[3]["X-Goog-Visitor-Id"]);
        Assert.DoesNotContain("private-marker", result.ToString());
    }

    private const string Video = "abcdefghijk";
    private const string ScriptPath = "/s/player/abc123/player_ias.vflset/en_US/base.js";
    private const string WatchPage = "{\"jsUrl\":\"" + ScriptPath + "\"}";
    private const string PlayerScript = "...signatureTimestamp:24321,...";
    private static string Player(string url, string id = Video) => JsonSerializer.Serialize(new
    {
        playabilityStatus = new { status = "OK" }, videoDetails = new { videoId = id },
        playbackTracking = new { videostatsWatchtimeUrl = new { baseUrl = url } }
    });

    [Theory]
    [InlineData(null)]
    [InlineData("brand-page")]
    public async Task RecordsOneStartWithParametersFromTheSelectedIdentity(string? page)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script,
            WatchPage, PlayerScript, Player("https://s.youtube.com/api/stats/watchtime?ei=one%2Btwo&vm=monitor&of=opaque&ignore=other"), "");
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        await clock.Client(http).RecordWatchStartAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, page), Video, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(.25));
        Assert.Equal(6, transport.Requests.Count);
        Assert.Equal($"https://www.youtube.com/watch?v={Video}", transport.Requests[2].Url);
        Assert.Equal("https://www.youtube.com" + ScriptPath, transport.Requests[3].Url);
        foreach (var headers in transport.RequestHeaders.Skip(2).Take(2))
        {
            Assert.False(headers.ContainsKey("Authorization"));
            Assert.False(headers.ContainsKey("Cookie"));
            Assert.False(headers.ContainsKey("X-Goog-PageId"));
        }
        var player = transport.Requests[4];
        var record = transport.Requests[5];
        Assert.Equal("https://www.youtube.com/youtubei/v1/player", player.Url);
        Assert.Equal("5.20260901", transport.RequestHeaders[4]["X-Youtube-Client-Version"]);
        var body = JsonDocument.Parse(player.Body).RootElement;
        Assert.Equal(Video, body.GetProperty("videoId").GetString());
        Assert.Equal("5.20260901", body.GetProperty("context").GetProperty("client").GetProperty("clientVersion").GetString());
        Assert.Equal(24321, body.GetProperty("playbackContext").GetProperty("contentPlaybackContext").GetProperty("signatureTimestamp").GetInt32());
        var user = body.GetProperty("context").GetProperty("user");
        Assert.Equal(page, user.TryGetProperty("onBehalfOfUser", out var identity) ? identity.GetString() : null);
        Assert.Equal("Bearer access", player.Auth);
        Assert.Equal("Bearer access", record.Auth);
        Assert.Equal(page, player.Page);
        Assert.Equal(page, record.Page);
        Assert.Matches(@"^https://www\.youtube\.com/api/stats/playback\?ns=yt&ver=2&docid=abcdefghijk&len=180&cmt=0\.25&cpn=[A-Za-z0-9_-]{16}&ei=one%2Btwo&vm=monitor&of=opaque$", record.Url);
        Assert.Contains($"&cpn={body.GetProperty("cpn").GetString()}&", record.Url);
    }

    [Theory]
    [InlineData("no timestamp")]
    [InlineData("signatureTimestamp:24321,signatureTimestamp:24322,")]
    [InlineData("signatureTimestamp:24321, signatureTimestamp: 24322,")]
    [InlineData("signatureTimestamp:24321.5,")]
    [InlineData("signatureTimestamp:24321e2,")]
    [InlineData("signatureTimestamp:24321, signatureTimestamp: 24322.5,")]
    [InlineData("[{signatureTimestamp:24321},{\"signatureTimestamp\":24322}]")]
    [InlineData("[{signatureTimestamp:24321},{'signatureTimestamp':24322}]")]
    [InlineData("[{signatureTimestamp:24321},{'signatureTimestamp':24322.5}]")]
    public async Task MissingOrAmbiguousPlaybackContextNeverMakesAnAuthenticatedLookupOrWrite(string script)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, WatchPage, script);
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).RecordWatchStartAsync(
            new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, null), Video, TimeSpan.FromSeconds(180), TimeSpan.Zero));
        Assert.Equal("watch_context_unavailable", error.Code);
        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Null(request.Auth));
    }

    [Theory]
    [InlineData("https://evil.example/api/stats/watchtime?ei=event&vm=monitor&of=opaque", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque&of=second", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque", "zzzzzzzzzzz")]
    public async Task MalformedOrMismatchedTrackingDataNeverWritesHistory(string url, string id)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, WatchPage, PlayerScript, Player(url, id));
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).RecordWatchStartAsync(
            new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, null), Video, TimeSpan.FromSeconds(180), TimeSpan.Zero));
        Assert.Equal("watch_tracking_unusable", error.Code);
        Assert.Equal(5, transport.Requests.Count);
    }

    [Theory]
    [InlineData("discovery", "watch_discovery_failed", 2)]
    [InlineData("lookup", "watch_lookup_failed", 5)]
    [InlineData("not_playable", "watch_lookup_not_playable", 5)]
    [InlineData("write", "watch_record_rejected", 6)]
    public async Task DistinguishesRequestStagesWithoutExposingResponse(string scenario, string code, int count)
    {
        const string marker = "PRIVATE_MARKER";
        using var transport = new ClientTests.Transport(ClientTests.Html, scenario == "discovery" ? marker : ClientTests.Script, WatchPage, PlayerScript);
        transport.Responses.Enqueue(scenario switch
        {
            "lookup" => (System.Net.HttpStatusCode.Forbidden, marker),
            "not_playable" => (System.Net.HttpStatusCode.OK, "{\"playabilityStatus\":{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"PRIVATE_MARKER\"}}"),
            _ => (System.Net.HttpStatusCode.OK, Player("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque"))
        });
        transport.Responses.Enqueue((System.Net.HttpStatusCode.BadRequest, marker));
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).RecordWatchStartAsync(
            new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, null), Video, TimeSpan.FromSeconds(180), TimeSpan.Zero));
        Assert.Equal(code, error.Code);
        Assert.Equal(count, transport.Requests.Count);
        Assert.DoesNotContain(marker, error.ToString());
        Assert.Null(error.InnerException);
    }
}
