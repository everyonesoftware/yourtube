using Xunit;

namespace YourTube.Playback.Tests;

public sealed class VideoChaptersTests
{
    [Fact]
    public void ParsesAStandaloneCreatorListInDescriptionOrder()
    {
        const string description = "Watch the preview at 2:15.\r\n\r\nChapters:\r\n  0:00 Intro  \r\n0:10 – The build\r\n01:00:00\tフィナーレ\r\n\r\nThanks for watching!";

        Assert.Equal(
            [new VideoChapter(0, "Intro"), new VideoChapter(10, "The build"), new VideoChapter(3600, "フィナーレ")],
            VideoChapters.Parse(description, TimeSpan.FromSeconds(3610)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0:00 Intro\n0:10 Middle")]
    [InlineData("0:01 Intro\n0:11 Middle\n0:21 End")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:10 Duplicate\n0:20 End")]
    [InlineData("0:00 Intro\n0:20 Middle\n0:10 Out of order\n0:30 End")]
    [InlineData("0:00 Intro\n0:09 Too short\n0:20 End")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:31 Final chapter too short")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:40 Starts at end")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:50 Starts beyond end")]
    [InlineData("0:00 Intro\n0:10\n0:20 Middle\n0:30 End")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:20 – \n0:30 End")]
    [InlineData("0:00 Intro\n0:1 Bad width\n0:10 Middle\n0:20 End")]
    [InlineData("0:00 Intro\n0:60 Bad seconds\n0:10 Middle\n0:20 End")]
    [InlineData("0:00 Intro\n0:60:00 Bad minutes\n0:10 Middle\n0:20 End")]
    [InlineData("0:00 Intro\n999999999999999999999:10 Overflow\n0:10 Middle\n0:20 End")]
    [InlineData("0:00 Intro\n0:10.5 Fraction\n0:10 Middle\n0:20 End")]
    [InlineData("0:00 Intro\n0:10Title without separator\n0:20 Middle\n0:30 End")]
    [InlineData("Time 0:00 Intro\nTime 0:10 Middle\nTime 0:20 End")]
    [InlineData("0:00 Intro\nSome prose\n0:10 Middle\nMore prose\n0:20 End")]
    [InlineData("Corrections:\n\n0:00 First\n0:10 Second\n0:20 Third")]
    [InlineData("0:00 Intro\n0:10 Middle\n0:20 End\n\n0:00 A\n0:10 B\n0:20 C")]
    public void RejectsMissingInvalidOrAmbiguousChapterLists(string? description) =>
        Assert.Empty(VideoChapters.Parse(description, TimeSpan.FromSeconds(40)));

    [Fact]
    public void PreservesAValidListBesideCorrectionTimestamps()
    {
        const string description = "0:00 - Intro\r0:10|Middle\r0:20 — End\r\rCorrection:\r0:00 First\r0:10 Second\r0:20 Third";

        Assert.Equal(["Intro", "Middle", "End"],
            VideoChapters.Parse(description, TimeSpan.FromSeconds(30)).Select(chapter => chapter.Title));
    }

    [Fact]
    public void RequiresTheFullTenSecondsForTheLastChapter()
    {
        const string description = "0:00 Intro\n0:10 Middle\n0:20 End";
        var minimum = TimeSpan.FromSeconds(30);

        Assert.Equal(3, VideoChapters.Parse(description, minimum).Count);
        Assert.Empty(VideoChapters.Parse(description, minimum - TimeSpan.FromTicks(1)));
        Assert.Empty(VideoChapters.Parse(description, TimeSpan.Zero));
        Assert.Empty(VideoChapters.Parse(description, TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void RejectsOversizedInputsWithoutClippingTheChapterList()
    {
        const string description = "0:00 Intro\n0:10 Middle\n0:20 End";
        var duration = TimeSpan.FromHours(1);
        var longList = string.Join('\n', Enumerable.Range(0, 257).Select(i => $"{i / 6}:{(i % 6) * 10:00} Title"));

        Assert.Equal(256, VideoChapters.Parse(longList[..longList.LastIndexOf('\n')], duration).Count);
        Assert.Empty(VideoChapters.Parse(longList, duration));
        Assert.Empty(VideoChapters.Parse(description + "\n\n" + new string('x', 65_536), duration));
        Assert.Empty(VideoChapters.Parse(description + new string('x', 256), duration));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9.999, 0)]
    [InlineData(10, 1)]
    [InlineData(19.999, 1)]
    [InlineData(20, 2)]
    [InlineData(100, 2)]
    [InlineData(-1, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.NegativeInfinity, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void FindsTheLatestChapterAtOrBeforeThePosition(double position, int? index)
    {
        VideoChapter[] chapters = [new(0, "Intro"), new(10, "Middle"), new(20, "End")];

        Assert.Same(index is { } expected ? chapters[expected] : null, VideoChapters.At(chapters, position));
    }

    [Fact]
    public void NoChapterExistsForAnEmptyListOrBeforeTheFirstStart()
    {
        Assert.Null(VideoChapters.At([], 0));
        Assert.Null(VideoChapters.At([new(10, "Later")], 0));
    }
}
