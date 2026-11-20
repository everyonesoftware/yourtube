using System.Net;
using YourTube.Playback;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class RedirectBindingTests
{
    private const string Origin = "https://a.googlevideo.com/videoplayback?";
    private const string Edge = "https://b.googlevideo.com";
    private const string Media = "id=fixture&itag=134&clen=100&lmt=123";
    private const string Range = "&range=10-19";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllowsObservedEdgeRoutingChangesOnlyWhenUnsigned(bool signed)
    {
        string signature = signed ? "&sparams=id%2Cmet%2Cclen&sig=fixture" : "&sparams=id%2Cclen&sig=fixture";
        var handler = new Handler(r => r.RequestUri!.Host == "a.googlevideo.com"
            ? Redirect(Edge + "/videoplayback?" + Media + Range + signature + "&met=new&fexp=new&cm2rm=1&rrc=1&cmsv=2")
            : Slice());
        using var http = new HttpClient(new PublicTransport(handler));
        string original = Origin + Media + Range + signature + "&met=old&fexp=old&fvip=3";
        if (signed)
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync(original));
            Assert.Equal("unsafe_resource", error.Code);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            Assert.Equal(10, (await http.GetByteArrayAsync(original)).Length);
            Assert.Equal(2, handler.Calls);
        }
    }

    [Theory]
    [InlineData("/other?" + Media + Range)]
    [InlineData("/videoplayback?id=other&itag=134&clen=100&lmt=123" + Range)]
    [InlineData("/videoplayback?id=fixture&itag=140&clen=100&lmt=123" + Range)]
    [InlineData("/videoplayback?id=fixture&itag=134&clen=200&lmt=123" + Range)]
    [InlineData("/videoplayback?id=fixture&itag=134&clen=100&lmt=456" + Range)]
    [InlineData("/videoplayback?itag=134&clen=100&lmt=123" + Range)]
    [InlineData("/videoplayback?" + Media + "&range=20-29")]
    [InlineData("/videoplayback?" + Media)]
    [InlineData("/videoplayback?" + Media + Range + "&%72ange=10-19")]
    [InlineData("/videoplayback?" + Media + Range + "&ID=fixture")]
    [InlineData("/videoplayback?" + Media + Range + "&unknown=changed")]
    public async Task RejectsChangedOrAmbiguousResourceOrQueryRange(string target)
    {
        // The fixture's equally sized body would not distinguish the resource or interval.
        var handler = new Handler(r => r.RequestUri!.Host == "a.googlevideo.com" ? Redirect(Edge + target) : Slice());
        using var http = new HttpClient(new PublicTransport(handler));
        var error = await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync(Origin + Media + Range));
        Assert.Equal("unsafe_resource", error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)] // HEAD has no range.
    [InlineData(true)] // Header-only GET must not acquire a second selector.
    public async Task RejectsIntroducedQueryRange(bool headerRange)
    {
        var handler = new Handler(r => r.RequestUri!.Host == "a.googlevideo.com" ? Redirect(Edge + "/videoplayback?" + Media + Range) : Slice());
        using var http = new HttpClient(new PublicTransport(handler));
        using var request = new HttpRequestMessage(headerRange ? HttpMethod.Get : HttpMethod.Head, Origin + Media);
        if (headerRange) request.Headers.Range = new(10, 19);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => http.SendAsync(request));
        Assert.Equal("unsafe_resource", error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChecksEachHopWhileAllowingRoutingChanges(bool changeSecondHop)
    {
        var handler = new Handler(r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Null(r.Headers.Range);
            if (r.RequestUri!.Host == "a.googlevideo.com")
                return Redirect(Edge + "/videoplayback?range=10-19&lmt=123&clen=100&itag=134&id=fixture&mn=edge-b&redirect_counter=1");
            if (r.RequestUri.Query.Contains("redirect_counter=1", StringComparison.Ordinal))
                return Redirect("?" + Media + (changeSecondHop ? "&range=20-29" : Range) + "&redirect_counter=2");
            return Slice();
        });
        using var http = new HttpClient(new PublicTransport(handler));
        var start = Origin + Media + Range + "&mn=edge-a&mt=old";
        if (changeSecondHop)
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync(start));
            Assert.Equal("unsafe_resource", error.Code);
            Assert.Equal(2, handler.Calls);
        }
        else
        {
            Assert.Equal(10, (await http.GetByteArrayAsync(start)).Length);
            Assert.Equal(3, handler.Calls);
        }
    }

    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new(target, UriKind.RelativeOrAbsolute) }
    };

    private static HttpResponseMessage Slice() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
}
