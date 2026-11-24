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

    internal static byte[] Filter(byte[] original) => Filter(original, false, out _);

    internal static byte[] Filter(byte[] original, bool measure, out HashSet<string>? candidates)
    {
        candidates = null;
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
            var bytes = changed ? JsonSerializer.SerializeToUtf8Bytes(root) : original;
            if (measure) candidates = DirectCandidates(streaming);
            return bytes;
        }
        catch (JsonException) { return original; }
        catch (ArgumentException) { return original; } // Ambiguous JSON, e.g. duplicate property names.
    }

    // Match the pinned library's two arrays. An external DASH manifest, a cipher,
    // or ambiguous URLs prevent an exact count from this response alone.
    private static HashSet<string>? DirectCandidates(JsonObject streaming)
    {
        if (!string.IsNullOrWhiteSpace(Text(streaming["dashManifestUrl"]))) return null;
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "formats", "adaptiveFormats" })
        {
            if (streaming[name] is not JsonArray entries) continue;
            foreach (var node in entries)
            {
                if (node is not JsonObject entry || entry["cipher"] is not null || entry["signatureCipher"] is not null) return null;
                var url = Text(entry["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PublicTransport.IsCdn(uri) ||
                    uri.Query.TrimStart('?').Split('&').Any(p => System.Net.WebUtility.UrlDecode(p.Split('=', 2)[0]) == "range") ||
                    !urls.Add(uri.AbsoluteUri)) return null;
            }
        }
        return urls.Count > 0 ? urls : null;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
