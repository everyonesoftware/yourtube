using System.Net;
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
    [InlineData(null)]
    [InlineData("brand-page")]
    public async Task ProgressReusesAcknowledgedStartAndSendsOnlyTheWatchedRange(string? page)
    {
        using var transport = ProgressTransport("", "");
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Identity", null, page);
        var record = await client.RecordWatchStartAsync(Session(clock), account, Video, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(.25));
        Assert.Equal(6, transport.Requests.Count);
        Assert.Equal("YourTubeClient.WatchRecord [redacted]", record.ToString());

        var refreshed = new YouTubeSession("rotated-access", "rotated-refresh", clock.Now.AddHours(2));
        await client.RecordWatchProgressAsync(refreshed, account, record, TimeSpan.FromSeconds(.25), TimeSpan.FromSeconds(10.125));
        await client.RecordWatchProgressAsync(refreshed, account, record, TimeSpan.FromSeconds(175), TimeSpan.FromSeconds(180));

        Assert.Equal(8, transport.Requests.Count);
        using var body = JsonDocument.Parse(transport.Requests[4].Body);
        var nonce = body.RootElement.GetProperty("cpn").GetString();
        Assert.Contains($"&cpn={nonce}&", transport.Requests[5].Url);
        const string prefix = "https://www.youtube.com/api/stats/watchtime?ns=yt&ver=2&docid=abcdefghijk&len=180";
        var suffix = $"&cpn={nonce}&ei=one%2Btwo&vm=monitor%26inert%3Dvalue&of=opaque";
        Assert.Equal(prefix + "&st=0.25&et=10.125&cmt=10.125" + suffix, transport.Requests[6].Url);
        Assert.Equal(prefix + "&st=175&et=180&cmt=180" + suffix, transport.Requests[7].Url);
        foreach (var request in transport.Requests.Skip(6))
        {
            Assert.Equal("GET", request.Method);
            Assert.Empty(request.Body);
            Assert.Equal("Bearer rotated-access", request.Auth);
            Assert.Equal(page, request.Page);
            Assert.Equal("https://www.youtube.com/tv", request.Referer);
            Assert.Equal(transport.Requests[5].Agent, request.Agent);
        }
        Assert.All(transport.RequestHeaders.Skip(6), headers => Assert.False(headers.ContainsKey("Cookie")));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1000, 0)]
    [InlineData(0, 0)]
    [InlineData(0, .1)] // Positive TimeSpan range, but empty at the wire precision.
    [InlineData(179999, 180000.1)] // Reject out-of-bounds input even when it rounds to duration.
    public async Task InvalidWatchRangesDoNotDispatch(double startMilliseconds, double endMilliseconds)
    {
        using var transport = ProgressTransport();
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Owner", null, null);
        var session = Session(clock);
        var record = await client.RecordWatchStartAsync(session, account, Video, TimeSpan.FromSeconds(180), TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<YouTubeException>(() => client.RecordWatchProgressAsync(session, account, record,
            TimeSpan.FromMilliseconds(startMilliseconds), TimeSpan.FromMilliseconds(endMilliseconds)));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(6, transport.Requests.Count);
    }

    [Theory]
    [InlineData("same_page")]
    [InlineData("different_page")]
    [InlineData("different_client")]
    [InlineData("missing_record")]
    [InlineData("expired_session")]
    public async Task ProgressRequiresTheOriginalClientAndAccountAndAValidSession(string scenario)
    {
        using var transport = ProgressTransport();
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Owner", null, null);
        var session = Session(clock);
        var record = await client.RecordWatchStartAsync(session, account, Video, TimeSpan.FromSeconds(180), TimeSpan.Zero);
        var sender = scenario == "different_client" ? clock.Client(http) : client;
        var selected = scenario switch
        {
            "same_page" => account with { },
            "different_page" => account with { PageId = "brand-page" },
            _ => account
        };
        if (scenario == "expired_session") session = new("expired", "refresh", clock.Now.AddSeconds(-1));

        var error = await Assert.ThrowsAsync<YouTubeException>(() => sender.RecordWatchProgressAsync(session, selected,
            scenario == "missing_record" ? null! : record, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Equal(scenario == "expired_session" ? "auth_required" : "invalid_argument", error.Code);
        Assert.Equal(6, transport.Requests.Count);
    }

    [Theory]
    [InlineData(false, "watch_progress_rejected")]
    [InlineData(true, "watch_progress_unconfirmed")]
    public async Task FailedProgressIsSanitizedAndNeverRetried(bool networkFailure, string expectedCode)
    {
        const string marker = "PRIVATE_MARKER";
        using var transport = ProgressTransport();
        transport.Responses.Enqueue((HttpStatusCode.Redirect, marker));
        using var gateway = new TrackingGateway(transport);
        using var http = new HttpClient(gateway);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Brand", null, "brand-page");
        var session = Session(clock);
        var record = await client.RecordWatchStartAsync(session, account, Video, TimeSpan.FromSeconds(180), TimeSpan.Zero);
        gateway.BeforeReturningResponse = response =>
        {
            if (networkFailure) throw new HttpRequestException(marker);
            response.Headers.Location = new Uri("https://example.invalid/" + marker);
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<YouTubeException>(() => client.RecordWatchProgressAsync(session, account,
            record, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Equal(expectedCode, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(marker, error.ToString());
        Assert.Equal(7, transport.Requests.Count);
    }

    [Fact]
    public async Task CanceledProgressDoesNotDispatch()
    {
        using var transport = ProgressTransport();
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Owner", null, null);
        var session = Session(clock);
        var record = await client.RecordWatchStartAsync(session, account, Video, TimeSpan.FromSeconds(180), TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RecordWatchProgressAsync(session, account,
            record, TimeSpan.Zero, TimeSpan.FromSeconds(1), new CancellationToken(canceled: true)));
        Assert.Equal(6, transport.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledWriteCannotReturnALateAcknowledgement(bool progress)
    {
        using var transport = ProgressTransport("");
        using var gateway = new TrackingGateway(transport);
        using var http = new HttpClient(gateway);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var account = new YouTubeAccount("Owner", null, null);
        var session = Session(clock);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = TimeSpan.FromSeconds(180);
        var record = progress ? await client.RecordWatchStartAsync(session, account, Video, duration, TimeSpan.Zero) : null;
        gateway.BeforeReturningResponse = _ => { entered.TrySetResult(); return released.Task; };
        var write = progress
            ? client.RecordWatchProgressAsync(session, account, record!, TimeSpan.Zero, TimeSpan.FromSeconds(1), cancellation.Token)
            : client.RecordWatchStartAsync(session, account, Video, duration, TimeSpan.Zero, cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        finally { released.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(progress ? 7 : 6, transport.Requests.Count);
    }

    private sealed class TrackingGateway(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public Func<HttpResponseMessage, Task>? BeforeReturningResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            try
            {
                if (BeforeReturningResponse is { } intercept && request.RequestUri!.AbsolutePath.StartsWith("/api/stats/", StringComparison.Ordinal))
                    await intercept(response);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static YouTubeSession Session(ClientTests.Clock clock) => new("access", "refresh", clock.Now.AddHours(1));

    private static ClientTests.Transport ProgressTransport(params string[] responses) => new(
        [ClientTests.Html, ClientTests.Script, WatchPage, PlayerScript,
            Player("https://s.youtube.com/api/stats/watchtime?ei=one%2Btwo&vm=monitor%26inert%3Dvalue&of=opaque&final=1"), "", .. responses]);

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
