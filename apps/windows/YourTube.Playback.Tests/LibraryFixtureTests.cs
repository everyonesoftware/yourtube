using System.Net;
using System.Text.Json;
using Xunit;
using YourTube.Playback;
namespace YourTube.Playback.Tests;

public sealed class LibraryFixtureTests
{
    // Synthetic public-shaped provider fixtures; never captured authenticated responses.
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ActualPinnedLibraryResolvesMetadataStreamsAndValidityRedirect(bool includeLength)
    {
        byte[] full = [..IndexTests.Header(), ..new byte[100]];
        byte[] fullAudio = [..IndexTests.Header(true), ..new byte[100]];
        var video = ResolverTests.Url("video"); var audio = ResolverTests.Url("audio");
        var streams = new[] {
            new { itag=134, url=video.AbsoluteUri, mimeType="video/mp4; codecs=\"avc1.64001f\"", bitrate=1000000, contentLength=includeLength ? full.Length.ToString() : null, width=640, height=360, qualityLabel="360p" },
            new { itag=140, url=audio.AbsoluteUri, mimeType="audio/mp4; codecs=\"mp4a.40.2\"", bitrate=128000, contentLength=includeLength ? fullAudio.Length.ToString() : null, width=0, height=0, qualityLabel="" }
        };
        var player = JsonSerializer.Serialize(new { playabilityStatus = new { status="OK" }, videoDetails = new { title="Synthetic fixture title", author="Fixture author", channelId="UCaaaaaaaaaaaaaaaaaaaaaa", lengthSeconds="1", shortDescription="Fixture description" }, microformat = new { playerMicroformatRenderer = new { uploadDate="2020-01-01" } }, streamingData = new { adaptiveFormats=streams } });
        int heads=0, tails=0, redirects=0;
        var handler = new Handler(r => {
            var u = r.RequestUri!;
            if (u.Host == "www.youtube.com")
            {
                Assert.Null(r.Headers.Authorization);
                if (u.AbsolutePath == "/watch") return new(HttpStatusCode.OK) { Content = new StringContent("<html><head><meta property=\"og:url\" content=\"https://www.youtube.com/watch?v=jNQXAC9IVRw\"></head><body><div id=\"player\"></div><script>var ytInitialPlayerResponse = " + player + ";</script></body></html>") };
                if (u.AbsolutePath == "/sw.js_data") return new(HttpStatusCode.OK) { Content = new StringContent("[[null,null,[[[null,null,null,null,null,null,null,null,null,null,null,null,null,\"fixture-visitor\"]]]]]") };
                if (u.AbsolutePath == "/youtubei/v1/player") return new(HttpStatusCode.OK) { Content = new StringContent(player) };
                return new(HttpStatusCode.NotFound);
            }
            Assert.True(PublicTransport.IsCdn(u)); Assert.Null(r.Headers.Authorization); Assert.False(r.Headers.Contains("Cookie"));
            var source = u.Query.Contains("id=audio", StringComparison.Ordinal) ? fullAudio : full;
            if (r.Method == HttpMethod.Head) { heads++; var h = new HttpResponseMessage(HttpStatusCode.OK); h.Content.Headers.ContentLength=source.Length; return h; }
            if (u.Host == "a.googlevideo.com") { redirects++; var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new UriBuilder(u) { Host="b.googlevideo.com" }.Uri; return redirect; }
            var range = u.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("range=", StringComparison.Ordinal))[6..].Split('-');
            int start=int.Parse(range[0]), end=int.Parse(range[1]); if (end==source.Length-1 && start==source.Length-2) tails++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(source[start..(end+1)]) };
        });
        using var resolver = new PlaybackResolver(handler); PlaybackMedia? media = null; string? error = null;
        try { media = await resolver.ResolveAsync("jNQXAC9IVRw"); } catch (PlaybackException e) { error=e.Code; }
        Assert.True(media is not null, "Pinned library fixture resolution failed: " + (error ?? "unknown"));
        Assert.Equal("Synthetic fixture title", media!.Title); Assert.Equal("Fixture author", media.Author); Assert.Equal("Fixture description", media.Description);
        Assert.Single(media.Qualities); Assert.True(tails >= 2); Assert.True(redirects >= 4); Assert.Equal(includeLength ? 0 : 2, heads);
    }
}
