namespace YourTube.Playback;

public sealed record PlaybackStartupSnapshot(int Completed, long ElapsedMs, IReadOnlyList<long?> StepDurations, bool Failed, bool EndedUnconfirmed)
{
    public const int StepCount = 5;
    public bool IsFinal => Completed == StepCount || Failed || EndedUnconfirmed;
}

// One small in-memory timeline per startup attempt, shared by resolver and native callbacks.
internal sealed class PlaybackStartup
{
    private readonly object gate = new();
    private readonly long?[] started = new long?[PlaybackStartupSnapshot.StepCount + 1];
    private int current;
    private long? stoppedAt;
    private bool failed;
    private (TimeSpan Position, long Elapsed)? previousSample;
    private int forwardSamples;

    public PlaybackStartup() => started[0] = 0;

    public void Record(PlaybackTimingStage stage, long elapsed)
    {
        if (stage == PlaybackTimingStage.NativePlaybackEnded) { Stop(elapsed, isFailure: false); return; }
        Advance(stage switch
        {
            PlaybackTimingStage.CompatibleStreamsFound => 1,
            PlaybackTimingStage.NativeSourceStarted => 2,
            PlaybackTimingStage.NativeSourceCreated => 3,
            PlaybackTimingStage.NativePlayerOpened => 4,
            _ => -1
        }, elapsed);
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
            return new(current, total, durations, failed, stoppedAt is not null && !failed);
        }
    }
}
