using System.Globalization;
using System.Text.Json;

namespace YourTube.Playback;

// Only the fields Theater and DASH need. No provider response or media URL is retained.
internal sealed class PlayerMetadata(string title, string author, string? description, TimeSpan duration)
{
    internal string Title { get; } = title;
    internal string Author { get; } = author;
    internal string? Description { get; } = description;
    internal TimeSpan Duration { get; } = duration;

    internal static PlayerMetadata? Read(byte[] body, string expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!Object(root, "videoDetails", out var details)) return null;
            var id = Text(details, "videoId");
            if (id is not null && id != expectedId)
                throw new PlaybackException("unavailable", "The player response does not match the requested video.");
            if (id is null || !Object(root, "playabilityStatus", out var playability) || Text(playability, "status") != "OK" ||
                IsTrue(details, "isLiveContent") || IsTrue(details, "isLive") || IsTrue(details, "isUpcoming")) return null;
            var title = Text(details, "title");
            var author = Text(details, "author");
            if (title is null || string.IsNullOrWhiteSpace(author) ||
                !int.TryParse(Text(details, "lengthSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                return null;
            return new(title, author, Text(details, "shortDescription"), TimeSpan.FromSeconds(seconds));
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; } // E.g. an unpaired escaped surrogate.
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool Object(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
}
