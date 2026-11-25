using System.Globalization;

namespace YourTube.Playback;

public sealed record VideoChapter(double StartSeconds, string Title);

public static class VideoChapters
{
    private const int MaxDescriptionLength = 65_536;
    private const int MaxTitleLength = 256;
    private const int MaxChapters = 256;
    private const int MinimumChapterSeconds = 10;

    /// <summary>Returns the single valid, consecutive timestamp block, or an empty list.</summary>
    public static IReadOnlyList<VideoChapter> Parse(string? description, TimeSpan duration)
    {
        if (description is not { Length: > 0 and <= MaxDescriptionLength } ||
            duration.Ticks < 3L * MinimumChapterSeconds * TimeSpan.TicksPerSecond)
            return [];

        // Compare whole-second starts to a tick-accurate duration without rounding up.
        var latestStart = duration.Ticks / TimeSpan.TicksPerSecond - MinimumChapterSeconds;
        IReadOnlyList<VideoChapter>? result = null;
        var block = new List<VideoChapter>();
        var invalid = false;
        var corrections = false;

        foreach (var rawLine in description.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            var firstColon = 0;
            while (firstColon < line.Length && char.IsAsciiDigit(line[firstColon]))
                firstColon++;

            if (firstColon > 0 && firstColon < line.Length && line[firstColon] == ':')
            {
                if (corrections || invalid)
                    continue;

                // A malformed timestamp row invalidates its whole block, not just that row.
                if (block.Count == MaxChapters ||
                    !TryReadChapter(line, firstColon, out var start, out var title) ||
                    start > latestStart ||
                    (block.Count == 0 ? start != 0 : start - block[^1].StartSeconds < MinimumChapterSeconds))
                {
                    invalid = true;
                    continue;
                }

                block.Add(new VideoChapter(start, title.ToString()));
                continue;
            }

            if (!FinishBlock())
                return [];

            // YouTube descriptions can also include timestamp lists under a correction heading.
            if (!line.IsEmpty)
                corrections = line.Equals("Correction:", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("Corrections:", StringComparison.OrdinalIgnoreCase);
        }

        return FinishBlock() ? result ?? [] : [];

        bool FinishBlock()
        {
            if (!invalid && block.Count >= 3)
            {
                if (result is not null)
                    return false;
                result = Array.AsReadOnly(block.ToArray());
            }

            block.Clear();
            invalid = false;
            return true;
        }
    }

    /// <summary>Finds the latest start at or before a finite playback position in a sorted chapter list.</summary>
    public static VideoChapter? At(IReadOnlyList<VideoChapter> chapters, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return null;

        var low = 0;
        var high = chapters.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (chapters[middle].StartSeconds <= seconds)
                low = middle + 1;
            else
                high = middle;
        }

        return low == 0 ? null : chapters[low - 1];
    }

    private static bool TryReadChapter(ReadOnlySpan<char> line, int firstColon, out long seconds, out ReadOnlySpan<char> title)
    {
        seconds = 0;
        title = default;
        if (!int.TryParse(line[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out var leading))
            return false;

        var tail = line[(firstColon + 1)..];
        if (!TryReadClockPart(tail, out var endPart))
            return false;
        tail = tail[2..];
        seconds = leading * 60L + endPart;

        if (!tail.IsEmpty && tail[0] == ':')
        {
            tail = tail[1..];
            if (!TryReadClockPart(tail, out var finalPart))
                return false;
            seconds = seconds * 60L + finalPart;
            tail = tail[2..];
        }

        if (tail.IsEmpty || (!char.IsWhiteSpace(tail[0]) && !IsTitleSeparator(tail[0])))
            return false;
        title = tail.Trim();
        if (IsTitleSeparator(title[0]))
            title = title[1..].TrimStart();
        return title.Length is > 0 and <= MaxTitleLength;
    }

    private static bool IsTitleSeparator(char value) => value is '-' or '–' or '—' or '|';

    private static bool TryReadClockPart(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.Length < 2 || text[0] is < '0' or > '5' || !char.IsAsciiDigit(text[1]))
            return false;
        value = (text[0] - '0') * 10 + (text[1] - '0');
        return true;
    }
}
