using System.Text;
using System.Xml.Linq;
using YourTube.Playback;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class QualityProjectionTests
{
    private static readonly XNamespace Dash = "urn:mpeg:dash:schema:mpd:2011";
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(37);
    private static MediaTrack Video(string tag, int height, uint bitrate) =>
        new(ResolverTests.Url(tag), ResolverTests.Track().Size, "avc1.64001f", bitrate, height == 360 ? 640 : 1280, height);

    private static Task<PlaybackMedia> Assemble(PlaybackResolver resolver, params MediaTrack[] tracks) =>
        resolver.AssembleAsync("jNQXAC9IVRw", "Title", "Author", "Description", Duration, tracks);

    private static XDocument Parse(PlaybackMedia media) => XDocument.Parse(Encoding.UTF8.GetString(media.Manifest));
    private static XElement Set(XDocument manifest, string kind) =>
        manifest.Descendants(Dash + "AdaptationSet").Single(s => (string?)s.Attribute("contentType") == kind);

    [Fact]
    public async Task ExactHeightUsesHighestValidatedBitrateAndPreservesOriginalAudioAndMetadata()
    {
        var handler = ResolverTests.Network();
        using var resolver = new PlaybackResolver(handler);
        var best = Video("video-best", 360, 900_000);
        var lower = Video("video-lower", 360, 500_000);
        var taller = Video("video-taller", 720, 2_000_000);
        var invalid = new MediaTrack(ResolverTests.Url("audio-invalid-video"), ResolverTests.Track(true).Size, "avc1.64001f", 3_000_000, 640, 360);
        var original = await Assemble(resolver, taller, best, lower, invalid, ResolverTests.Track(true));
        int calls = handler.Calls;

        Assert.Same(original, resolver.ForQuality(original, null));
        var projected = resolver.ForQuality(original, 360);
        Assert.NotSame(original, projected);
        Assert.Equal(new[] { new PlaybackQuality(720, 2_000_000), new(360, 900_000), new(360, 500_000) }, original.Qualities);
        Assert.Equal(new PlaybackQuality(360, 900_000), Assert.Single(projected.Qualities));
        Assert.Equal((original.VideoId, original.Title, original.Author, original.Description, original.Duration, original.ExpiresAt, original.ManifestUri),
            (projected.VideoId, projected.Title, projected.Author, projected.Description, projected.Duration, projected.ExpiresAt, projected.ManifestUri));

        var full = Parse(original);
        var filtered = Parse(projected);
        var originalVideos = Set(full, "video").Elements(Dash + "Representation").ToArray();
        Assert.Equal(3, originalVideos.Length);
        Assert.True(XNode.DeepEquals(originalVideos[1], Assert.Single(Set(filtered, "video").Elements(Dash + "Representation"))));
        Assert.True(XNode.DeepEquals(Set(full, "audio"), Set(filtered, "audio")));
        foreach (var omitted in new[] { originalVideos[0], originalVideos[2] }) omitted.Remove();
        Assert.True(XNode.DeepEquals(full, filtered));
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task ProjectionUsesTrustedAssemblyAndOwnsOnlyRetainedResources()
    {
        var handler = ResolverTests.Network();
        using var resolver = new PlaybackResolver(handler);
        var best = Video("video-best", 360, 900_000);
        var lower = Video("video-lower", 360, 500_000);
        var taller = Video("video-taller", 720, 2_000_000);
        var audio = ResolverTests.Track(true);
        var original = await Assemble(resolver, best, lower, taller, audio);
        var expected = Set(Parse(original), "video").Elements(Dash + "Representation").First();
        Array.Clear(original.Manifest);
        int calls = handler.Calls;

        var projected = resolver.ForQuality(original, 360);
        Assert.True(XNode.DeepEquals(expected, Assert.Single(Set(Parse(projected), "video").Elements(Dash + "Representation"))));
        foreach (var omitted in new[] { lower, taller, Video("foreign", 360, 900_000) })
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => resolver.ReadResourceAsync(projected, omitted.Uri, 10, 2));
            Assert.Equal("unsafe_resource", error.Code);
        }
        Assert.Equal(calls, handler.Calls);
        Assert.Equal("video/mp4", (await resolver.ReadResourceAsync(projected, best.Uri, 10, 2)).ContentType);
        Assert.Equal("audio/mp4", (await resolver.ReadResourceAsync(projected, audio.Uri, 10, 2)).ContentType);
        Assert.Equal("video/mp4", (await resolver.ReadResourceAsync(original, taller.Uri, 10, 2)).ContentType);
    }

    [Fact]
    public async Task RejectsUnavailableHeightAndForeignOrForgedSourcesWithoutFetching()
    {
        var handler = ResolverTests.Network();
        using var resolver = new PlaybackResolver(handler);
        using var another = new PlaybackResolver(ResolverTests.Network());
        var invalid = new MediaTrack(ResolverTests.Url("audio-invalid-video"), ResolverTests.Track(true).Size, "avc1.64001f", 2_000_000, 1280, 720);
        var original = await Assemble(resolver, Video("video", 360, 900_000), invalid, ResolverTests.Track(true));
        var forged = new PlaybackMedia(original.VideoId, original.Title, original.Author, original.Description, original.Duration,
            original.ExpiresAt, original.Manifest, original.ManifestUri, original.Qualities);
        int calls = handler.Calls;

        Assert.Equal("unsupported_quality", Assert.Throws<PlaybackException>(() => resolver.ForQuality(original, 361)).Code);
        Assert.Equal("unsupported_quality", Assert.Throws<PlaybackException>(() => resolver.ForQuality(original, 0)).Code);
        Assert.Equal("unsupported_quality", Assert.Throws<PlaybackException>(() => resolver.ForQuality(original, 720)).Code);
        foreach (int? height in new int?[] { null, 360 })
        {
            Assert.Equal("unsafe_resource", Assert.Throws<PlaybackException>(() => another.ForQuality(original, height)).Code);
            Assert.Equal("unsafe_resource", Assert.Throws<PlaybackException>(() => resolver.ForQuality(forged, height)).Code);
        }
        Assert.Equal(calls, handler.Calls);
    }
}
