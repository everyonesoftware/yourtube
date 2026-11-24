using System.Net;
using System.Text.Json;
using Xunit;
using YourTube.Playback;
namespace YourTube.Playback.Tests;

public sealed class LibraryFixtureTests
{
    // Synthetic public-shaped provider fixtures; never captured authenticated responses.
    [Theory][InlineData(true, "complete")][InlineData(false, "complete")]
    [InlineData(true, "wrong_id")][InlineData(true, "invalid_duration")][InlineData(true, "missing_description")][InlineData(true, "empty_description")]
    [InlineData(true, "malformed_description")][InlineData(true, "expired_description")][InlineData(true, "missing_audio")]
    [InlineData(true, "unreachable")][InlineData(false, "unreachable")]
    [InlineData(true, "unreachable", 2)][InlineData(false, "unreachable", 2)]
    public async Task ActualPinnedLibraryResolvesMetadataStreamsAndValidityRedirect(bool includeLength, string scenario, int maxConcurrentChecks = 1)
    {
        byte[] full = [..IndexTests.Header(), ..new byte[100]];
        byte[] fullAudio = [..IndexTests.Header(true), ..new byte[100]];
        var video = ResolverTests.Url("video"); var audio = ResolverTests.Url("audio");
        var streams = new[] {
            new { itag=160, url=ResolverTests.Url("missing").AbsoluteUri, mimeType="video/mp4; codecs=\"avc1.64001f\"", bitrate=250000, contentLength=includeLength ? full.Length.ToString() : null, width=256, height=144, qualityLabel="144p" },
            new { itag=248, url=ResolverTests.Url("decoy-vp9").AbsoluteUri, mimeType="video/webm; codecs=\"vp9\"", bitrate=2000000, contentLength=includeLength ? full.Length.ToString() : null, width=1920, height=1080, qualityLabel="1080p" },
            new { itag=315, url=ResolverTests.Url("decoy-high").AbsoluteUri, mimeType="video/mp4; codecs=\"avc1.64001f\"", bitrate=5000000, contentLength=includeLength ? full.Length.ToString() : null, width=3840, height=2160, qualityLabel="2160p" },
            new { itag=18, url=ResolverTests.Url("decoy-muxed").AbsoluteUri, mimeType="video/mp4; codecs=\"avc1.64001f, mp4a.40.2\"", bitrate=1000000, contentLength=includeLength ? full.Length.ToString() : null, width=640, height=360, qualityLabel="360p" },
            new { itag=134, url=video.AbsoluteUri, mimeType="video/mp4; codecs=\"avc1.64001f\"", bitrate=1000000, contentLength=includeLength ? full.Length.ToString() : null, width=640, height=360, qualityLabel="360p" },
            new { itag=140, url=audio.AbsoluteUri, mimeType="audio/mp4; codecs=\"mp4a.40.2\"", bitrate=128000, contentLength=includeLength ? fullAudio.Length.ToString() : null, width=0, height=0, qualityLabel="" }
        };
        string Player(string id, string duration, string? description) => JsonSerializer.Serialize(new { playabilityStatus = new { status="OK" }, videoDetails = new { videoId=id, title="Synthetic fixture title", author="Fixture author", channelId="UCaaaaaaaaaaaaaaaaaaaaaa", lengthSeconds=duration, shortDescription=description }, microformat = new { playerMicroformatRenderer = new { uploadDate="2020-01-01" } }, streamingData = new { adaptiveFormats=scenario switch { "missing_audio" => streams[1..^1], "unreachable" => streams, _ => streams[1..] } } });
        var watchPlayer = Player("jNQXAC9IVRw", "1", "Fixture description");
        var streamPlayer = Player(scenario == "wrong_id" ? "abcdefghijk" : "jNQXAC9IVRw", scenario == "invalid_duration" ? "0" : "1",
            scenario switch { "missing_description" or "expired_description" => null, "empty_description" => "", _ => "Fixture description" });
        if (scenario == "malformed_description") streamPlayer = streamPlayer.Replace("Fixture description", "\\uD800", StringComparison.Ordinal);
        var clock = new FixtureClock();
        var timing = PlaybackTiming.StartForStartup();
        int heads=0, tails=0, redirects=0, decoyRequests=0, watches=0;
        var probeSnapshots = new List<StreamDiscoverySnapshot>();
        var handler = new Handler(r => {
            var u = r.RequestUri!;
            if (u.Host == "www.youtube.com")
            {
                Assert.Null(r.Headers.Authorization);
                if (u.AbsolutePath == "/watch") { Interlocked.Increment(ref watches); if (scenario == "expired_description") clock.Now = DateTimeOffset.MaxValue; return new(HttpStatusCode.OK) { Content = new StringContent("<html><head><meta property=\"og:url\" content=\"https://www.youtube.com/watch?v=jNQXAC9IVRw\"></head><body><div id=\"player\"></div><script>var ytInitialPlayerResponse = " + watchPlayer + ";</script></body></html>") }; }
                if (u.AbsolutePath == "/sw.js_data") return new(HttpStatusCode.OK) { Content = new StringContent("[[null,null,[[[null,null,null,null,null,null,null,null,null,null,null,null,null,\"fixture-visitor\"]]]]]") };
                if (u.AbsolutePath == "/youtubei/v1/player")
                {
                    Assert.Equal(StreamDiscoveryStep.StreamList, timing.ReadStartup()!.Discovery.Current);
                    return new(HttpStatusCode.OK) { Content = new StringContent(streamPlayer) };
                }
                return new(HttpStatusCode.NotFound);
            }
            Assert.True(PublicTransport.IsCdn(u)); Assert.Null(r.Headers.Authorization); Assert.False(r.Headers.Contains("Cookie"));
            var live = timing.ReadStartup()!;
            if (live.Completed == 0)
            {
                Assert.Equal(StreamDiscoveryStep.Reachability, live.Discovery.Current);
                lock (probeSnapshots) probeSnapshots.Add(live.Discovery);
            }
            else Assert.Null(live.Discovery.Current); // DASH index reads are not discovery.
            if (u.Query.Contains("id=decoy-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref decoyRequests);
                return new(HttpStatusCode.NotFound);
            }
            if (u.Query.Contains("id=missing", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
            var source = u.Query.Contains("id=audio", StringComparison.Ordinal) ? fullAudio : full;
            if (r.Method == HttpMethod.Head) { Interlocked.Increment(ref heads); var h = new HttpResponseMessage(HttpStatusCode.OK); h.Content.Headers.ContentLength=source.Length; return h; }
            if (u.Host == "a.googlevideo.com") { Interlocked.Increment(ref redirects); var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new UriBuilder(u) { Host="b.googlevideo.com" }.Uri; return redirect; }
            var range = u.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("range=", StringComparison.Ordinal))[6..].Split('-');
            int start=int.Parse(range[0]), end=int.Parse(range[1]); if (end==source.Length-1 && start==source.Length-2) Interlocked.Increment(ref tails);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(source[start..(end+1)]) };
        });
        using var resolver = new PlaybackResolver(handler) { MaxConcurrentStreamChecks = maxConcurrentChecks }; PlaybackMedia? media = null; string? error = null;
        try { media = await resolver.ResolveAsync("jNQXAC9IVRw", CancellationToken.None, timing); } catch (PlaybackException e) { error=e.Code; timing.FailStartup(); }
        var discovery = Assert.IsType<PlaybackStartupSnapshot>(timing.ReadStartup()).Discovery;
        Assert.NotNull(discovery.Durations[0]);
        if (scenario == "missing_audio")
        {
            Assert.Null(media); Assert.Equal("unsupported_media", error);
            Assert.True(timing.ReadStartup() is { Completed: 0, Failed: true });
            Assert.Equal(StreamDiscoveryStep.Compatibility, discovery.Current);
            Assert.All(discovery.Durations, duration => Assert.NotNull(duration));
            return;
        }
        if (scenario == "wrong_id")
        {
            Assert.Null(media); Assert.Equal("unavailable", error);
            Assert.Equal(0, watches + tails + redirects + heads + decoyRequests);
            Assert.Equal(StreamDiscoveryStep.StreamList, discovery.Current);
            Assert.Null(discovery.Durations[1]);
            Assert.Null(discovery.Durations[2]);
            return;
        }
        Assert.True(media is not null, "Pinned library fixture resolution failed: " + (error ?? "unknown"));
        Assert.Null(discovery.Current);
        Assert.All(discovery.Durations, duration => Assert.NotNull(duration));
        if (scenario is "complete" or "unreachable")
        {
            int total = scenario == "unreachable" ? 3 : 2;
            int[] expected = includeLength ? [0, 0, 1, 1] : [0, 0, 0, 1, 1, 1];
            if (total == 3) expected = [0, .. expected.Select(n => n + 1)];
            if (maxConcurrentChecks == 1 || !includeLength)
                Assert.Equal(expected.Select(n => (StreamCheckProgress?)new StreamCheckProgress(n, total)), probeSnapshots.Select(s => s.ReachabilityProgress));
            else
                Assert.Equal(expected.Length, probeSnapshots.Count); // No duplicate probes or redirect hops.
            Assert.Equal(new StreamCheckProgress(total, total), discovery.ReachabilityProgress);
        }
        Assert.Equal(scenario == "invalid_duration" ? 1 : 0, watches);
        Assert.Equal("Synthetic fixture title", media!.Title); Assert.Equal("Fixture author", media.Author);
        bool missing = scenario is "missing_description" or "malformed_description" or "expired_description";
        Assert.Equal(missing || scenario == "empty_description" ? "" : "Fixture description", media.Description);
        Assert.Single(media.Qualities); Assert.Equal(2, tails); Assert.Equal(4, redirects); Assert.Equal(includeLength ? 0 : 2, heads);
        // The existing filter leaves unrewritable JSON untouched. Invalid optional
        // text must not make resolution fail or relax final codec selection.
        if (scenario != "malformed_description") Assert.Equal(0, decoyRequests);
        int beforeDeferred = watches;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Null(await resolver.GetDeferredDescriptionAsync(media, canceled.Token));
        Assert.Equal(beforeDeferred, watches);
        var description = await resolver.GetDeferredDescriptionAsync(media, CancellationToken.None, clock);
        Assert.Equal(missing && scenario != "expired_description" ? "Fixture description" : null, description);
        Assert.Equal(beforeDeferred + (missing ? 1 : 0), watches);
        if (scenario == "expired_description")
        {
            Assert.Null(await resolver.GetDeferredDescriptionAsync(media, CancellationToken.None, clock));
            Assert.Equal(beforeDeferred + 1, watches);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
