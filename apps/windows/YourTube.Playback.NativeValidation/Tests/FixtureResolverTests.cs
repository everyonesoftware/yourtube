using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Xunit;
using YourTube.Playback;

namespace YourTube.Playback.NativeValidation;

public sealed class FixtureResolverTests
{
    [Fact]
    public async Task BothTracksExposeRealInitializationIndexAndFragmentRanges()
    {
        var resolver = new FixtureResolver();
        var media = await resolver.ResolveAsync(FixtureResolver.PrimaryId);
        var mpd = XDocument.Parse(Encoding.UTF8.GetString(media.Manifest));
        var ns = mpd.Root!.Name.Namespace;
        Assert.Equal("urn:mpeg:dash:profile:isoff-on-demand:2011", (string?)mpd.Root.Attribute("profiles"));
        var tracks = mpd.Descendants(ns + "Representation").ToArray();
        Assert.Equal(2, tracks.Length);
        foreach (var track in tracks)
        {
            var uri = new Uri(track.Element(ns + "BaseURL")!.Value, UriKind.Absolute);
            Assert.Equal("fixture.invalid", uri.Host);
            var segment = track.Element(ns + "SegmentBase")!;
            var mime = track.Parent!.Attribute("mimeType")!.Value;
            var full = await resolver.ReadResourceAsync(media, uri, null, null);
            Assert.Equal(mime, full.ContentType);

            async Task<byte[]> ReadRange(string text)
            {
                var range = text.Split('-').Select(ulong.Parse).ToArray();
                var resource = await resolver.ReadResourceAsync(media, uri, range[0], range[1] - range[0] + 1);
                Assert.Equal(full.Bytes[checked((int)range[0])..checked((int)(range[1] + 1))], resource.Bytes);
                return resource.Bytes;
            }

            var init = await ReadRange(segment.Element(ns + "Initialization")!.Attribute("range")!.Value);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(init, 4, 4));
            var index = await ReadRange(segment.Attribute("indexRange")!.Value);
            Assert.Equal("sidx", Encoding.ASCII.GetString(index, 4, 4));
            Assert.Equal(ulong.Parse(segment.Attribute("presentationTimeOffset")!.Value), BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(20)));
            Assert.Equal(uint.Parse(segment.Attribute("timescale")!.Value), BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(16)));
            var start = (ulong)(init.Length + index.Length);
            var fragment = await resolver.ReadResourceAsync(media, uri, start, null);
            Assert.Equal("moof", Encoding.ASCII.GetString(fragment.Bytes, 4, 4));
            Assert.Equal(full.Bytes[(int)start..], fragment.Bytes);
        }
    }

    [Fact]
    public async Task OnlyFixtureIdentitiesAndExactOwnedTrackUrisAreAccepted()
    {
        var resolver = new FixtureResolver();
        var media = await resolver.ResolveAsync(FixtureResolver.PrimaryId);
        var track = new Uri("https://fixture.invalid/video.mp4");
        var foreignMedia = await new FixtureResolver().ResolveAsync(media.VideoId);
        await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveAsync("jNQXAC9IVRw"));
        await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(foreignMedia, track, 0, 1));
        foreach (var uri in new[] { new Uri("https://fixture.invalid/video.mp4?x=1"), new Uri("https://fixture.invalid/other.mp4"), new Uri("file:///video.mp4"), new Uri("https://example.com/video.mp4") })
            await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, uri, 0, 1));
    }

    [Fact]
    public async Task FixtureIdentitiesAreSelectableByTheProductionPlaybackController()
    {
        var resolver = new FixtureResolver();
        var ids = new[] { FixtureResolver.PrimaryId, FixtureResolver.PendingId, FixtureResolver.ReplacementId };
        Assert.Equal(3, ids.Distinct().Count());
        foreach (var id in ids)
        {
            Assert.NotNull(YourTube.App.BrowserPolicy.VideoUri(id));
            Assert.Equal(id, (await resolver.ResolveAsync(id)).VideoId);
        }
    }

    [Fact]
    public async Task InvalidRangesAndCanceledReadsFailClosed()
    {
        var resolver = new FixtureResolver();
        var media = await resolver.ResolveAsync(FixtureResolver.PrimaryId);
        var track = new Uri("https://fixture.invalid/audio.mp4");
        var full = await resolver.ReadResourceAsync(media, track, null, null);
        foreach (var (offset, length) in new (ulong?, ulong?)[] { (0, 0), ((ulong)full.Bytes.Length, null), (1, (ulong)full.Bytes.Length), (1, ulong.MaxValue), (ulong.MaxValue, 2) })
            await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, track, offset, length));
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ReadResourceAsync(media, track, 0, 1, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(media.VideoId, canceled));
        full.Bytes[0] ^= byte.MaxValue;
        var first = await resolver.ReadResourceAsync(media, track, 0, 1);
        Assert.NotEqual(full.Bytes[0], first.Bytes[0]);
    }
}
