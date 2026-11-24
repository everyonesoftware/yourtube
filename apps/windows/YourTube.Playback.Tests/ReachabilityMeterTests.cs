using Xunit;

namespace YourTube.Playback.Tests;

public sealed class ReachabilityMeterTests
{
    [Fact]
    public void ShowsAdditionalPlayerRequestsEvenIfDiscoveryNeverReachesCdn()
    {
        var timing = PlaybackTiming.StartForStartup();
        var meter = new ReachabilityMeter(timing, 4);
        meter.PlayerDispatched();
        Assert.Null(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        meter.PlayerDispatched();
        timing.FailStartup();
        var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        Assert.Equal(0, diagnostics.PeakInFlight);
        Assert.Equal(1, diagnostics.PlayerRetries);
    }

    [Fact]
    public void ReportsMedianSlowestAndWallClockFinalQuarterWithoutSummingOverlaps()
    {
        var time = new Clock();
        var timing = PlaybackTiming.StartForStartup();
        var meter = new ReachabilityMeter(timing, 4, time);
        long[] starts = Enumerable.Range(0, 4)
            .Select(_ => meter.Dispatch(HttpMethod.Get, true, true, false, ReachabilityFallback.None)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            time.Now = new[] { 100L, 200, 300, 1200 }[i];
            meter.Complete(starts[i], true);
            meter.CandidateProgress(new(i + 1, 4));
        }
        var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        Assert.Equal(250, diagnostics.MedianCheckMs);
        Assert.Equal(1200, diagnostics.SlowestCheckMs);
        Assert.Equal(900, diagnostics.FinalQuarterMs);
        meter.CandidateProgress(new(0, 8));
        Assert.Null(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics!.FinalQuarterMs);
    }

    [Fact]
    public void MarksMixedOnlyAfterObservedOverlapAndSerialWork()
    {
        var timing = PlaybackTiming.StartForStartup();
        var meter = new ReachabilityMeter(timing, 4);
        var a = meter.Dispatch(HttpMethod.Get, true, true, false, ReachabilityFallback.None);
        var b = meter.Dispatch(HttpMethod.Get, true, true, false, ReachabilityFallback.None);
        meter.Complete(a, true);
        meter.Complete(b, true);
        var c = meter.Dispatch(HttpMethod.Head, false, false, false, ReachabilityFallback.RequestMismatch);
        meter.Complete(c, false);
        var diagnostics = Assert.IsType<StreamReachabilityDiagnostics>(timing.ReadStartup()!.Discovery.ReachabilityDiagnostics);
        Assert.Equal(ReachabilityMode.Mixed, diagnostics.Mode);
        Assert.Equal(ReachabilityFallback.RequestMismatch, diagnostics.Fallback);
        Assert.Equal(2, diagnostics.PeakInFlight);
        Assert.Equal(1, diagnostics.SizeRequests);
        Assert.Null(diagnostics.FinalQuarterMs);
    }

    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
}
