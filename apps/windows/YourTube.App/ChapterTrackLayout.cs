using YourTube.Playback;

namespace YourTube.App;

internal static class ChapterTrackLayout
{
    internal readonly record struct Segment(double Left, double Right)
    {
        internal double Width => Right - Left;
        internal double PlayedWidth(double playhead) => Math.Clamp(playhead - Left, 0, Width);
    }

    internal static Segment[] Create(IReadOnlyList<VideoChapter> chapters, double duration, double width)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(duration) || duration <= 0) return [];
        if (chapters.Count < 2 || duration <= chapters[^1].StartSeconds) return [new(0, width)];

        var segments = new Segment[chapters.Count];
        double left = 0;
        double leftInset = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            double right = i + 1 < chapters.Count ? X(i + 1) : width;
            double rightInset = 0;
            if (i + 1 < chapters.Count)
            {
                double next = i + 2 < chapters.Count ? X(i + 2) : width;
                // Center the gap on the timestamp without swallowing short chapters at narrow widths.
                rightInset = Math.Min(2, Math.Min(right - left, next - right) / 4);
            }
            segments[i] = new(left + leftInset, right - rightInset);
            left = right;
            leftInset = rightInset;
        }
        return segments;

        double X(int index) => chapters[index].StartSeconds / duration * width;
    }
}
