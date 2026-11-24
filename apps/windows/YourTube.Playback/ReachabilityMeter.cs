namespace YourTube.Playback;

// Aggregates only numeric measurements and fixed classifications. No request identities.
internal sealed class ReachabilityMeter(PlaybackTiming timing, int limit, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SortedDictionary<long, int> samples = new();
    private const int SampleLimit = 256;
    private int active, peak, tails, sizes, other, redirects, probeRetries, playerRequests, sampleCount;
    private bool usedSerial;
    private ReachabilityFallback reason;
    private int prefetchedTails;
    private int? repeatedFormatEntriesInPlan;
    private long? threeQuartersAt, finalQuarter;

    internal static bool IsTail(Uri uri) => uri.Query.StartsWith("?range=", StringComparison.Ordinal) ||
        uri.Query.Contains("&range=", StringComparison.Ordinal);

    internal long Dispatch(HttpMethod method, bool tail, bool speculative, bool retry, ReachabilityFallback fallback)
    {
        lock (gate)
        {
            if (method == HttpMethod.Head) sizes++;
            else if (tail) tails++;
            else other++;
            if (speculative && tail) prefetchedTails++;
            if (retry) probeRetries++;
            if (!speculative)
            {
                usedSerial = true;
                if (reason == ReachabilityFallback.None) reason = limit == 1 ? ReachabilityFallback.ConfiguredSequential : fallback;
            }
            peak = Math.Max(peak, ++active);
            Publish();
            return time.GetTimestamp();
        }
    }

    internal void Complete(long started, bool sample)
    {
        lock (gate)
        {
            active--;
            if (sample && sampleCount <= SampleLimit && ++sampleCount <= SampleLimit)
            {
                long elapsed = time.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;
                samples[elapsed] = samples.GetValueOrDefault(elapsed) + 1;
            }
            Publish();
        }
    }

    internal void RedirectDispatched() { lock (gate) { redirects++; Publish(); } }
    internal void PlayerDispatched() { lock (gate) { playerRequests++; Publish(); } }

    internal void CandidateProgress(StreamCheckProgress? progress)
    {
        lock (gate)
        {
            if (progress is not { Checked: > 0 } known)
                threeQuartersAt = finalQuarter = null;
            else
            {
                if (known.Checked * 4L >= known.Total * 3L) threeQuartersAt ??= time.GetTimestamp();
                if (known.Checked == known.Total && known.Total >= 4 && threeQuartersAt is long at)
                    finalQuarter = time.GetElapsedTime(at).Ticks / TimeSpan.TicksPerMillisecond;
            }
            Publish();
        }
    }

    internal void RecordPlan(int? repeatedFormatEntries)
    {
        lock (gate) repeatedFormatEntriesInPlan = repeatedFormatEntries;
    }

    private long SampleAt(int index)
    {
        foreach (var (elapsed, count) in samples)
        {
            if (index < count) return elapsed;
            index -= count;
        }
        return 0;
    }

    private void Publish()
    {
        if (peak == 0 && playerRequests <= 1) return;
        bool hasSamples = sampleCount is > 0 and <= SampleLimit;
        var mode = peak <= 1 ? ReachabilityMode.Sequential : usedSerial ? ReachabilityMode.Mixed : ReachabilityMode.Parallel;
        timing.RecordReachabilityDiagnostics(new(peak, mode, reason, tails, sizes, other, redirects,
            probeRetries, Math.Max(0, playerRequests - 1),
            hasSamples ? (SampleAt((sampleCount - 1) / 2) + SampleAt(sampleCount / 2)) / 2 : null,
            hasSamples ? samples.Last().Key : null, finalQuarter)
        {
            PrefetchedTailRequests = prefetchedTails,
            RepeatedFormatEntriesInPlan = repeatedFormatEntriesInPlan
        });
    }
}
