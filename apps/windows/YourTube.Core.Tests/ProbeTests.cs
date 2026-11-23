using YourTube.Probe;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class ProbeTests
{
    [Fact]
    public async Task ContextProbeAddsOnlyTheDiscoveredPlaybackContextToTheTvControl()
    {
        const string player = "{\"playabilityStatus\":{\"status\":\"UNPLAYABLE\"}}";
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script,
            ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), ClientTests.Token,
            "{\"contents\":[{\"accountItem\":{\"accountName\":{\"simpleText\":\"Test identity\"}}}]}",
            "{\"jsUrl\":\"/s/player/abc123/player_ias.vflset/en_US/base.js\"}", "...signatureTimestamp:24321,...", player, player, player);
        using var output = new StringWriter();
        var result = await ProbeRunner.RunAsync(transport, output, true, _ => { }, CancellationToken.None, ["abcdefghijk"], includePlaybackContext: true);
        Assert.Equal(0, result);
        Assert.Equal(10, transport.Requests.Count);
        Assert.All(transport.Requests.Skip(5).Take(2), request => Assert.Null(request.Auth));
        Assert.All(transport.Requests.Skip(7), request =>
        {
            Assert.Equal("https://www.youtube.com/youtubei/v1/player", request.Url);
            Assert.Equal("Bearer test-access", request.Auth);
        });
        var control = System.Text.Json.Nodes.JsonNode.Parse(transport.Requests[8].Body)!.AsObject();
        var candidate = System.Text.Json.Nodes.JsonNode.Parse(transport.Requests[9].Body)!.AsObject();
        Assert.Equal(24321, candidate["playbackContext"]!["contentPlaybackContext"]!["signatureTimestamp"]!.GetValue<int>());
        Assert.False(control.ContainsKey("playbackContext"));
        Assert.NotEqual(control["cpn"]!.ToString(), candidate["cpn"]!.ToString());
        candidate.Remove("playbackContext");
        control.Remove("cpn");
        candidate.Remove("cpn");
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(control, candidate));
        Assert.Contains("profile=tv_with_context status=UNPLAYABLE", output.ToString());
        Assert.DoesNotContain("24321", output.ToString());
    }

    [Theory]
    [InlineData("{\"jsUrl\":\"https://example.com/s/player/test/player_ias.vflset/en_US/base.js\"}")]
    [InlineData("{\"jsUrl\":\"/s/player/../player_ias.vflset/en_US/base.js\"}")]
    public async Task ContextDiscoveryRejectsUnexpectedScriptPaths(string html)
    {
        using var transport = new ClientTests.Transport(html);
        using var http = new HttpClient(transport);
        Assert.Null(await PlayerContextDiscovery.DiscoverAsync(http, "abcdefghijk", CancellationToken.None));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(false, "abcdefghijk")]
    [InlineData(true, "not-a-video-id")]
    public async Task InvalidLookupBatchDoesNotIssueAnActivationCode(bool signIn, string id)
    {
        using var transport = new ClientTests.Transport();
        using var output = new StringWriter();
        Assert.Equal(2, await ProbeRunner.RunAsync(transport, output, signIn, _ => throw new InvalidOperationException(), CancellationToken.None, [id]));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task LookupBatchAuthorizesOnceAndNeverReadsOrWritesHistory()
    {
        const string blocked = "{\"playabilityStatus\":{\"status\":\"UNPLAYABLE\",\"reason\":\"private-reason\"},\"playbackTracking\":{\"videostatsPlaybackUrl\":{\"baseUrl\":\"private-tracking\"},\"videostatsWatchtimeUrl\":{\"baseUrl\":\"\"}}}";
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script,
            ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), ClientTests.Token,
            "{\"contents\":[{\"accountItem\":{\"accountName\":{\"simpleText\":\"Private name\"}}}]}", blocked, blocked, blocked, blocked);
        using var output = new StringWriter();
        var activations = 0;
        var result = await ProbeRunner.RunAsync(transport, output, true, _ => activations++, CancellationToken.None, ["abcdefghijk", "zzzzzzzzzzz"]);
        Assert.Equal(0, result);
        Assert.Equal(1, activations);
        Assert.Equal(9, transport.Requests.Count);
        Assert.All(transport.Requests.Skip(5), request => Assert.Equal("https://www.youtube.com/youtubei/v1/player", request.Url));
        Assert.Contains("case=2 profile=tv_fallback status=UNPLAYABLE tracking_usable=no playback_start_field=present watch_time_field=absent", output.ToString());
        foreach (var secret in new[] { "Private name", "private-reason", "private-tracking", "abcdefghijk", "zzzzzzzzzzz", "test-access", "test-refresh", "TEST-CODE" })
            Assert.DoesNotContain(secret, output.ToString());
    }

    [Fact]
    public async Task DefaultProbeIssuesAndObservesPendingWithoutSecretOutputOrActivation()
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), "{\"error\":\"authorization_pending\"}");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var activated = false;
        var result = await ProbeRunner.RunAsync(transport, output, false, _ => activated = true, timeout.Token);
        Assert.Equal(0, result);
        Assert.False(activated);
        Assert.Equal(4, transport.Requests.Count);
        Assert.Contains("device_authorization=issued", output.ToString());
        Assert.Contains("token_poll=pending", output.ToString());
        foreach (var secret in new[] { "TEST-CODE", "private-device", "first.apps", "test_secret", "test-access", "test-refresh" }) Assert.DoesNotContain(secret, output.ToString());
    }
    [Fact]
    public async Task ProbeReportsSafeFailuresWithNonzeroExit()
    {
        using var transport = new ClientTests.Transport("private-response");
        using var output = new StringWriter();
        var result = await ProbeRunner.RunAsync(transport, output, false, null, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.Contains("probe_error=bootstrap_failed", output.ToString());
        Assert.DoesNotContain("private-response", output.ToString());
    }
    [Fact]
    public async Task OptionalSignInActivatesLocallyThenCountsRealAccountHistoryPagesWithoutPrintingThem()
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), ClientTests.Token,
            "{\"contents\":[{\"accountItem\":{\"accountName\":{\"simpleText\":\"Private name\"}}}]}",
            "{\"contents\":{\"items\":[{\"gridVideoRenderer\":{\"videoId\":\"abcdefghijk\",\"title\":{\"simpleText\":\"Private title\"}}}],\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"next\"}}]}}", "{\"continuationContents\":{\"gridContinuation\":{\"items\":[]}}}");
        using var output = new StringWriter();
        var activated = false;
        var result = await ProbeRunner.RunAsync(transport, output, true, _ => activated = true, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.True(activated);
        Assert.Contains("accounts=1", output.ToString());
        Assert.Contains("history_pages=2 videos=1", output.ToString());
        foreach (var secret in new[] { "Private", "abcdefghijk", "test-access", "test-refresh", "TEST-CODE", "private-device" }) Assert.DoesNotContain(secret, output.ToString());
    }
    [Fact]
    public async Task CancelledProbeReturnsSafeNonzeroStatus()
    {
        using var transport = new ClientTests.Transport();
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(130, await ProbeRunner.RunAsync(transport, output, false, null, cancellation.Token));
        Assert.Contains("probe_cancelled=true", output.ToString());
        Assert.Empty(transport.Requests);
    }
}
