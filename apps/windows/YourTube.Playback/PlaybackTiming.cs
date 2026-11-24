using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace YourTube.Playback;

// The resolver and native player share this closed vocabulary; no caller-controlled text.
public enum PlaybackTimingStage
{
    ResolveStarted, ResolveCompleted, ResolveFailed, ResolveCanceled,
    MetadataStarted, MetadataCompleted, StreamManifestStarted, StreamManifestCompleted,
    FirstLeadingReadStarted, FirstLeadingReadCompleted, DashAssemblyCompleted,
    NativeSourceStarted, NativeSourceCreated, NativeSourceFailed, NativeSourceCanceled,
    NativeStartupHintApplied, NativeStartupHintUnavailable,
    NativeFirstReadStarted, NativeFirstSuccessfulRead, NativeReadFailed,
    NativePlayerOpening, NativePlayerAttached, NativePlayerOpened, NativePlayRequested,
    NativeFirstPlaybackProgress, NativeBufferingStarted, NativeBufferingEnded,
    NativeDownloadBitrateChanged, NativePlaybackBitrateChanged,
    NativeDownloadBitrateAtFirstProgress, NativePlaybackBitrateAtFirstProgress,
    NativePlayerFailed, NativeSourceExpired, NativeDisposed,
    ReturnToAutoStarted, ManualQualityStarted, MetadataFromPlayerResponse, CompatibleStreamsFound, NativePlaybackEnded,
    StreamReachabilityStarted
}

public sealed class PlaybackTiming
{
    private readonly PlaybackTimingFile? file;
    private readonly PlaybackStartup? startup;
    private readonly Guid attempt = Guid.NewGuid();
    private readonly long start = Stopwatch.GetTimestamp();
    private static readonly Lazy<PlaybackTimingFile?> localFile = new(() =>
    {
        if (Environment.GetEnvironmentVariable("YOURTUBE_PLAYBACK_TIMING") != "1") return null;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : new(Path.Combine(local, "YourTube", "playback-timing.log"));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Security.SecurityException) { return null; }
    });

    internal PlaybackTiming(PlaybackTimingFile file) => this.file = file;
    private PlaybackTiming(PlaybackTimingFile? file, PlaybackStartup startup) => (this.file, this.startup) = (file, startup);
    public static PlaybackTiming? StartIfEnabled() => localFile.Value is { } file ? new(file) : null;
    public static PlaybackTiming StartForStartup() => new(localFile.Value, new PlaybackStartup());
    public bool IsRecordingFile => file is not null;
    private long ElapsedMs => Stopwatch.GetElapsedTime(start).Ticks / TimeSpan.TicksPerMillisecond;
    public PlaybackStartupSnapshot? ReadStartup() => startup?.Read(ElapsedMs);
    public void ObserveStartupClock(TimeSpan? playingPosition, double rate = 1) => startup?.ObserveClock(playingPosition, ElapsedMs, rate);
    public void FailStartup() => startup?.Fail(ElapsedMs);

    public void Mark(PlaybackTimingStage stage, long? positionMs = null, uint? bitrateBps = null,
        uint? previousBitrateBps = null, int? height = null, int? reasonCode = null)
    {
        if (!Enum.IsDefined(stage)) return;
        var elapsed = ElapsedMs;
        startup?.Record(stage, elapsed);
        file?.Enqueue(new(attempt, DateTimeOffset.UtcNow, elapsed, stage.ToString(), positionMs, bitrateBps, previousBitrateBps, height, reasonCode));
    }
}

internal sealed record PlaybackTimingRow(Guid Attempt, DateTimeOffset Utc, long ElapsedMs, string Stage,
    long? PositionMs, uint? BitrateBps, uint? PreviousBitrateBps, int? Height, int? ReasonCode);

internal sealed class PlaybackTimingFile : IAsyncDisposable
{
    private readonly Channel<PlaybackTimingRow> pending = Channel.CreateBounded<PlaybackTimingRow>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Task writer;
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PlaybackTimingFile(string path, int byteLimit = 512 * 1024) => writer = Task.Run(async () =>
    {
        await foreach (var row in pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var line = JsonSerializer.SerializeToUtf8Bytes(row, json);
                if (line.Length + 1 > byteLimit) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length + line.Length + 1 > byteLimit) stream.SetLength(0);
                stream.Position = stream.Length;
                Span<byte> entry = new byte[line.Length + 1];
                line.CopyTo(entry);
                entry[^1] = (byte)'\n';
                stream.Write(entry);
            }
            catch (IOException) { /* Optional diagnostics must not affect playback. */ }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    });

    internal void Enqueue(PlaybackTimingRow row) => pending.Writer.TryWrite(row);

    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }
}
