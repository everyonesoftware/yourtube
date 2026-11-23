namespace YourTube.App;

// UI-thread state, scoped to one logical playback. Sampling never spans a seek or source handoff.
internal sealed class WatchProgress
{
    private const int MaxPendingRanges = 64;
    private (TimeSpan Position, long Tick)? sample;
    private readonly LinkedList<(TimeSpan Start, TimeSpan End)> pending = new();
    private TimeSpan pendingDuration;
    private int readyCount;
    private bool discontinuity;
    private object? seekSource;
    private int outstandingSeeks;

    public bool IsSeeking => outstandingSeeks > 0;

    public void Reset()
    {
        sample = null;
        pending.Clear();
        pendingDuration = TimeSpan.Zero;
        readyCount = 0;
        discontinuity = false;
    }

    public void BreakSegment()
    {
        sample = null;
        discontinuity = true;
    }

    public void SourceChanged(object? source)
    {
        seekSource = source;
        outstandingSeeks = 0;
        BreakSegment();
    }

    public void BeginSeek()
    {
        outstandingSeeks++;
        BreakSegment();
    }

    public void CompleteSeek(object source)
    {
        if (!ReferenceEquals(source, seekSource) || !IsSeeking) return;
        outstandingSeeks--;
        BreakSegment();
    }

    public TimeSpan? Observe(TimeSpan position, TimeSpan duration, long tick, double rate)
    {
        if (IsSeeking) return null;
        if (duration <= TimeSpan.Zero || position < TimeSpan.Zero || position > duration)
        {
            BreakSegment();
            return null;
        }
        var prior = sample;
        sample = (position, tick);
        if (prior is not { } previous) return null;
        var elapsed = tick - previous.Tick;
        var delta = position - previous.Position;
        if (elapsed <= 0 || elapsed > 2000 || delta < TimeSpan.Zero || !double.IsFinite(rate) || rate <= 0 ||
            delta.TotalMilliseconds > elapsed * rate + 350)
        {
            discontinuity = true;
            return null;
        }
        if (delta == TimeSpan.Zero) return null;
        if (!discontinuity && pending.Last is { } tail && tail.Value.End == previous.Position)
            tail.Value = (tail.Value.Start, position);
        else if (pending.Count < MaxPendingRanges)
            pending.AddLast((previous.Position, position));
        else
        {
            // Keep the oldest unsent ranges when a slow write meets repeated discontinuities.
            discontinuity = true;
            return previous.Position;
        }
        pendingDuration += delta;
        discontinuity = false;
        return previous.Position;
    }

    public (TimeSpan Start, TimeSpan End)? Take(bool final)
    {
        if (!CanTake(final)) return null;
        // Once qualified, every range in the checkpoint remains eligible, even the short ones.
        if (readyCount == 0) readyCount = pending.Count;
        var range = pending.First!.Value;
        pending.RemoveFirst();
        pendingDuration -= range.End - range.Start;
        readyCount--;
        return range;
    }

    public bool CanTake(bool final) => readyCount > 0 || pending.Count == MaxPendingRanges ||
        pendingDuration >= TimeSpan.FromSeconds(final ? 0.5 : 15);

    // A pause may occur while a regular checkpoint is still in flight. Let that
    // drain pick up the newly eligible ranges even if playback resumes first.
    public void FlushAfterCurrentWrite()
    {
        if (pendingDuration >= TimeSpan.FromSeconds(0.5)) readyCount = pending.Count;
    }

    public async Task DrainAsync(bool final, Func<(TimeSpan Start, TimeSpan End), Task<bool>> send, CancellationToken token)
    {
        while (!token.IsCancellationRequested && Take(final) is { } range)
            if (!await send(range)) return;
    }
}
