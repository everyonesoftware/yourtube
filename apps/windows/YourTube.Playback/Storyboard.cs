using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourTube.Playback;

public readonly record struct StoryboardFrame(int Sheet, int Column, int Row, int Width, int Height);

// Opaque, resolver-owned VOD sheet catalog; the public surface never exposes signed URLs.
public sealed class Storyboard
{
    private readonly string prefix;
    private readonly string query;
    private readonly int level, width, height, count, columns, rows, interval;
    internal int MaximumSheetWidth => width * columns;
    internal int MaximumSheetHeight => height * rows;

    private Storyboard(string prefix, string query, int level, int width, int height, int count, int columns, int rows, int interval)
    {
        this.prefix = prefix;
        this.query = query;
        this.level = level;
        this.width = width;
        this.height = height;
        this.count = count;
        this.columns = columns;
        this.rows = rows;
        this.interval = interval;
    }

    public StoryboardFrame? FrameAt(double seconds)
    {
        double index = Math.Floor(seconds * 1000 / interval);
        if (!double.IsFinite(index) || index < 0 || index >= count) return null;
        int tile = (int)index % (columns * rows);
        return new((int)index / (columns * rows), tile % columns, tile / columns, width, height);
    }

    internal Uri? SheetUri(int sheet) => sheet < 0 || sheet > (count - 1) / (columns * rows) ? null :
        new Uri(FormattableString.Invariant($"{prefix}{level}/M{sheet}.jpg?{query}"));

    internal static Storyboard? Read(byte[] body, string expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return Read(document.RootElement, expectedId);
        }
        catch (JsonException) { return null; }
    }

    internal static Storyboard? Read(JsonElement root, string expectedId)
    {
        try
        {
            var details = Unique(root, "videoDetails");
            if (Text(details, "videoId") != expectedId || !Regex.IsMatch(expectedId, @"\A[A-Za-z0-9_-]{11}\z") ||
                Text(Unique(root, "playabilityStatus"), "status") != "OK" ||
                details.EnumerateObject().Any(p => p.Name is "isLive" or "isLiveContent" or "isUpcoming" && p.Value.ValueKind != JsonValueKind.False)) return null;
            var spec = Text(Unique(Unique(root, "storyboards"), "playerStoryboardSpecRenderer"), "spec");
            if (spec is null || spec.Length > 8192) return null;
            var parts = spec.Split('|');
            if (parts.Length is < 2 or > 9) return null;

            // Match the observed wire template literally before constructing URLs. In particular,
            // URI normalization must not accept a disguised origin or a different video/path.
            var template = Regex.Match(parts[0], @"\A(https://i\.ytimg\.com/sb/" + expectedId +
                @"/storyboard3_L)\$L/\$N\.jpg\?(sqp=[A-Za-z0-9_-]+(?:=|%3[Dd]){0,2})\z");
            if (!template.Success) return null;
            Storyboard? best = null;
            for (int i = 1; i < parts.Length; i++)
            {
                var fields = parts[i].Split('#');
                if (fields.Length != 8 || fields[6] != "M$M" ||
                    !Regex.IsMatch(fields[7], @"\Ars\$[A-Za-z0-9_-]{1,256}\z") ||
                    !Number(fields[0], 1, 512, out int width) || !Number(fields[1], 1, 512, out int height) ||
                    !Number(fields[2], 1, 100_000, out int count) || !Number(fields[3], 1, 20, out int columns) ||
                    !Number(fields[4], 1, 20, out int rows) || !Number(fields[5], 1, 3_600_000, out int interval)) continue;
                // The CDN pads the observed odd nominal widths (e.g. 159 -> 160) per tile.
                width = (width + 1) & ~1;
                if (width * columns > 2048 || height * rows > 2048 || width * height * columns * rows > 2_097_152) continue;
                if (best is null || width * height > best.width * best.height)
                    best = new(template.Groups[1].Value, template.Groups[2].Value + "&sigh=" + fields[7],
                        i - 1, width, height, count, columns, rows, interval);
            }
            return best;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    private static bool Number(string value, int min, int max, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= min && number <= max;

    private static string? Text(JsonElement parent, string name) => Unique(parent, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static JsonElement Unique(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return default;
        JsonElement found = default;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found.ValueKind != JsonValueKind.Undefined) return default;
            found = property.Value;
        }
        return found;
    }
}
