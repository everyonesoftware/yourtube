using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class ReachabilityConcurrencyTests
{
    private const string VideoId = "jNQXAC9IVRw";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PinnedLibraryOverlapsOnlyTheConfiguredWindowAndKeepsTheFullOrderedCatalog(int limit)
    {
        using var fixture = new ProbeFixture();
        using var resolver = new PlaybackResolver(fixture) { MaxConcurrentStreamChecks = limit };
        var timing = PlaybackTiming.StartForStartup();
        var resolve = resolver.ResolveAsync(VideoId, CancellationToken.None, timing);
        try
        {
            // Hold the first candidate while the other admitted checks finish out of order.
            await fixture.Entered[limit - 1].Task.WaitAsync(Deadline);
            Assert.Equal(limit, fixture.Calls.Count);
            Assert.Equal(limit, timing.ReadStartup()!.Discovery.ReachabilityDiagnostics?.PeakInFlight);
            Assert.Equal(new StreamCheckProgress(0, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);
            for (int i = 1; i < limit; i++) fixture.Release[i].TrySetResult();
            for (int i = 1; i < limit; i++) await fixture.Exited[i].Task.WaitAsync(Deadline);
            using var progressDeadline = new CancellationTokenSource(Deadline);
            while (timing.ReadStartup()!.Discovery.ReachabilityProgress != new StreamCheckProgress(limit - 1, 4))
                await Task.Delay(10, progressDeadline.Token);
            Assert.Equal(limit, fixture.Calls.Count);
            Assert.False(resolve.IsCompleted);
            fixture.Release[0].TrySetResult();
            fixture.ReleaseAll();
            var media = await resolve.WaitAsync(Deadline);
            Assert.Equal(new[] { new PlaybackQuality(144, 250_000), new PlaybackQuality(360, 1_000_000), new PlaybackQuality(720, 2_000_000) }, media.Qualities);
            Assert.Equal(new StreamCheckProgress(4, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);
            var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
            Assert.Equal(limit == 1 ? ReachabilityMode.Sequential : ReachabilityMode.Parallel, diagnostics.Mode);
            Assert.Equal(limit == 1 ? ReachabilityFallback.ConfiguredSequential : ReachabilityFallback.None, diagnostics.Fallback);
            Assert.Equal((4, 0, 0, 0, 0, 0), (diagnostics.TailRequests, diagnostics.SizeRequests, diagnostics.OtherRequests,
                diagnostics.RedirectHops, diagnostics.ProbeRetries, diagnostics.PlayerRetries));
            Assert.NotNull(diagnostics.FinalQuarterMs);
            Assert.True(diagnostics.MedianCheckMs >= 0 && diagnostics.SlowestCheckMs >= diagnostics.MedianCheckMs);
            Assert.Equal(new[] { 0, 1, 2, 3 }, fixture.Calls.Order());
            Assert.Equal(limit, fixture.Peak);

            using var serialFixture = new ProbeFixture();
            serialFixture.ReleaseAll();
            using var serial = new PlaybackResolver(serialFixture);
            Assert.Equal(1, serial.MaxConcurrentStreamChecks);
            var baseline = await serial.ResolveAsync(VideoId).WaitAsync(Deadline);
            Assert.True(baseline.Manifest.AsSpan().SequenceEqual(media.Manifest), "DASH output changed with concurrency.");
        }
        finally
        {
            fixture.ReleaseAll();
            await Task.WhenAny(resolve, Task.Delay(Deadline));
        }
    }

    [Fact]
    public async Task CancellationDrainsAdmittedChecksWithoutStartingQueuedChecks()
    {
        using var fixture = new ProbeFixture();
        using var resolver = new PlaybackResolver(fixture) { MaxConcurrentStreamChecks = 2 };
        using var cancel = new CancellationTokenSource();
        var resolve = resolver.ResolveAsync(VideoId, cancel.Token);
        try
        {
            await fixture.Entered[1].Task.WaitAsync(Deadline);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve.WaitAsync(Deadline));
            Assert.True(fixture.Exited[0].Task.IsCompletedSuccessfully);
            Assert.True(fixture.Exited[1].Task.IsCompletedSuccessfully);
            Assert.Equal(new[] { 0, 1 }, fixture.Calls.Order());
        }
        finally
        {
            cancel.Cancel();
            fixture.ReleaseAll();
            await Task.WhenAny(resolve, Task.Delay(Deadline));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void OutOfRangeLimitIsRejected(int value)
    {
        using var resolver = new PlaybackResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.MaxConcurrentStreamChecks = value);
        Assert.Equal(1, resolver.MaxConcurrentStreamChecks);
        resolver.MaxConcurrentStreamChecks = 16;
        Assert.Equal(16, resolver.MaxConcurrentStreamChecks);
    }

    [Fact]
    public async Task UnsafeRedirectStillFailsAndDrainsTheOtherAdmittedProbe()
    {
        using var fixture = new ProbeFixture(unsafeRedirect: true);
        using var resolver = new PlaybackResolver(fixture) { MaxConcurrentStreamChecks = 2 };
        var resolve = resolver.ResolveAsync(VideoId);
        try
        {
            await fixture.Entered[1].Task.WaitAsync(Deadline);
            fixture.Release[0].TrySetResult();
            var error = await Assert.ThrowsAsync<PlaybackException>(() => resolve.WaitAsync(Deadline));
            Assert.Equal("unsafe_resource", error.Code);
            Assert.True(fixture.Exited[1].Task.IsCompletedSuccessfully);
            Assert.Equal(new[] { 0, 1 }, fixture.Calls.Order());
        }
        finally
        {
            fixture.ReleaseAll();
            await Task.WhenAny(resolve, Task.Delay(Deadline));
        }
    }

    [Fact]
    public async Task PlayerRetryUsesOnlyItsOwnProbeResultsAndProgress()
    {
        using var fixture = new ProbeFixture();
        using var transport = new PublicTransport(fixture);
        using var http = new HttpClient(transport);
        using var cancel = new CancellationTokenSource(Deadline);
        var token = cancel.Token;
        var timing = PlaybackTiming.StartForStartup();
        const string player = "https://www.youtube.com/youtubei/v1/player";
        await transport.DiscoverAsync(VideoId, timing, 2, token, async () =>
        {
            using var firstPlayer = await http.PostAsync(player, null, token);
            fixture.Release[0].TrySetResult();
            using var firstProbe = await http.GetAsync(fixture.TailUri(0), token);
            await fixture.Entered[1].Task.WaitAsync(token);
            Assert.Equal(new StreamCheckProgress(1, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);

            using var secondPlayer = await http.PostAsync(player, null, token);
            Assert.True(fixture.Exited[1].Task.IsCompletedSuccessfully);
            Assert.Equal(new StreamCheckProgress(0, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);
            fixture.ReleaseAll();
            for (int i = 0; i < 4; i++)
            {
                using var probe = await http.GetAsync(fixture.TailUri(i), token);
                Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            }
            return true;
        });
        Assert.Equal(new StreamCheckProgress(4, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 3 }, fixture.Calls.Order());
        var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        Assert.Equal(6, diagnostics.TailRequests);
        Assert.Equal(1, diagnostics.PlayerRetries);
        Assert.Equal(0, diagnostics.ProbeRetries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedSpeculativeProbeIsRetriedOnDemand(bool timeout)
    {
        using var fixture = new ProbeFixture(abandonFirstSecondProbe: true, timeout: timeout);
        using var transport = new PublicTransport(fixture);
        using var http = new HttpClient(transport);
        using var cancel = new CancellationTokenSource(Deadline);
        var token = cancel.Token;
        var timing = PlaybackTiming.StartForStartup();
        await transport.DiscoverAsync(VideoId, timing, 2, token, async () =>
        {
            using var player = await http.PostAsync("https://www.youtube.com/youtubei/v1/player", null, token);
            fixture.ReleaseAll();
            for (int i = 0; i < 4; i++)
            {
                using var probe = await http.GetAsync(fixture.TailUri(i), token);
                Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
                Assert.Equal(2, (await probe.Content.ReadAsByteArrayAsync(token)).Length);
            }
            return true;
        });
        Assert.Equal(new[] { 0, 1, 1, 2, 3 }, fixture.Calls.Order());
        Assert.Equal(new StreamCheckProgress(4, 4), timing.ReadStartup()!.Discovery.ReachabilityProgress);
        var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        Assert.Equal(5, diagnostics.TailRequests);
        Assert.Equal(1, diagnostics.ProbeRetries);
    }

    [Fact]
    public async Task SpeculativeTimerLeavesTimeForTheFirstDemandedProbeToRetry()
    {
        using var fixture = new ProbeFixture(holdFirstProbeUntilCanceled: true);
        var budget = TimeSpan.FromMilliseconds(800);
        using var transport = new PublicTransport(fixture, budget);
        using var http = new HttpClient(transport) { Timeout = budget };
        using var deadline = new CancellationTokenSource(Deadline);
        var token = deadline.Token;
        await transport.DiscoverAsync(VideoId, null, 2, token, async () =>
        {
            using var player = await http.PostAsync("https://www.youtube.com/youtubei/v1/player", null, token);
            fixture.ReleaseAll();
            using var probe = await http.GetAsync(fixture.TailUri(0), token);
            Assert.Equal(2, (await probe.Content.ReadAsByteArrayAsync(token)).Length);
            return true;
        });
        Assert.Equal(new[] { 0, 0, 1 }, fixture.Calls.Order());
    }

    private sealed class ProbeFixture(bool unsafeRedirect = false, bool abandonFirstSecondProbe = false, bool timeout = false,
        bool holdFirstProbeUntilCanceled = false) : HttpMessageHandler
    {
        internal TaskCompletionSource[] Entered { get; } = Signals();
        internal TaskCompletionSource[] Exited { get; } = Signals();
        internal TaskCompletionSource[] Release { get; } = Signals();
        internal ConcurrentQueue<int> Calls { get; } = new();
        private readonly byte[] video = [..IndexTests.Header(), ..new byte[100]];
        private readonly byte[] audio = [..IndexTests.Header(true), ..new byte[100]];
        private int active, peak;
        private int firstProbeCalls, secondProbeCalls;
        internal int Peak => Volatile.Read(ref peak);
        private static string StreamUrl(int index) => $"https://a.googlevideo.com/videoplayback?expire=4102444800&id={index}";
        internal Uri TailUri(int index)
        {
            int length = (index == 3 ? audio : video).Length;
            return new($"{StreamUrl(index)}&range={length - 2}-{length - 1}&hl=en");
        }

        private static TaskCompletionSource[] Signals() => Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        internal void ReleaseAll()
        {
            foreach (var gate in Release) gate.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/sw.js_data")
                return new(HttpStatusCode.OK) { Content = new StringContent("[[null,null,[[[null,null,null,null,null,null,null,null,null,null,null,null,null,\"fixture-visitor\"]]]]]") };
            if (uri.AbsolutePath == "/youtubei/v1/player")
            {

                int[] heights = [720, 360, 144, 0];
                int[] widths = [1280, 640, 256, 0];
                int[] bitrates = [2_000_000, 1_000_000, 250_000, 128_000];
                var streams = Enumerable.Range(0, 4).Select(i => new {
                    itag = 134 + i, url = StreamUrl(i),
                    mimeType = i == 3 ? "audio/mp4; codecs=\"mp4a.40.2\"" : "video/mp4; codecs=\"avc1.64001f\"",
                    contentLength = (i == 3 ? audio : video).Length.ToString(),
                    bitrate = bitrates[i], height = heights[i], width = widths[i], qualityLabel = $"{heights[i]}p"
                });
                var body = JsonSerializer.Serialize(new {
                    playabilityStatus = new { status = "OK" },
                    videoDetails = new { videoId = VideoId, title = "Fixture", author = "Fixture author", lengthSeconds = "1", shortDescription = "Fixture description" },
                    streamingData = new { adaptiveFormats = streams }
                });
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            Assert.True(PublicTransport.IsCdn(uri));
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Range);
            Assert.False(request.Headers.Contains("Cookie"));
            var query = uri.Query[1..].Split('&').ToDictionary(p => p.Split('=')[0], p => p.Split('=')[1]);
            int index = int.Parse(query["id"]);
            byte[] bytes = index == 3 ? audio : video;
            if (query["range"] == $"{bytes.Length - 2}-{bytes.Length - 1}")
            {
                Assert.Equal("en", query["hl"]);
                Calls.Enqueue(index);
                int count = Interlocked.Increment(ref active);
                for (int old; count > (old = Peak) && Interlocked.CompareExchange(ref peak, count, old) != old;) { }
                Entered[index].TrySetResult();
                try
                {
                    if (holdFirstProbeUntilCanceled && index == 0 && Interlocked.Increment(ref firstProbeCalls) == 1)
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    await Release[index].Task.WaitAsync(token);
                }
                finally { Interlocked.Decrement(ref active); Exited[index].TrySetResult(); }
                if (unsafeRedirect && index == 0)
                    return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://untrusted.example/") } };

                if (abandonFirstSecondProbe && index == 1 && Interlocked.Increment(ref secondProbeCalls) == 1)
                {
                    if (timeout) throw new OperationCanceledException("Synthetic probe timeout");
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8192]) };
                }

                bytes = bytes[^2..];
            }
            else Assert.Equal($"0-{bytes.Length - 1}", query["range"]);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
