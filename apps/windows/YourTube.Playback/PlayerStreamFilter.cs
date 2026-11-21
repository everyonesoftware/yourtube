using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YourTube.Playback;

// YoutubeExplode 6.6.3 probes stream lengths before exposing stream metadata. Drop
// only known-incompatible entries from its public player JSON before it does that.
// The resolver's post-filter and media validation remain authoritative.
internal static class PlayerStreamFilter
{
    internal const string AvcPattern = "\\Aavc1\\.[0-9a-fA-F]{6}\\z";
    internal const string AacCodec = "mp4a.40.2";
    internal const int MaxHeight = 1080;
    internal const int MaxWidth = 1920;

    internal static byte[] Filter(byte[] original)
    {
        try
        {
            if (JsonNode.Parse(original) is not JsonObject root ||
                !root.TryGetPropertyValue("streamingData", out var data) || data is not JsonObject streaming)
                return original;

            bool changed = false;
            foreach (var name in new[] { "formats", "adaptiveFormats" })
            {
                if (!streaming.TryGetPropertyValue(name, out var value) || value is not JsonArray entries) continue;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i] is not JsonObject entry || !IsUnsupported(entry)) continue;
                    entries.RemoveAt(i);
                    changed = true;
                }
            }
            return changed ? JsonSerializer.SerializeToUtf8Bytes(root) : original;
        }
        catch (JsonException) { return original; }
        catch (ArgumentException) { return original; } // Ambiguous JSON, e.g. duplicate property names.
    }

    private static bool IsUnsupported(JsonObject entry)
    {
        if (!entry.TryGetPropertyValue("mimeType", out var value) || value is not JsonValue json ||
            !json.TryGetValue<string>(out var text) || !MediaTypeHeaderValue.TryParse(text, out var mime)) return false;
        var codecs = mime.Parameters.FirstOrDefault(p => p.Name.Equals("codecs", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (string.IsNullOrWhiteSpace(codecs)) return false;
        return mime.MediaType switch
        {
            "video/mp4" => !Regex.IsMatch(codecs, AvcPattern, RegexOptions.CultureInvariant) ||
                OutsideRange(entry, "height", MaxHeight) || OutsideRange(entry, "width", MaxWidth),
            "audio/mp4" => codecs != AacCodec,
            "video/webm" or "audio/webm" => true,
            _ => false,
        };
    }

    private static bool OutsideRange(JsonObject entry, string key, int max) =>
        entry.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
        value.TryGetValue<int>(out var dimension) && dimension > max;
}
