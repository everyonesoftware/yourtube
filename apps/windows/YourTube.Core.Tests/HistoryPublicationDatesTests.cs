using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;
using YourTube.App;

namespace YourTube.Core.Tests;

public sealed class HistoryPublicationDatesTests
{
    private const string Id = "jNQXAC9IVRw";
    private const string Canonical = $"<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v={Id}\">";
    private const string Published = "<meta itemprop=\"datePublished\" content=\"2026-11-24T20:31:52-07:00\">";

    [Theory]
    [InlineData(2026, 11, 24, "1 day ago")]
    [InlineData(2026, 11, 23, "2 days ago")]
    [InlineData(2026, 11, 18, "1 week ago")]
    [InlineData(2026, 10, 25, "1 month ago")]
    [InlineData(2005, 4, 23, "21 years ago")]
    [InlineData(2026, 11, 25, "Today")]
    [InlineData(2026, 11, 26, null)]
    public void FormatsPublicationAgeFromTheCalendarDate(int year, int month, int day, string? expected) =>
        Assert.Equal(expected, HistoryPublicationDates.FormatAge(new(year, month, day), new(2026, 11, 25)));

    [Fact]
    public async Task LookupReadsARealPublicWatchPageWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("YOURTUBE_PUBLIC_PUBLICATION_PROOF") != "1") return;
        using var dates = new HistoryPublicationDates();
        Assert.Equal(new DateOnly(2005, 4, 23), await dates.LoadAsync(Id, default));
    }

    [Fact]
    public async Task ReadsPublicationDateForTheRequestedVideoAndReusesIt()
    {
        var transport = new Transport($"<head>{Canonical}{Published}</head><body></body>");
        using var dates = new HistoryPublicationDates(transport);
        Assert.Equal(new DateOnly(2026, 11, 24), await dates.LoadAsync(Id, default));
        Assert.Equal(new DateOnly(2026, 11, 24), await dates.LoadAsync(Id, default));
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("<head>" + Canonical + "</head>" + Published)]
    [InlineData("<head>" + Canonical + "<script>const decoy = '" + Published + "';</script></head>")]
    [InlineData("<head>" + Canonical + "<!--" + Published + "--></head>")]
    [InlineData("<head>" + Canonical + Published + Published + "</head>")]
    [InlineData("<head>" + Canonical + "<meta itemprop=\"datePublished\" content=\"2026-02-30\"></head>")]
    [InlineData("<head>" + Canonical + "<meta itemprop=\"uploadDate\" content=\"2026-11-24\"></head>")]
    [InlineData("<head><link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=abcdefghijk\">" + Published + "</head>")]
    public async Task MissingAmbiguousOrUnrelatedPublicationDateStaysUnknown(string html)
    {
        using var dates = new HistoryPublicationDates(new Transport(html));
        Assert.Null(await dates.LoadAsync(Id, default));
    }

    [Fact]
    public async Task InvalidVideoIdNeverReachesTransport()
    {
        var transport = new Transport("");
        using var dates = new HistoryPublicationDates(transport);
        Assert.Null(await dates.LoadAsync("abcdefgh&x=", default));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task RedirectDoesNotSupplyAPublicationDate()
    {
        using var dates = new HistoryPublicationDates(new Transport($"<head>{Canonical}{Published}</head>", HttpStatusCode.Redirect));
        Assert.Null(await dates.LoadAsync(Id, default));
    }

    [Fact]
    public async Task OversizedResponseStaysUnknown()
    {
        using var dates = new HistoryPublicationDates(new Transport("<head>" + new string('x', 4 * 1024 * 1024) + Canonical + Published + "</head>"));
        Assert.Null(await dates.LoadAsync(Id, default));
    }

    [Fact]
    public async Task CancelledQueuedCardDoesNotDispatchOrConsumeAnotherSlot()
    {
        var transport = new HeldTransport();
        using var dates = new HistoryPublicationDates(transport);
        using var queued = new CancellationTokenSource();
        var first = dates.LoadAsync(Id, default);
        var second = dates.LoadAsync("abcdefghijk", default);
        var recycled = dates.LoadAsync("ABCDEFGHIJK", queued.Token);
        queued.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recycled);
            Assert.Equal(2, transport.Calls);
        }
        finally { transport.Release.SetResult(); }
        await Task.WhenAll(first, second);
        await dates.LoadAsync("01234567890", default);
        Assert.Equal(3, transport.Calls);
    }

    private sealed class HeldTransport : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task InvalidCompressedBodyStaysUnknown()
    {
        using var dates = new HistoryPublicationDates(new InvalidGzipTransport());
        Assert.Null(await dates.LoadAsync(Id, default));
    }

    private sealed class InvalidGzipTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = new StreamContent(new GZipStream(new MemoryStream("not gzip"u8.ToArray()), CompressionMode.Decompress));
            body.Headers.ContentType = new("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }

    private sealed class Transport(string html, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://www.youtube.com/watch?v={Id}&hl=en", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Referrer);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }
}
