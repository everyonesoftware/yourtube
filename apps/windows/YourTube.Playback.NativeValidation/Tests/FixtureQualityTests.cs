using System.Buffers.Binary;
using System.Xml.Linq;
using Xunit;
using YourTube.Playback;

namespace YourTube.Playback.NativeValidation;

public sealed class FixtureQualityTests
{
    private static readonly XNamespace Dash = "urn:mpeg:dash:schema:mpd:2011";

    [Theory]
    [InlineData(null, "90,180")]
    [InlineData(90, "90")]
    [InlineData(180, "180")]
    public async Task OnlyRequestedVideoRenditionsAreAdvertisedWithSharedAudio(int? height, string expected)
    {
        var resolver = new FixtureResolver();
        var media = await resolver.ResolveQualityAsync(FixtureResolver.PrimaryId, height, default);
        var tracks = Tracks(media);
        Assert.Equal(expected, Heights(tracks));
        Assert.Equal(expected, string.Join(',', media.Qualities.Select(q => q.Height).Order()));
        foreach (var video in tracks.Where(t => t.Attribute("height") is not null))
        {
            Assert.Equal((uint)video.Attribute("bandwidth")!,
                media.Qualities.Single(q => q.Height == (int)video.Attribute("height")!).Bitrate);
        }
        var audio = Assert.Single(tracks, t => (string?)t.Parent!.Attribute("mimeType") == "audio/mp4");
        Assert.Equal("https://fixture.invalid/audio.mp4", UriOf(audio).AbsoluteUri);
    }

    [Fact]
    public async Task TheAlternateHasItsOwnBoundedIndexAndAlignedPlayableFragments()
    {
        var resolver = new FixtureResolver();
        var auto = await resolver.ResolveQualityAsync(FixtureResolver.PrimaryId, null, default);
        var tracks = Tracks(auto).Where(t => t.Attribute("height") is not null).ToArray();
        Assert.Equal(2, tracks.Select(UriOf).Distinct().Count());
        uint[]? timeline = null;
        foreach (var track in tracks)
        {
            var uri = UriOf(track);
            Assert.Equal("fixture.invalid", uri.Host);
            var full = await resolver.ReadResourceAsync(auto, uri, null, null);
            Assert.Equal("video/mp4", full.ContentType);
            var segment = track.Element(Dash + "SegmentBase")!;
            async Task<byte[]> ReadRange(string value)
            {
                var range = value.Split('-').Select(ulong.Parse).ToArray();
                var bytes = (await resolver.ReadResourceAsync(auto, uri, range[0], range[1] - range[0] + 1)).Bytes;
                Assert.Equal(full.Bytes[(int)range[0]..(int)(range[1] + 1)], bytes);
                return bytes;
            }

            var init = await ReadRange((string)segment.Element(Dash + "Initialization")!.Attribute("range")!);
            Assert.Equal("ftyp"u8.ToArray(), init[4..8]);
            // The first avc1 may be an ftyp compatible brand; the final avc1 in our init is the sample entry.
            var sample = init.AsSpan().LastIndexOf("avc1"u8);
            Assert.True(sample > 0);
            Assert.Equal((int)track.Attribute("width")!, BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(sample + 28)));
            Assert.Equal((int)track.Attribute("height")!, BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(sample + 30)));
            var index = await ReadRange((string)segment.Attribute("indexRange")!);
            Assert.Equal($"{init.Length}-{init.Length + index.Length - 1}", (string?)segment.Attribute("indexRange"));
            Assert.Equal("sidx"u8.ToArray(), index[4..8]);
            Assert.Equal(index.Length, BinaryPrimitives.ReadInt32BigEndian(index));
            Assert.Equal(1, index[8]); // Both generated fixtures use version 1.
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(16));
            Assert.Equal((uint)segment.Attribute("timescale")!, timescale);
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(20)));
            Assert.Equal(0UL, (ulong)segment.Attribute("presentationTimeOffset")!);
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(index.AsSpan(28)));
            var count = BinaryPrimitives.ReadUInt16BigEndian(index.AsSpan(38));
            Assert.Equal(40 + 12 * count, index.Length);
            Assert.True(count > 1);
            var start = (ulong)(init.Length + index.Length);
            var durations = new uint[count];
            for (int i = 0; i < count; i++)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(40 + 12 * i));
                Assert.InRange(size, 8U, (uint)(full.Bytes.Length - (int)start));
                var fragment = await resolver.ReadResourceAsync(auto, uri, start, size);
                Assert.Equal(full.Bytes[(int)start..(int)(start + size)], fragment.Bytes);
                Assert.Equal("moof"u8.ToArray(), fragment.Bytes[4..8]);
                start += size;
                var duration = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(44 + 12 * i));
                Assert.Equal(2 * timescale, duration);
                durations[i] = duration / timescale;
            }
            Assert.Equal(auto.Duration.TotalSeconds, durations.Sum(d => (double)d));
            timeline ??= durations;
            Assert.Equal(timeline, durations);
        }
    }

    [Fact]
    public async Task QualityOwnershipDoesNotExposeOtherRenditionsOrChangeLegacyResolution()
    {
        var resolver = new FixtureResolver();
        var legacy = await resolver.ResolveAsync(FixtureResolver.PrimaryId);
        Assert.Equal("180", Heights(Tracks(legacy)));
        Assert.Equal(180, Assert.Single(legacy.Qualities).Height);
        var original = await resolver.ResolveQualityAsync(legacy.VideoId, 180, default);
        Assert.Equal(legacy.Manifest, original.Manifest);
        var alternate = await resolver.ResolveQualityAsync(legacy.VideoId, 90, default);
        var alternateUri = UriOf(Assert.Single(Tracks(alternate), t => t.Attribute("height") is not null));
        var originalUri = UriOf(Assert.Single(Tracks(legacy), t => t.Attribute("height") is not null));
        foreach (var (media, uri) in new[] { (legacy, alternateUri), (original, alternateUri), (alternate, originalUri) })
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(media, uri, 0, 1));
            Assert.Equal("unsafe_resource", error.Code);
        }
        var unknown = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveQualityAsync(legacy.VideoId, 720, default));
        Assert.Equal("unsupported_media", unknown.Code);
        await Assert.ThrowsAsync<PlaybackException>(() => resolver.ResolveQualityAsync("jNQXAC9IVRw", null, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveQualityAsync(legacy.VideoId, 90, new(true)));
    }

    private static XElement[] Tracks(PlaybackMedia media)
    {
        using var stream = new MemoryStream(media.Manifest);
        return XDocument.Load(stream).Descendants(Dash + "Representation").ToArray();
    }

    private static Uri UriOf(XElement track) => new(track.Element(Dash + "BaseURL")!.Value, UriKind.Absolute);

    private static string Heights(XElement[] tracks) => string.Join(',', tracks.Where(t => t.Attribute("height") is not null)
        .Select(t => (int)t.Attribute("height")!).Order());
}
