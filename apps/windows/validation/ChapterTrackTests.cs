using YourTube.App;
using YourTube.Playback;

internal static class ChapterTrackTests
{
    internal static void Run()
    {
        var chapters = VideoChapters.Parse("0:00 Intro\n0:20 First\n1:00 Last", TimeSpan.FromSeconds(100));
        var spans = ChapterTrackLayout.Create(chapters, 100, 500);
        StateTests.Verify(spans is [{ Left: 0, Right: 98 }, { Left: 102, Right: 298 }, { Left: 302, Right: 500 }],
            "chapter gaps are centered at the exact timestamps, with no gap at either end");
        StateTests.Verify(spans.Select(span => span.PlayedWidth(100)).SequenceEqual([98d, 0, 0]) &&
            spans.Select(span => span.PlayedWidth(150)).SequenceEqual([98d, 48, 0]) &&
            spans.Select(span => span.PlayedWidth(500)).SequenceEqual([98d, 196, 198]),
            "played progress stops at each gap and fills the next segment independently");

        var narrow = ChapterTrackLayout.Create(chapters, 100, 5);
        StateTests.Verify(narrow.All(span => span.Width > 0) && narrow[0].Right < narrow[1].Left &&
            narrow[1].Right < narrow[2].Left, "narrow tracks shrink gaps without swallowing chapters");
        StateTests.Verify(ChapterTrackLayout.Create([], 100, 500).Length == 0 &&
            ChapterTrackLayout.Create(chapters, 60, 500).Length == 0 &&
            ChapterTrackLayout.Create(chapters, 100, 0).Length == 0,
            "no chapters, a stale duration or unmeasured width keep the native track");
        Console.WriteLine("PASS: proportional chapter segments, open gaps and played widths.");
    }
}
