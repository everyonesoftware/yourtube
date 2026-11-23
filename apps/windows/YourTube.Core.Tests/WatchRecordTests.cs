using System.Text.Json;
using YourTube.Core;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class WatchRecordTests
{
    private const string Video = "abcdefghijk";
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
            Player("https://s.youtube.com/api/stats/watchtime?ei=one%2Btwo&vm=monitor&of=opaque&ignore=other"), "");
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        await clock.Client(http).RecordWatchStartAsync(new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, page), Video, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(.25));
        Assert.Equal(4, transport.Requests.Count);
        var player = transport.Requests[2];
        var record = transport.Requests[3];
        Assert.Equal("https://www.youtube.com/youtubei/v1/player", player.Url);
        var body = JsonDocument.Parse(player.Body).RootElement;
        Assert.Equal(Video, body.GetProperty("videoId").GetString());
        var user = body.GetProperty("context").GetProperty("user");
        Assert.Equal(page, user.TryGetProperty("onBehalfOfUser", out var identity) ? identity.GetString() : null);
        Assert.Equal("Bearer access", player.Auth);
        Assert.Equal("Bearer access", record.Auth);
        Assert.Equal(page, player.Page);
        Assert.Equal(page, record.Page);
        Assert.Matches(@"^https://www\.youtube\.com/api/stats/playback\?ns=yt&ver=2&docid=abcdefghijk&len=180&cmt=0\.25&cpn=[A-Za-z0-9_-]{16}&ei=one%2Btwo&vm=monitor&of=opaque$", record.Url);
    }

    [Theory]
    [InlineData("https://evil.example/api/stats/watchtime?ei=event&vm=monitor&of=opaque", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque&of=second", Video)]
    [InlineData("https://s.youtube.com/api/stats/watchtime?ei=event&vm=monitor&of=opaque", "zzzzzzzzzzz")]
    public async Task MalformedOrMismatchedTrackingDataNeverWritesHistory(string url, string id)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, Player(url, id));
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).RecordWatchStartAsync(
            new("access", "refresh", clock.Now.AddHours(1)), new("Identity", null, null), Video, TimeSpan.FromSeconds(180), TimeSpan.Zero));
        Assert.Equal("watch_tracking_unusable", error.Code);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Theory]
    [InlineData("discovery", "watch_discovery_failed", 2)]
    [InlineData("lookup", "watch_lookup_failed", 3)]
    [InlineData("not_playable", "watch_lookup_not_playable", 3)]
    [InlineData("write", "watch_record_rejected", 4)]
    public async Task DistinguishesRequestStagesWithoutExposingResponse(string scenario, string code, int count)
    {
        const string marker = "PRIVATE_MARKER";
        using var transport = new ClientTests.Transport(ClientTests.Html, scenario == "discovery" ? marker : ClientTests.Script);
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
