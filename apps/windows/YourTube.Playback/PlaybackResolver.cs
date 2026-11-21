using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace YourTube.Playback;
internal sealed class MediaTrack(Uri uri, ulong size, string codec, uint bitrate, int width, int height)
{
    internal Uri Uri { get; } = uri; internal ulong Size { get; } = size; internal string Codec { get; } = codec; internal uint Bitrate { get; } = bitrate; internal int Width { get; } = width; internal int Height { get; } = height;
    public override string ToString() => "MediaTrack [redacted]";
}
public sealed class PlaybackResolver : IPlaybackResolver, IDisposable
{
    private const ulong MaximumRead = 16 * 1024 * 1024;
    private const ulong InitialHeaderRead = 16 * 1024;
    private const ulong HeaderRead = 1024 * 1024;
    private static readonly XNamespace Dash = "urn:mpeg:dash:schema:mpd:2011";
    private sealed record OwnedSource(XDocument Manifest, Dictionary<string, MediaTrack> Tracks);
    private readonly HttpClient http;
    private readonly YoutubeClient client;
    private readonly ConditionalWeakTable<PlaybackMedia, OwnedSource> ownership = new();
    private bool disposed;
    public PlaybackResolver() : this(CreatePublicHandler()) { }
    internal static HttpClientHandler CreatePublicHandler() => new() { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None };
    // Injected handlers must not auto-redirect or attach credentials. Intended for deterministic tests.
    public PlaybackResolver(HttpMessageHandler handler)
    {
        http = new HttpClient(new PublicTransport(handler)) { Timeout = TimeSpan.FromSeconds(40) };
        client = new YoutubeClient(http);
    }
    public Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken = default) =>
        ResolveAsync(videoId, cancellationToken, null);

    public async Task<PlaybackMedia> ResolveAsync(string videoId, CancellationToken cancellationToken, PlaybackTiming? timing)
    {
        try
        {
            CheckDisposed();
            if (!Regex.IsMatch(videoId ?? "", "\\A[A-Za-z0-9_-]{11}\\z", RegexOptions.CultureInvariant)) throw new PlaybackException("invalid_video", "The video identity is invalid.");
            timing?.Mark(PlaybackTimingStage.MetadataStarted);
            var metadata = await client.Videos.GetAsync(videoId!, cancellationToken);
            timing?.Mark(PlaybackTimingStage.MetadataCompleted);
            if (metadata.Duration is not { } duration || duration <= TimeSpan.Zero) throw Unsupported();
            timing?.Mark(PlaybackTimingStage.StreamManifestStarted);
            var manifest = await client.Videos.Streams.GetManifestAsync(videoId!, cancellationToken);
            timing?.Mark(PlaybackTimingStage.StreamManifestCompleted);
            var videos = manifest.GetVideoOnlyStreams().Where(s => s.Container.Name == "mp4" && Regex.IsMatch(s.VideoCodec, PlayerStreamFilter.AvcPattern) && s.VideoResolution.Height is > 0 and <= PlayerStreamFilter.MaxHeight && s.VideoResolution.Width <= PlayerStreamFilter.MaxWidth).OrderBy(s => s.VideoResolution.Height).ThenBy(s => s.Bitrate.BitsPerSecond).ToArray();
            var audio = manifest.GetAudioOnlyStreams().Where(s => s.Container.Name == "mp4" && s.AudioCodec == PlayerStreamFilter.AacCodec && s.IsAudioLanguageDefault != false).OrderByDescending(s => s.IsAudioLanguageDefault == true).ThenByDescending(s => s.Bitrate.BitsPerSecond).FirstOrDefault();
            if (videos.Length == 0 || audio is null) throw Unsupported();
            var tracks = videos.Select(s => new MediaTrack(new Uri(s.Url), checked((ulong)s.Size.Bytes), s.VideoCodec, checked((uint)s.Bitrate.BitsPerSecond), s.VideoResolution.Width, s.VideoResolution.Height)).ToList();
            tracks.Add(new(new Uri(audio.Url), checked((ulong)audio.Size.Bytes), audio.AudioCodec, checked((uint)audio.Bitrate.BitsPerSecond), 0, 0));
            return await AssembleAsync(videoId!, metadata.Title, metadata.Author.ChannelTitle, metadata.Description, duration, tracks, cancellationToken, timing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("unavailable", "This public video could not be resolved for native playback."); }
    }
    internal async Task<PlaybackMedia> AssembleAsync(string id, string title, string author, string description, TimeSpan duration, IReadOnlyList<MediaTrack> tracks, CancellationToken token = default, PlaybackTiming? timing = null)
    {
        CheckDisposed();
        if (duration <= TimeSpan.Zero || tracks.Count > 33) throw Unsupported();
        var ns = Dash;
        var video = new XElement(ns + "AdaptationSet", new XAttribute("id", "1"), new XAttribute("contentType", "video"), new XAttribute("mimeType", "video/mp4"));
        var audio = new XElement(ns + "AdaptationSet", new XAttribute("id", "2"), new XAttribute("contentType", "audio"), new XAttribute("mimeType", "audio/mp4"));
        var owned = new Dictionary<string, MediaTrack>(StringComparer.Ordinal); var qualities = new List<PlaybackQuality>();
        DateTimeOffset expiry = DateTimeOffset.MaxValue; int index = 0;
        bool firstRead = true;
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            if (!PublicTransport.IsCdn(track.Uri) || track.Size < 8 || track.Size > long.MaxValue || track.Bitrate == 0 || track.Height < 0 || track.Height > PlayerStreamFilter.MaxHeight || track.Width < 0 || track.Width > PlayerStreamFilter.MaxWidth || (track.Height == 0 ? track.Codec != PlayerStreamFilter.AacCodec : !Regex.IsMatch(track.Codec, PlayerStreamFilter.AvcPattern))) throw Unsupported();
            var expires = Expiry(track.Uri);
            if (expires <= DateTimeOffset.UtcNow) throw Expired();
            Mp4Index ranges;
            try
            {
                if (firstRead) timing?.Mark(PlaybackTimingStage.FirstLeadingReadStarted);
                var bytes = await FetchAsync(track, 0, Math.Min(InitialHeaderRead, track.Size), token);
                if (firstRead) timing?.Mark(PlaybackTimingStage.FirstLeadingReadCompleted);
                firstRead = false;
                string sampleType = track.Height > 0 ? "avc1" : "mp4a";
                ulong limit = Math.Min(HeaderRead, track.Size);
                ranges = Mp4Header.ParsePrefix(bytes, track.Size, limit, sampleType)
                    ?? Mp4Header.Parse(await FetchAsync(track, 0, limit, token), track.Size, sampleType);
            }
            catch (PlaybackException e) when (e.Code == "unsupported_media") { continue; }
            expiry = expires < expiry ? expires : expiry;
            owned.Add(track.Uri.AbsoluteUri, track);
            var representation = new XElement(ns + "Representation", new XAttribute("id", (++index).ToString(CultureInfo.InvariantCulture)), new XAttribute("bandwidth", track.Bitrate), new XAttribute("codecs", track.Codec), new XElement(ns + "BaseURL", track.Uri.AbsoluteUri), new XElement(ns + "SegmentBase", new XAttribute("indexRange", $"{ranges.IndexStart}-{ranges.IndexEnd}"), new XAttribute("indexRangeExact", "true"), new XAttribute("timescale", ranges.Timescale), new XAttribute("presentationTimeOffset", ranges.PresentationTimeOffset), new XElement(ns + "Initialization", new XAttribute("range", $"0-{ranges.InitEnd}"))));
            if (track.Height > 0) { representation.Add(new XAttribute("width", track.Width), new XAttribute("height", track.Height)); video.Add(representation); qualities.Add(new(track.Height, track.Bitrate)); }
            else audio.Add(representation);
        }
        if (!video.HasElements || !audio.HasElements) throw Unsupported();
        var mpd = new XDocument(new XElement(ns + "MPD", new XAttribute("type", "static"), new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"), new XAttribute("mediaPresentationDuration", XmlConvert.ToString(duration)), new XAttribute("minBufferTime", "PT1.5S"), new XElement(ns + "Period", new XAttribute("start", "PT0S"), new XAttribute("duration", XmlConvert.ToString(duration)), video, audio)));
        var media = new PlaybackMedia(id, title, author, description, duration, expiry, Serialize(mpd), new("https://www.youtube.com/yourtube-playback.mpd"), qualities.AsReadOnly());
        ownership.Add(media, new(mpd, owned));
        timing?.Mark(PlaybackTimingStage.DashAssemblyCompleted);
        return media;
    }

    /// <summary>Returns Auto unchanged, or a DASH source limited to one validated video rendition at the exact height.</summary>
    public PlaybackMedia ForQuality(PlaybackMedia media, int? height)
    {
        CheckDisposed();
        if (media is null || !ownership.TryGetValue(media, out var source)) throw PublicTransport.Unsafe();
        if (height is null) return media;

        // Never reparse the caller-visible, mutable MPD bytes or use them to grant resource access.
        var mpd = new XDocument(source.Manifest);
        var videos = mpd.Descendants(Dash + "AdaptationSet")
            .Single(set => (string?)set.Attribute("contentType") == "video")
            .Elements(Dash + "Representation").ToArray();
        var selected = videos.Where(r => (int?)r.Attribute("height") == height)
            .MaxBy(r => (uint)r.Attribute("bandwidth")!);
        if (selected is null) throw new PlaybackException("unsupported_quality", "The requested video quality is unavailable.");

        foreach (var video in videos)
            if (video != selected) video.Remove();

        var tracks = mpd.Descendants(Dash + "BaseURL").ToDictionary(url => url.Value, url => source.Tracks[url.Value], StringComparer.Ordinal);
        var qualities = Array.AsReadOnly(new[] { new PlaybackQuality(height.Value, (uint)selected.Attribute("bandwidth")!) });
        var projected = new PlaybackMedia(media.VideoId, media.Title, media.Author, media.Description, media.Duration,
            media.ExpiresAt, Serialize(mpd), media.ManifestUri, qualities);
        ownership.Add(projected, new(mpd, tracks));
        return projected;
    }

    private static byte[] Serialize(XDocument mpd) => Encoding.UTF8.GetBytes(mpd.ToString(SaveOptions.DisableFormatting));

    public async Task<PlaybackResource> ReadResourceAsync(PlaybackMedia media, Uri uri, ulong? offset, ulong? length, CancellationToken cancellationToken = default)
    {
        try
        {
            CheckDisposed();
            if (media is null || uri is null || !PublicTransport.IsCdn(uri) || !ownership.TryGetValue(media, out var owned) || !owned.Tracks.TryGetValue(uri.AbsoluteUri, out var track)) throw PublicTransport.Unsafe();
            if (media.ExpiresAt <= DateTimeOffset.UtcNow || Expiry(track.Uri) <= DateTimeOffset.UtcNow) throw Expired();
            ulong start = offset ?? 0;
            if (start >= track.Size) throw InvalidRange();
            ulong count = length ?? track.Size - start;
            return new(await FetchAsync(track, start, count, cancellationToken), track.Height > 0 ? "video/mp4" : "audio/mp4");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("network_error", "The media resource could not be read."); }
    }
    private async Task<byte[]> FetchAsync(MediaTrack track, ulong start, ulong count, CancellationToken token)
    {
        if (count == 0 || count > MaximumRead || start >= track.Size || count > track.Size - start) throw InvalidRange();
        if (!PublicTransport.IsCdn(track.Uri)) throw PublicTransport.Unsafe();
        // Googlevideo honors this query range even on CDN nodes that ignore Range headers.
        if (Query(track.Uri, "range") is not null) throw PublicTransport.Unsafe();
        ulong end = start + count - 1;
        var target = new Uri(track.Uri.AbsoluteUri + (track.Uri.Query.Length == 0 ? "?" : "&") + "range=" + start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) throw new PlaybackException("media_unavailable", "The public media resource is unavailable.");
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.OK && range is not null) throw InvalidRange();
        if (response.StatusCode == HttpStatusCode.PartialContent && (range?.Unit != "bytes" || range.From != (long)start || range.To != (long)end || range.Length != (long)track.Size)) throw InvalidRange();
        if (response.Content.Headers.ContentLength is { } declared && (ulong)declared != count) throw InvalidRange();
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if ((ulong)bytes.Length != count) throw InvalidRange();
        return bytes;
    }
    private static string? Query(Uri uri, string key)
    {
        string? value = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        { var pair = part.Split('=', 2); if (Uri.UnescapeDataString(pair[0]) != key) continue; if (value is not null || pair.Length != 2) throw PublicTransport.Unsafe(); value = Uri.UnescapeDataString(pair[1]); }
        return value;
    }
    private static DateTimeOffset Expiry(Uri uri)
    {
        if (!long.TryParse(Query(uri, "expire"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 60 || seconds > 253402300799) throw Unsupported();
        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(-60);
    }
    private static PlaybackException Unsupported() => new("unsupported_media", "This video has no supported H.264/AAC indexed public media.");
    private static PlaybackException InvalidRange() => new("invalid_range", "The media byte range is invalid or exceeds the read limit.");
    private static PlaybackException Expired() => new("expired", "The public media has expired. Retry playback to resolve it again.");
    private void CheckDisposed() { if (disposed) throw new PlaybackException("disposed", "The playback resolver has been closed."); }
    public void Dispose() { if (disposed) return; disposed = true; ownership.Clear(); client.Dispose(); http.Dispose(); }
}
