using Xunit;
using YourTube.Playback;

namespace YourTube.Playback.Tests;

public sealed class PlaybackStartupTests
{
    [Fact]
    public void MilestonesReportMeasuredDurationsAndOnlyAdvancingPlaybackCompletesStartup()
    {
        var start = new PlaybackStartup();
        start.Record(PlaybackTimingStage.CompatibleStreamsFound, 800);
        start.Record(PlaybackTimingStage.NativeSourceStarted, 2200);
        start.Record(PlaybackTimingStage.NativeSourceCreated, 2600);
        start.Record(PlaybackTimingStage.NativePlayRequested, 2601);
        start.Record(PlaybackTimingStage.NativeFirstSuccessfulRead, 2750);
        var buffering = start.Read(12600);
        Assert.Equal(3, buffering.Completed);
        Assert.Equal(new long?[] { 800, 1400, 400, 10000, null }, buffering.StepDurations);

        start.Record(PlaybackTimingStage.NativePlayerOpened, 13200);
        start.ObserveClock(TimeSpan.Zero, 13300);
        start.ObserveClock(TimeSpan.Zero, 13310);
        start.ObserveClock(null, 13320); // break for a seek or interruption
        start.ObserveClock(TimeSpan.FromMinutes(5), 13340);
        Assert.Equal(4, start.Read(13350).Completed);
        start.ObserveClock(TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(40), 13380);
        start.ObserveClock(TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(60), 13400);
        start.Fail(24000); // a later playback failure is not a failed startup
        var result = start.Read(50000);
        Assert.Equal(5, result.Completed);
        Assert.False(result.Failed);
        Assert.Equal(13400, result.ElapsedMs);
        Assert.Equal(new long?[] { 800, 1400, 400, 10600, 200 }, result.StepDurations);
    }

    [Fact]
    public void EarlySeekJumpIsNotPlaybackAndMissingSeekCompletionDoesNotBlockRealProgress()
    {
        var start = new PlaybackStartup();
        start.Record(PlaybackTimingStage.NativePlayerOpened, 100);
        start.ObserveClock(null, 150); // The app initiated a seek.
        start.ObserveClock(TimeSpan.Zero, 200); // The old position is still visible.
        start.ObserveClock(TimeSpan.FromSeconds(30), 350); // The new position lands without a seek-complete event.
        Assert.Equal(4, start.Read(350).Completed);
        start.ObserveClock(TimeSpan.FromMilliseconds(30150), 500);
        start.ObserveClock(TimeSpan.FromMilliseconds(30300), 650);
        Assert.Equal(5, start.Read(650).Completed);
    }

    [Fact]
    public void VideoEndingBeforeEnoughClockSamplesFreezesStartupWithoutReportingSuccess()
    {
        var start = new PlaybackStartup();
        start.Record(PlaybackTimingStage.NativePlayerOpened, 100);
        start.ObserveClock(TimeSpan.FromSeconds(29), 200);
        start.ObserveClock(TimeSpan.FromMilliseconds(29150), 350);
        start.Record(PlaybackTimingStage.NativePlaybackEnded, 430);
        start.ObserveClock(TimeSpan.FromMilliseconds(29300), 500);
        var result = start.Read(10000);
        Assert.Equal(4, result.Completed);
        Assert.Equal(430, result.ElapsedMs);
        Assert.False(result.Failed);
        Assert.True(result.EndedUnconfirmed);
        Assert.True(result.IsFinal);
    }

    [Fact]
    public void FailureFreezesItsStageAndLateCallbacksCannotRewriteIt()
    {
        var start = new PlaybackStartup();
        start.Record(PlaybackTimingStage.CompatibleStreamsFound, 600);
        start.Fail(2000);
        start.Record(PlaybackTimingStage.NativePlayerOpened, 2200);
        start.Fail(3000);
        var result = start.Read(4000);
        Assert.True(result.Failed);
        Assert.Equal(1, result.Completed);
        Assert.Equal(2000, result.ElapsedMs);
        Assert.Equal(new long?[] { 600, 1400, null, null, null }, result.StepDurations);
    }

    [Fact]
    public void DiscoveryKeepsCompletedChecksAndFreezesTheFailedSubtask()
    {
        var start = new PlaybackStartup();
        start.Record(PlaybackTimingStage.StreamManifestStarted, 50);
        start.Record(PlaybackTimingStage.StreamReachabilityStarted, 350);
        Assert.Equal(new long?[] { 300, 250, null }, start.Read(600).Discovery.Durations);
        start.Record(PlaybackTimingStage.StreamReachabilityStarted, 900);
        start.Fail(1150);
        start.Record(PlaybackTimingStage.StreamManifestCompleted, 1300);
        var result = start.Read(5000);
        Assert.True(result.Failed);
        Assert.Equal(StreamDiscoveryStep.Reachability, result.Discovery.Current);
        Assert.Equal(new long?[] { 300, 800, null }, result.Discovery.Durations);
        Assert.All(new PlaybackStartup().Read(0).Discovery.Durations, duration => Assert.Null(duration));
    }

    [Fact]
    public void InMemoryTimingAcceptsMilestonesWithoutAFileSink()
    {
        var timing = PlaybackTiming.StartForStartup();
        timing.Mark(PlaybackTimingStage.CompatibleStreamsFound);
        var result = Assert.IsType<PlaybackStartupSnapshot>(timing.ReadStartup());
        Assert.Equal(1, result.Completed);
        Assert.False(result.Failed);
    }
}
