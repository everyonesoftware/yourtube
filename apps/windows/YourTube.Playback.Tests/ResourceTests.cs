using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Xunit;
using YourTube.Playback;
namespace YourTube.Playback.Tests;
public sealed class ResourceTests
{
    private static async Task<(PlaybackMedia Media, Uri Uri)> Create(PlaybackResolver resolver)
    {
        var media = await resolver.AssembleAsync("jNQXAC9IVRw", "", "", "", TimeSpan.FromSeconds(1), [ResolverTests.Track(), ResolverTests.Track(true)]);
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        return (media, new Uri(XDocument.Parse(System.Text.Encoding.UTF8.GetString(media.Manifest)).Descendants(ns + "BaseURL").First().Value));
    }
    [Theory]
    [InlineData(0,0)][InlineData(ulong.MaxValue,1)][InlineData(0,16777217)][InlineData(1,ulong.MaxValue)]
    public async Task RejectsInvalidRangesBeforeNetwork(ulong start, ulong length)
    {
        var handler = ResolverTests.Network(); using var resolver = new PlaybackResolver(handler); var (media, uri) = await Create(resolver); int calls = handler.Calls;
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, uri, start, length)); Assert.Equal("invalid_range", e.Code); Assert.Equal(calls, handler.Calls);
    }
    [Fact] public async Task ExactOwnershipCannotBeForgedByChangingQuery()
    {
        var handler = ResolverTests.Network(); using var resolver = new PlaybackResolver(handler); var (media, uri) = await Create(resolver); int calls = handler.Calls;
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, new Uri(uri.AbsoluteUri + "&extra=1"), 0, 10)); Assert.Equal("unsafe_resource", e.Code); Assert.Equal(calls, handler.Calls);
    }
    [Fact] public async Task AnotherResolverDoesNotOwnMedia()
    {
        using var owner = new PlaybackResolver(ResolverTests.Network()); var (media, uri) = await Create(owner); using var stranger = new PlaybackResolver(ResolverTests.Network());
        var e = await Assert.ThrowsAsync<PlaybackException>(() => stranger.ReadResourceAsync(media, uri, 0, 10)); Assert.Equal("unsafe_resource", e.Code);
    }
    [Theory]
    [InlineData(200,0,9,302)][InlineData(206,1,10,302)][InlineData(206,0,9,999)][InlineData(206,0,8,302)]
    public async Task RejectsMisleadingContentRanges(int status, long from, long to, long total)
    {
        bool mutate = false;
        var original = ResolverTests.Network(); using var invoker = new HttpMessageInvoker(original);
        var handler = new Handler(r => {
            if (!mutate) return invoker.SendAsync(r, CancellationToken.None).GetAwaiter().GetResult();
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(new byte[10]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total); return response;
        });
        using var resolver = new PlaybackResolver(handler); var (media, uri) = await Create(resolver); mutate = true;
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, uri, 0, 10)); Assert.Equal("invalid_range", e.Code);
    }
    [Fact] public async Task SanitizesTransportExceptions()
    {
        using var resolver = new PlaybackResolver(new Handler(_ => throw new HttpRequestException("PRIVATE_PROVIDER_BODY")));
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveAsync("jNQXAC9IVRw"));
        Assert.Null(e.InnerException); Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", e.ToString());
    }
    [Theory][InlineData("")][InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")][InlineData("jNQXAC9IVRw\n")]
    public async Task RejectsNonCanonicalVideoIdsBeforeNetwork(string id)
    {
        var handler = ResolverTests.Network(); using var resolver = new PlaybackResolver(handler);
        var e = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveAsync(id)); Assert.Equal("invalid_video", e.Code); Assert.Equal(0, handler.Calls);
    }
}
