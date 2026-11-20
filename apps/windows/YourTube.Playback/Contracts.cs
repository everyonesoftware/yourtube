namespace YourTube.Playback;

public interface IPlaybackResolver
{
    Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken = default);
    Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken cancellationToken = default);
}
public sealed record PlaybackQuality(int Height, uint Bitrate);
public sealed class PlaybackMedia(string videoId, string title, string author, string description, TimeSpan duration, DateTimeOffset expiresAt, byte[] manifest, Uri manifestUri, IReadOnlyList<PlaybackQuality> qualities)
{
    public string VideoId { get; } = videoId;
    public string Title { get; } = title;
    public string Author { get; } = author;
    public string Description { get; } = description;
    public TimeSpan Duration { get; } = duration;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public byte[] Manifest { get; } = manifest;
    public Uri ManifestUri { get; } = manifestUri;
    public IReadOnlyList<PlaybackQuality> Qualities { get; } = qualities;
    public override string ToString() => "PlaybackMedia [redacted]";
}
public sealed class PlaybackResource(byte[] bytes, string contentType)
{
    public byte[] Bytes { get; } = bytes;
    public string ContentType { get; } = contentType;
    public override string ToString() => "PlaybackResource";
}
public sealed class PlaybackException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
internal readonly record struct Mp4Index(ulong InitEnd, ulong IndexStart, ulong IndexEnd, uint Timescale = 1, ulong PresentationTimeOffset = 0);
