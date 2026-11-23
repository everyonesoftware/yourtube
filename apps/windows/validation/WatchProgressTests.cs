using YourTube.App;

internal static class WatchProgressTests
{
    public static void Run()
    {
        SeparateRangesCheckpointInOrder();
        WaitForCurrentSourceSeeks();
        DrainWhilePausingAsync().GetAwaiter().GetResult();
        var tracker = new WatchProgress();
        var duration = At(180);
        StateTests.Verify(tracker.Observe(At(0), duration, 0, 1) is null, "a clock sample by itself does not count as playback");
        for (int i = 1; i < 15; i++) tracker.Observe(At(i), duration, i * 1000, 1);
        StateTests.Verify(tracker.Take(final: false) is null, "no checkpoint before 15 seconds of observed playback");
        tracker.Observe(At(15), duration, 15000, 1);
        StateTests.Verify(tracker.Take(final: false) == (At(0), At(15)), "first checkpoint covers only observed advancement");
        StateTests.Verify(tracker.Take(final: true) is null, "no duplicate checkpoint on immediate pause");

        tracker.Observe(At(16), duration, 16000, 1);
        StateTests.Verify(tracker.Observe(At(90), duration, 16150, 1) is null, "a forward jump is not counted as watched");
        tracker.Observe(At(91), duration, 17150, 1);
        tracker.BreakSegment();
        StateTests.Verify(tracker.Take(final: true) == (At(15), At(16)), "pause preserves the watched range before a seek");
        StateTests.Verify(tracker.Take(final: true) == (At(90), At(91)), "pause also flushes the actual watched range after a seek");
        StateTests.Verify(tracker.Take(final: true) is null, "pause does not send the skipped gap or repeat either range");

        tracker.Observe(At(91), duration, 18000, 2);
        tracker.Observe(At(93), duration, 19000, 2);
        StateTests.Verify(tracker.Take(final: true) == (At(91), At(93)), "faster playback counts its actual media range");
        tracker.BreakSegment();
        tracker.Observe(At(92.75), duration, 20000, 1);
        tracker.Observe(At(93.75), duration, 21000, 1);
        StateTests.Verify(tracker.Take(final: true) == (At(92.75), At(93.75)), "quality handoff begins with a fresh sample");
        tracker.Observe(At(94.75), duration, 22000, 1);
        tracker.Reset();
        StateTests.Verify(tracker.Take(final: true) is null, "retiring a playback discards its pending range");
        Console.WriteLine("PASS: watched ranges checkpoint and flush without crossing seeks, handoffs or playback lifetimes.");
    }

    private static void SeparateRangesCheckpointInOrder()
    {
        var tracker = new WatchProgress();
        var duration = At(180);
        for (int i = 0; i <= 14; i++) tracker.Observe(At(i), duration, i * 1000, 1);
        tracker.BreakSegment(); // Buffering ends the first segment.
        tracker.Observe(At(14), duration, 16_000, 1);
        tracker.Observe(At(14.15), duration, 16_150, 1);
        StateTests.Verify(tracker.Take(final: false) is null, "short post-buffering playback is not itself a checkpoint");
        tracker.BreakSegment(); // Playback resumes at a different point, with nothing watched in between.
        tracker.Observe(At(90), duration, 17_000, 1);
        tracker.Observe(At(90.85), duration, 17_850, 1);
        StateTests.Verify(tracker.Take(final: false) == (At(0), At(14)), "14 seconds before buffering survive the new sample and qualify cumulatively");
        StateTests.Verify(tracker.Take(final: false) == (At(14), At(14.15)), "the qualified checkpoint retains the short post-buffering range");
        StateTests.Verify(tracker.Take(final: false) == (At(90), At(90.85)), "report the final observed range without spanning the jump");
        StateTests.Verify(tracker.Take(final: true) is null, "draining a checkpoint consumes it exactly once");
    }

    private static void WaitForCurrentSourceSeeks()
    {
        var tracker = new WatchProgress();
        var retired = new object();
        var source = new object();
        var duration = At(180);
        tracker.SourceChanged(retired);
        tracker.BeginSeek();
        tracker.SourceChanged(source);
        tracker.Observe(At(10), duration, 0, 1);
        tracker.Observe(At(11), duration, 1000, 1);
        tracker.BeginSeek();
        tracker.BeginSeek();
        tracker.Observe(At(11.15), duration, 1150, 1);
        StateTests.Verify(tracker.Observe(At(11.3), duration, 1300, 1) is null,
            "ticks from the old clock cannot count while native seek is still outstanding");
        tracker.CompleteSeek(source);
        tracker.CompleteSeek(retired);
        tracker.Observe(At(30), duration, 1450, 1);
        StateTests.Verify(tracker.Observe(At(30.25), duration, 1600, 1) is null,
            "an earlier seek and a retired source completion cannot rearm sampling");
        tracker.CompleteSeek(source);
        StateTests.Verify(tracker.Observe(At(30.5), duration, 1750, 1) is null, "the first post-seek sample establishes the new baseline");
        StateTests.Verify(tracker.Observe(At(31), duration, 2250, 1) == At(30.5), "only subsequent advancement counts");
        StateTests.Verify(tracker.Take(final: true) == (At(10), At(11)) && tracker.Take(final: true) == (At(30.5), At(31)) &&
            tracker.Take(final: true) is null, "keep legitimate ranges on both sides of the seek without counting its movement");

        tracker.BeginSeek();
        tracker.Reset(); // Toggling reporting does not finish a native seek.
        tracker.Observe(At(31), duration, 2300, 1);
        StateTests.Verify(tracker.Observe(At(31.15), duration, 2450, 1) is null, "resetting reporting leaves the native seek outstanding");
        tracker.SourceChanged(new object());
        tracker.Observe(At(31), duration, 2600, 1);
        StateTests.Verify(tracker.Observe(At(31.15), duration, 2750, 1) == At(31), "missing completion does not stick to the replacement source");
    }

    private static async Task DrainWhilePausingAsync()
    {
        var tracker = new WatchProgress();
        var duration = At(180);
        void AddRange(double start, long tick)
        {
            tracker.BreakSegment();
            tracker.Observe(At(start), duration, tick, 1);
            tracker.Observe(At(start + .25), duration, tick + 250, 1);
        }

        for (int i = 0; i <= 15; i++) tracker.Observe(At(i), duration, i * 1000, 1);
        using var canceled = new CancellationTokenSource();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<(TimeSpan, TimeSpan)>();
        var drain = tracker.DrainAsync(final: false, range =>
        {
            sent.Add(range);
            if (sent.Count == 1) return release.Task;
            if (sent.Count == 2) canceled.Cancel();
            return Task.FromResult(true);
        }, canceled.Token);
        AddRange(40, 16000);
        AddRange(60, 17000);
        tracker.FlushAfterCurrentWrite();
        StateTests.Verify(sent.SequenceEqual([(At(0), At(15))]), "pause queues more ranges without starting a parallel write");
        release.SetResult(true);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        StateTests.Verify(sent.SequenceEqual([(At(0), At(15)), (At(40), At(40.25))]),
            "the regular drain honors the pause flush but starts no more sends after cancellation");
        StateTests.Verify(tracker.Take(final: true) == (At(60), At(60.25)) && tracker.Take(final: true) is null,
            "the unattempted interval stays separate; previously dispatched intervals are not replayed");
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
}
