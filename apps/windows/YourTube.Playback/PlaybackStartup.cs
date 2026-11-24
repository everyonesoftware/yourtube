namespace YourTube.Playback;

public sealed record PlaybackStartupSnapshot(int Completed, long ElapsedMs, IReadOnlyList<long?> StepDurations, bool Failed, bool EndedUnconfirmed)
{
    public const int StepCount = 5;
    public bool IsFinal => Completed == StepCount || Failed || EndedUnconfirmed;
    public StreamDiscoverySnapshot Discovery { get; init; } = new(null, new long?[StreamDiscoverySnapshot.StepCount]);
}

public enum StreamDiscoveryStep { StreamList, Reachability, Compatibility }
public readonly record struct StreamCheckProgress(int Checked, int Total);
public enum ReachabilityMode { Sequential, Parallel, Mixed }
public enum ReachabilityFallback
{
    None, ConfiguredSequential, MissingLength, ExternalManifest, CipheredUrl, UnpredictableRequests, RequestMismatch,
    NoPlayerResponse, NoStreamList, UnreadablePlayerResponse, PlayerNotConfirmedPlayable, InvalidStreamEntry,
    UnsupportedStreamUrl, ExistingRangeParameter, DuplicateStreamUrl, NoDirectCandidates, CandidateCountOutsideWindow,
    MissingDirectUrl, InvalidFormatId, DuplicateFormatId, InvalidLength, ExistingLanguageParameter
}
public sealed record StreamReachabilityDiagnostics(int PeakInFlight, ReachabilityMode Mode, ReachabilityFallback Fallback,
    int TailRequests, int SizeRequests, int OtherRequests, int RedirectHops, int ProbeRetries, int PlayerRetries,
    long? MedianCheckMs, long? SlowestCheckMs, long? FinalQuarterMs)
{
    public int PrefetchedTailRequests { get; init; }
    public int? RepeatedFormatEntriesInPlan { get; init; }
}
public sealed record StreamDiscoverySnapshot(StreamDiscoveryStep? Current, IReadOnlyList<long?> Durations)
{
    public const int StepCount = 3;
    public StreamCheckProgress? ReachabilityProgress { get; init; }
    public StreamReachabilityDiagnostics? ReachabilityDiagnostics { get; init; }
}

// One small in-memory timeline per startup attempt, shared by resolver and native callbacks.
internal sealed class PlaybackStartup
{
    private readonly object gate = new();
    private readonly long?[] started = new long?[PlaybackStartupSnapshot.StepCount + 1];
    private readonly long?[] discoveryStarted = new long?[StreamDiscoverySnapshot.StepCount];
    private StreamDiscoveryStep? discoveryStep;
    private StreamCheckProgress? reachabilityProgress;
    private StreamReachabilityDiagnostics? reachabilityDiagnostics;
    private int current;
    private long? stoppedAt;
    private bool failed;
    private (TimeSpan Position, long Elapsed)? previousSample;
    private int forwardSamples;

    public PlaybackStartup() => started[0] = 0;

    public void RecordReachabilityProgress(StreamCheckProgress? progress)
    {
        lock (gate)
            if (stoppedAt is null && current == 0 && discoveryStep != StreamDiscoveryStep.Compatibility)
                reachabilityProgress = progress;
    }

    public void RecordReachabilityDiagnostics(StreamReachabilityDiagnostics diagnostics)
    {
        lock (gate)
            if (stoppedAt is null && current == 0 && discoveryStep != StreamDiscoveryStep.Compatibility)
                reachabilityDiagnostics = diagnostics;
    }

    public void Record(PlaybackTimingStage stage, long elapsed)
    {
        if (stage == PlaybackTimingStage.NativePlaybackEnded) { Stop(elapsed, isFailure: false); return; }
        lock (gate)
        {
            if (stoppedAt is not null) return;
            if (current == 0)
            {
                var step = stage switch
                {
                    PlaybackTimingStage.StreamManifestStarted => StreamDiscoveryStep.StreamList,
                    PlaybackTimingStage.StreamReachabilityStarted => StreamDiscoveryStep.Reachability,
                    PlaybackTimingStage.StreamManifestCompleted => StreamDiscoveryStep.Compatibility,
                    _ => (StreamDiscoveryStep?)null
                };
                if (step is { } next && (discoveryStep is null || next > discoveryStep))
                {
                    discoveryStarted[(int)next] = Math.Max(elapsed, discoveryStep is { } prior ? discoveryStarted[(int)prior]!.Value : 0);
                    discoveryStep = next;
                }
            }
            Advance(stage switch
            {
                PlaybackTimingStage.CompatibleStreamsFound => 1,
                PlaybackTimingStage.NativeSourceStarted => 2,
                PlaybackTimingStage.NativeSourceCreated => 3,
                PlaybackTimingStage.NativePlayerOpened => 4,
                _ => -1
            }, elapsed);
        }
    }

    private void Advance(int next, long elapsed)
    {
        if (next < 0) return;
        lock (gate)
        {
            if (stoppedAt is not null || next <= current) return;
            started[next] = Math.Max(started[current]!.Value, elapsed);
            current = next;
        }
    }

    public void ObserveClock(TimeSpan? playingPosition, long elapsed, double rate = 1)
    {
        lock (gate)
        {
            if (current != PlaybackStartupSnapshot.StepCount - 1 || stoppedAt is not null) return;
            var previous = previousSample;
            previousSample = playingPosition is { } sample ? (sample, elapsed) : null;
            if (playingPosition is not { } position || previous is not { } prior)
            {
                forwardSamples = 0;
                return;
            }
            var delta = (position - prior.Position).TotalMilliseconds;
            var interval = elapsed - prior.Elapsed;
            if (interval <= 0 || interval > 2000 || !double.IsFinite(rate) || rate <= 0 || delta < 0 || delta > interval * rate + 250)
            {
                forwardSamples = 0;
                return;
            }
            // One position jump is not playback. Don't depend on a separate seek-complete event.
            if (delta > 0 && ++forwardSamples >= 2)
                Advance(PlaybackStartupSnapshot.StepCount, elapsed);
        }
    }

    public void Fail(long elapsed) => Stop(elapsed, isFailure: true);

    private void Stop(long elapsed, bool isFailure)
    {
        lock (gate)
        {
            if (current == PlaybackStartupSnapshot.StepCount || stoppedAt is not null) return;
            stoppedAt = Math.Max(started[current]!.Value, elapsed);
            failed = isFailure;
        }
    }

    public PlaybackStartupSnapshot Read(long elapsed)
    {
        lock (gate)
        {
            long total = stoppedAt ?? (current == PlaybackStartupSnapshot.StepCount ? started[current]!.Value : Math.Max(started[current]!.Value, elapsed));
            var durations = new long?[PlaybackStartupSnapshot.StepCount];
            for (int i = 0; i < durations.Length; i++)
                if (started[i] is long start) durations[i] = i == current ? total - start : started[i + 1] - start;
            var discovery = new long?[StreamDiscoverySnapshot.StepCount];
            long end = started[1] ?? total;
            for (int i = discovery.Length - 1; i >= 0; i--)
                if (discoveryStarted[i] is long start)
                {
                    discovery[i] = Math.Max(0, end - start);
                    end = start;
                }
            return new(current, total, durations, failed, stoppedAt is not null && !failed)
            {
                Discovery = new(current == 0 ? discoveryStep : null, discovery)
                    { ReachabilityProgress = reachabilityProgress, ReachabilityDiagnostics = reachabilityDiagnostics }
            };
        }
    }
}
