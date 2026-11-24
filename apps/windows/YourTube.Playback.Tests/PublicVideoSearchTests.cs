using System.Net;
using System.Text.Json;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class PublicVideoSearchTests
{
    [Fact]
    public async Task SearchesTheFirstVideoBatchInProviderOrderWithoutContinuing()
    {
        using var handler = new Fixture(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://www.youtube.com/youtubei/v1/search", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("orbit + café \"test\"", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("EgIQAQ%3D%3D", body.RootElement.GetProperty("params").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("continuation").ValueKind);
            return Ok(Page(Video("bbbbbbbbbb2", "Second alphabetically", "1:02:03"),
                Video("aaaaaaaaaa1", "First alphabetically"),
                Video("bbbbbbbbbb2", "Duplicate")));
        });
        using var http = PublicVideoSearch.CreateClient(handler);
        var results = await new PublicVideoSearch(http).SearchAsync("  orbit + café \"test\"  ");
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new[]
        {
            new PublicVideoSearchResult("bbbbbbbbbb2", "Second alphabetically", "Fixture channel", "1:02:03"),
            new PublicVideoSearchResult("aaaaaaaaaa1", "First alphabetically", "Fixture channel", ""),
        }, results);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aaaaaaaaaa1")]
    [InlineData("ébbbbbbbbb2")]
    public async Task DoesNotNormalizeNoncanonicalProviderVideoIds(string id)
    {
        using var handler = new Fixture((_, _) => Task.FromResult(Ok(Page(Video(id, "Noncanonical")))));
        using var http = PublicVideoSearch.CreateClient(handler);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal("search_invalid_response", error.Code);
    }

    [Fact]
    public async Task LimitsResultsWithoutReordering()
    {
        using var handler = new Fixture((_, _) => Task.FromResult(Ok(Page(Enumerable.Range(0, 51)
            .Select(i => Video($"aaaaaaaaa{i:D2}", "Fixture title", "3:05")).ToArray()))));
        using var http = PublicVideoSearch.CreateClient(handler);
        var results = await new PublicVideoSearch(http).SearchAsync("test");
        Assert.Equal(50, results.Count);
        Assert.Equal("aaaaaaaaa49", results[^1].VideoId);
        Assert.Equal("3:05", results[^1].Duration);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RejectsInvalidQueriesWithoutSending()
    {
        using var handler = new Fixture((_, _) => Task.FromResult(Ok(Page())));
        using var http = PublicVideoSearch.CreateClient(handler);
        var provider = new PublicVideoSearch(http);
        foreach (var query in new[] { " \t ", new string('a', 257) })
            Assert.Equal("search_invalid_query", (await Assert.ThrowsAsync<PlaybackException>(() => provider.SearchAsync(query))).Code);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RecognizedEmptyPageDoesNotFollowContinuation()
    {
        using var handler = new Fixture((_, _) => Task.FromResult(Ok(Page("""{"backgroundPromoRenderer":{}}"""))));
        using var http = PublicVideoSearch.CreateClient(handler);
        Assert.Empty(await new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    public async Task MalformedItemSectionIsNotAnEmptyResult(string contents)
    {
        using var handler = new Fixture((_, _) => Task.FromResult(Ok(Page().Replace("\"contents\":[]", $"\"contents\":{contents}", StringComparison.Ordinal))));
        using var http = PublicVideoSearch.CreateClient(handler);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal("search_invalid_response", error.Code);
    }

    [Theory]
    [InlineData(200, "{private-provider-marker", "search_invalid_response")]
    [InlineData(200, "{\"error\":{\"message\":\"private-provider-marker\"}}", "search_invalid_response")]
    [InlineData(302, "private-provider-marker", "search_unavailable")]
    [InlineData(429, "private-provider-marker", "search_unavailable")]
    [InlineData(503, "private-provider-marker", "search_unavailable")]
    public async Task ResponseFailuresAreSafeAndNeverRetried(int status, string body, string code)
    {
        using var handler = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Headers = { Location = new Uri("https://example.invalid/private-provider-marker") },
            Content = new StringContent(body),
        }));
        using var http = PublicVideoSearch.CreateClient(handler);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private-provider-marker", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RejectsOversizedBodyWithoutContentLength()
    {
        using var handler = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableBody(new byte[4 * 1024 * 1024 + 1])),
        }));
        using var http = PublicVideoSearch.CreateClient(handler);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal("search_invalid_response", error.Code);
    }

    [Fact]
    public async Task PreservesCallerCancellationWhileWaitingForAResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Fixture(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok(Page());
        });
        using var http = PublicVideoSearch.CreateClient(handler);
        using var canceled = new CancellationTokenSource();
        var pending = new PublicVideoSearch(http).SearchAsync("test", canceled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TransportTimeoutIsNotCallerCancellation()
    {
        using var handler = new Fixture((_, _) => throw new TaskCanceledException("private-provider-marker"));
        using var http = PublicVideoSearch.CreateClient(handler);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => new PublicVideoSearch(http).SearchAsync("test"));
        Assert.Equal("search_timeout", error.Code);
        Assert.Null(error.InnerException);
    }

    private sealed class UnseekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static string Video(string id, string title, string? duration = null) =>
        JsonSerializer.Serialize(new { videoRenderer = new
        {
            videoId = id,
            title = new { simpleText = title },
            longBylineText = new { runs = new[] { new { text = "Fixture channel", navigationEndpoint = new { browseEndpoint = new { browseId = "UCabcdefghijklmnopqrstuv" } } } } },
            lengthText = duration is null ? null : new { simpleText = duration },
        } });

    private static string Page(params string[] items) => """
        {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":{"sectionListRenderer":{"contents":[
        {"itemSectionRenderer":{"contents":[ITEMS]}},
        {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"fixture-cursor"}}}}
        ]}}}}}
        """.Replace("ITEMS", string.Join(',', items), StringComparison.Ordinal);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return send(request, cancellationToken);
        }
    }
}
