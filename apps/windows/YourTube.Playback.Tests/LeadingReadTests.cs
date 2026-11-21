using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Xunit;
using YourTube.Playback;

namespace YourTube.Playback.Tests;

public sealed class LeadingReadTests
{
    [Theory]
    [InlineData("small", "16384")]
    [InlineData("large", "16384,1048576")]
    [InlineData("split_extended_header", "16384,1048576")]
    [InlineData("malformed", "16384")]
    [InlineData("out_of_bounds", "16384")]
    [InlineData("no_room_for_index", "16384")]
    public async Task IndexReadFallsBackOnlyForAnIncompletePrefixWithinTheOriginalBound(string shape, string expected)
    {
        var header = IndexTests.Header();
        byte[] prefix = header;
        if (shape == "large")
            prefix = [..header[..^44], ..IndexTests.Box("free", new byte[20 * 1024]), ..header[^44..]];
        if (shape == "split_extended_header")
        {
            byte[] extended = [0, 0, 0, 1, (byte)'s', (byte)'i', (byte)'d', (byte)'x', 0, 0, 0, 0, 0, 0, 0, 52];
            prefix = [..header[..^44], ..IndexTests.Box("free", new byte[16384 - 8 - 8 - (header.Length - 44)]), ..extended, ..header[^36..]];
        }
        if (shape == "malformed") prefix = [..IndexTests.Box("pssh", []), ..header];
        if (shape is "out_of_bounds" or "no_room_for_index")
        {
            prefix = [..header[..^44], ..IndexTests.Box("free", [])];
            uint size = shape == "out_of_bounds" ? 2 * 1024 * 1024 : (uint)(1024 * 1024 - (prefix.Length - 8) - 43);
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(prefix.Length - 8), size);
        }
        byte[] videoBytes = new byte[1024 * 1024 + 1024];
        prefix.CopyTo(videoBytes, 0);
        byte[] audioBytes = [..IndexTests.Header(true), ..new byte[100]];
        var video = new MediaTrack(ResolverTests.Url(), (ulong)videoBytes.Length, "avc1.64001f", 1000000, 640, 360);
        var requests = new List<int>();
        using var resolver = new PlaybackResolver(new Handler(r => {
            bool audio = r.RequestUri!.Query.Contains("id=audio", StringComparison.Ordinal);
            var range = r.RequestUri.Query.Split('&').Single(x => x.StartsWith("range=", StringComparison.Ordinal));
            Assert.StartsWith("range=0-", range);
            int length = int.Parse(range[8..]) + 1;
            if (!audio) requests.Add(length);
            var full = audio ? audioBytes : videoBytes;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(full[..length]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, length - 1, full.Length);
            return response;
        }));
        var task = resolver.AssembleAsync("jNQXAC9IVRw", "", "", "", TimeSpan.FromSeconds(1), [video, ResolverTests.Track(true)]);
        if (shape is "malformed" or "out_of_bounds" or "no_room_for_index")
            Assert.Equal("unsupported_media", (await Assert.ThrowsAsync<PlaybackException>(() => task)).Code);
        else
            Assert.Equal(360, Assert.Single((await task).Qualities).Height);
        Assert.Equal(expected, string.Join(',', requests));
    }
}
