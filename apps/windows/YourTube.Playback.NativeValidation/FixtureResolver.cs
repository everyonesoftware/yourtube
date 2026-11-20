using System.Runtime.CompilerServices;
using YourTube.Playback;

namespace YourTube.Playback.NativeValidation;

// Only the validation executable can resolve these synthetic tracks. No file path or network
// transport is derived from a request; even AMS gets only bytes from embedded resources.
internal sealed class FixtureResolver : IPlaybackResolver
{
    // Match the production controller's video ID format without resolving a public video.
    internal const string PrimaryId = "yt-fixture0";
    internal const string PendingId = "yt-fixture1";
    internal const string ReplacementId = "yt-fixture2";
    private static readonly Uri ManifestUri = new("https://fixture.invalid/manifest.mpd");
    private readonly byte[] manifest = Load("manifest.mpd");
    private readonly byte[] video = Load("video.mp4");
    private readonly byte[] audio = Load("audio.mp4");
    private readonly ConditionalWeakTable<PlaybackMedia, object> owned = new();

    public Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (videoId is not (PrimaryId or PendingId or ReplacementId))
            throw new PlaybackException("unavailable", "Unknown controlled playback fixture.");
        var media = new PlaybackMedia(videoId, "Controlled DASH fixture", "YourTube validation", "",
            TimeSpan.FromSeconds(18), DateTimeOffset.MaxValue, (byte[])manifest.Clone(), ManifestUri, [new(180, 210_000)]);
        owned.Add(media, new object());
        return Task.FromResult(media);
    }

    public Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (media is null || uri is null || !owned.TryGetValue(media, out _) || !uri.IsAbsoluteUri) throw UnsafeResource();
        var (bytes, contentType) = uri.AbsoluteUri switch
        {
            "https://fixture.invalid/video.mp4" => (video, "video/mp4"),
            "https://fixture.invalid/audio.mp4" => (audio, "audio/mp4"),
            _ => throw UnsafeResource()
        };
        var start = offset ?? 0;
        var size = (ulong)bytes.Length;
        if (start >= size) throw InvalidRange();
        var count = length ?? size - start;
        if (count == 0 || count > size - start) throw InvalidRange();
        return Task.FromResult(new PlaybackResource(bytes.AsSpan((int)start, (int)count).ToArray(), contentType));
    }

    private static byte[] Load(string name)
    {
        using var stream = typeof(FixtureResolver).Assembly.GetManifestResourceStream("Fixture." + name)
            ?? throw new InvalidOperationException("Controlled media resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static PlaybackException UnsafeResource() => new("unsafe_resource", "The controlled fixture does not own this resource.");
    private static PlaybackException InvalidRange() => new("unsupported_media", "The controlled fixture byte range is invalid.");
}
