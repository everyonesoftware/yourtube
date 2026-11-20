using System.Net;
using YourTube.Playback;
using Xunit;
namespace YourTube.Playback.Tests;
internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
{
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(action(request)); }
}
public sealed class TransportTests
{
    [Theory]
    [InlineData("https://googlevideo.com/x")][InlineData("https://a.googlevideo.com.evil.test/x")][InlineData("http://a.googlevideo.com/x")][InlineData("https://a.googlevideo.com:444/x")][InlineData("https://user@a.googlevideo.com/x")][InlineData("https://m.youtube.com/x")]
    public async Task RejectsOutsideBoundary(string value)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)); using var http = new HttpClient(new PublicTransport(handler));
        var error = await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync(value)); Assert.Equal("unsafe_resource", error.Code); Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData("https://evil.test/x")][InlineData("http://b.googlevideo.com/x")][InlineData("https://b.googlevideo.com:444/x")][InlineData("https://www.youtube.com/x")]
    public async Task RejectsRedirectBeforeFollowing(string target)
    {
        var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(target); return response; });
        using var http = new HttpClient(new PublicTransport(handler));
        var e = await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync("https://a.googlevideo.com/x")); Assert.Equal("unsafe_resource", e.Code); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task StopsRedirectCycles()
    {
        var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://a.googlevideo.com/x"); return response; });
        using var http = new HttpClient(new PublicTransport(handler));
        await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync("https://a.googlevideo.com/x")); Assert.Equal(5, handler.Calls);
    }
    [Fact] public async Task RejectsOversizeDeclaredBody()
    {
        var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentLength = 16777217; return response; });
        using var http = new HttpClient(new PublicTransport(handler)); await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync("https://a.googlevideo.com/x"));
    }
    [Fact] public async Task RejectsCompressedMedia()
    {
        var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentEncoding.Add("gzip"); return response; });
        using var http = new HttpClient(new PublicTransport(handler)); await Assert.ThrowsAsync<PlaybackException>(() => http.GetAsync("https://a.googlevideo.com/x"));
    }
    [Fact] public async Task RedirectStripsSecretsAndKeepsRange()
    {
        var handler = new Handler(r => {
            Assert.Null(r.Headers.Authorization); Assert.False(r.Headers.Contains("Cookie")); Assert.False(r.Headers.Contains("X-Secret"));
            Assert.Equal("bytes=0-9", r.Headers.Range?.ToString());
            if (r.RequestUri!.Host == "a.googlevideo.com") { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://b.googlevideo.com/x"); return response; }
            return new(HttpStatusCode.OK);
        });
        using var http = new HttpClient(new PublicTransport(handler)); using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.googlevideo.com/x");
        request.Headers.TryAddWithoutValidation("Cookie", "secret"); request.Headers.TryAddWithoutValidation("X-Secret", "secret"); request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret"); request.Headers.Range = new(0,9);
        using var result = await http.SendAsync(request); Assert.Equal(2, handler.Calls);
    }
}
