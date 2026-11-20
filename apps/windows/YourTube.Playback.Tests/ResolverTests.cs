using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using YourTube.Playback;
using Xunit;
namespace YourTube.Playback.Tests;
public sealed class ResolverTests
{
    internal static Uri Url(string tag = "video") => new("https://a.googlevideo.com/videoplayback?expire=" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() + "&id=" + tag);
    internal static MediaTrack Track(bool audio = false) => new(Url(audio ? "audio" : "video"), (ulong)IndexTests.Header(audio).Length + 100, audio ? "mp4a.40.2" : "avc1.64001f", audio ? 128000u : 1000000u, audio ? 0 : 640, audio ? 0 : 360);
    internal static Handler Network() => new(r => {
        Assert.Null(r.Headers.Range);
        var query = r.RequestUri!.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("range=", StringComparison.Ordinal))[6..].Split('-');
        byte[] full = [..IndexTests.Header(r.RequestUri!.Query.Contains("id=audio", StringComparison.Ordinal)), ..new byte[100]];
        var from = long.Parse(query[0]); var to = long.Parse(query[1]);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(full[(int)from..((int)to + 1)]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, full.Length);
        return response;
    });
    [Fact] public async Task RejectsExpiredMediaBeforeHeaderDownload()
    {
        var handler = Network(); using var resolver = new PlaybackResolver(handler);
        var expired = new MediaTrack(new Uri("https://a.googlevideo.com/videoplayback?expire=100"), 1000, "avc1.64001f", 1000000, 640, 360);
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.AssembleAsync("jNQXAC9IVRw", "", "", "", TimeSpan.FromSeconds(1), [expired, Track(true)]));
        Assert.Equal("expired", e.Code); Assert.Equal(0, handler.Calls);
    }
    [Fact] public async Task DisposedResolverRejectsNewWork()
    {
        var handler = Network(); var resolver = new PlaybackResolver(handler); resolver.Dispose();
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveAsync("jNQXAC9IVRw")); Assert.Equal("disposed", e.Code); Assert.Equal(0, handler.Calls);
    }
    [Fact] public async Task CancelledAssemblyDoesNotFetch()
    {
        var handler = Network(); using var resolver = new PlaybackResolver(handler); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.AssembleAsync("jNQXAC9IVRw", "", "", "", TimeSpan.FromSeconds(1), [Track(), Track(true)], cancellation.Token)); Assert.Equal(0, handler.Calls);
    }
    [Fact] public async Task BuildsSeparateTrackDashFromActualIndexes()
    {
        using var resolver = new PlaybackResolver(Network());
        var media = await resolver.AssembleAsync("jNQXAC9IVRw", "Title", "Author", "Description", TimeSpan.FromSeconds(1), [Track(), Track(true)]);
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(media.Manifest)); XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        Assert.Equal(2, xml.Descendants(ns + "Representation").Count());
        Assert.All(xml.Descendants(ns + "SegmentBase"), s => { Assert.NotNull(s.Attribute("indexRange")); Assert.Equal("1000", s.Attribute("timescale")?.Value); Assert.Equal("0", s.Attribute("presentationTimeOffset")?.Value); });
        Assert.Equal(360, Assert.Single(media.Qualities).Height);
        Assert.True(media.ExpiresAt < DateTimeOffset.UtcNow.AddHours(1));
        var uri = new Uri(xml.Descendants(ns + "BaseURL").First().Value);
        var resource = await resolver.ReadResourceAsync(media, uri, 0, 10); Assert.Equal(10, resource.Bytes.Length);
    }
    [Fact] public async Task RejectsUnownedMediaEvenWithSafeHost()
    {
        using var resolver = new PlaybackResolver(Network());
        var media = new PlaybackMedia("id", "", "", "", TimeSpan.FromSeconds(1), DateTimeOffset.MaxValue, [], new("https://www.youtube.com/yourtube-playback.mpd"), []);
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, Url(), 0, 10)); Assert.Equal("unsafe_resource", e.Code);
    }
}
