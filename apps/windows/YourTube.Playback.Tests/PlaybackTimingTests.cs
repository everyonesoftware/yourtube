using System.Text.Json;
using Xunit;
using YourTube.Playback;

namespace YourTube.Playback.Tests;

public class PlaybackTimingTests
{
    [Fact]
    public async Task LocalFileContainsOnlyCorrelatedSanitizedStagesAndMetrics()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "diagnostics", "timing.log");
        try
        {
            await using (var sink = new PlaybackTimingFile(path))
            {
                var timing = new PlaybackTiming(sink);
                timing.Mark(PlaybackTimingStage.ResolveStarted);
                timing.Mark(PlaybackTimingStage.NativeDownloadBitrateChanged, 12, 3000, 1500, 720, 5);
                timing.Mark((PlaybackTimingStage)999);
                new PlaybackTiming(sink).Mark(PlaybackTimingStage.ResolveStarted);
            }
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            using var changed = JsonDocument.Parse(lines[1]);
            using var next = JsonDocument.Parse(lines[2]);
            var row = changed.RootElement;
            Assert.Equal(new[] { "attempt", "utc", "elapsedMs", "stage", "positionMs", "bitrateBps", "previousBitrateBps", "height", "reasonCode" }, row.EnumerateObject().Select(p => p.Name));
            Assert.Equal("NativeDownloadBitrateChanged", row.GetProperty("stage").GetString());
            Assert.Equal(3000u, row.GetProperty("bitrateBps").GetUInt32());
            Assert.Equal(TimeSpan.Zero, row.GetProperty("utc").GetDateTimeOffset().Offset);
            Assert.True(row.GetProperty("elapsedMs").GetInt64() >= first.RootElement.GetProperty("elapsedMs").GetInt64());
            Assert.Equal(first.RootElement.GetProperty("attempt").GetGuid(), row.GetProperty("attempt").GetGuid());
            Assert.NotEqual(row.GetProperty("attempt").GetGuid(), next.RootElement.GetProperty("attempt").GetGuid());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task FileDropsOldDataAndKeepsNewestLineUnderByteLimit()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "timing.log");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 1000));
            await using (var sink = new PlaybackTimingFile(path, byteLimit: 512))
                new PlaybackTiming(sink).Mark(PlaybackTimingStage.NativeDisposed);
            Assert.InRange(new FileInfo(path).Length, 1, 512);
            using var line = JsonDocument.Parse(Assert.Single(File.ReadLines(path)));
            Assert.Equal("NativeDisposed", line.RootElement.GetProperty("stage").GetString());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task UnwritableFileDoesNotThrowIntoPlayback()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await using var sink = new PlaybackTimingFile(directory.FullName);
            new PlaybackTiming(sink).Mark(PlaybackTimingStage.NativePlayerOpened);
        }
        finally { directory.Delete(true); }
    }
}
