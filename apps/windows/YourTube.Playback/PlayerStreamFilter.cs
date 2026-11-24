using System.Globalization;
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

    internal static byte[] Filter(byte[] original, bool measure, bool prefetch,
        out HashSet<string>? candidates, out Uri[]? probes, out ReachabilityFallback fallback, out int repeatedFormatEntries)
    {
        candidates = null;
        probes = null;
        repeatedFormatEntries = 0;
        fallback = ReachabilityFallback.NoStreamList;
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
            if (measure || prefetch)
            {
                var direct = DirectCandidates(streaming, ref fallback);
                if (measure) candidates = direct;
                if (prefetch && direct is not null)
                {
                    if (root["playabilityStatus"] is JsonObject status && Text(status["status"]) == "OK")
                        probes = DirectTailProbes(streaming, direct.Count, ref fallback, out repeatedFormatEntries);
                    else
                        fallback = ReachabilityFallback.PlayerNotConfirmedPlayable;
                }
            }
            return bytes;
        }
        catch (JsonException) { fallback = ReachabilityFallback.UnreadablePlayerResponse; return original; }
        catch (ArgumentException) { fallback = ReachabilityFallback.UnreadablePlayerResponse; return original; } // E.g. duplicate property names.
    }

    // Only prefetch the pinned library's predictable direct-URL, known-length
    // tail checks. HEAD discovery, alternate providers and deciphering stay serial.
    private static Uri[]? DirectTailProbes(JsonObject streaming, int count, ref ReachabilityFallback fallback, out int repeatedFormatEntries)
    {
        repeatedFormatEntries = 0;
        if (count is < 2 or > 128) { fallback = ReachabilityFallback.CandidateCountOutsideWindow; return null; }
        var probes = new List<Uri>(count);
        var itags = new HashSet<int>();
        foreach (var name in new[] { "formats", "adaptiveFormats" })
        {
            if (streaming[name] is not JsonArray entries) continue;
            foreach (var node in entries)
            {
                if (node is not JsonObject entry) { fallback = ReachabilityFallback.InvalidStreamEntry; return null; }
                if (Text(entry["url"]) is not { Length: > 0 } url) { fallback = ReachabilityFallback.MissingDirectUrl; return null; }
                if (entry["itag"] is not JsonValue value || !value.TryGetValue<int>(out var itag) || itag <= 0)
                { fallback = ReachabilityFallback.InvalidFormatId; return null; }
                // Candidate URLs are already distinct. Replay matches exact requests in order, not format labels.
                if (!itags.Add(itag)) repeatedFormatEntries++;
                if (!long.TryParse(Text(entry["contentLength"]), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 2)
                {
                    fallback = entry["contentLength"] is null ? ReachabilityFallback.MissingLength : ReachabilityFallback.InvalidLength;
                    return null;
                }
                var uri = new Uri(url);
                if (uri.Query.TrimStart('?').Split('&').Any(p =>
                    string.Equals(System.Net.WebUtility.UrlDecode(p.Split('=', 2)[0]), "hl", StringComparison.OrdinalIgnoreCase)))
                { fallback = ReachabilityFallback.ExistingLanguageParameter; return null; }
                probes.Add(new Uri(uri.AbsoluteUri + (uri.Query.Length == 0 ? "?" : "&") +
                    FormattableString.Invariant($"range={length - 2}-{length - 1}&hl=en")));
            }
        }
        fallback = ReachabilityFallback.None;
        return probes.ToArray();
    }

    // Match the pinned library's two arrays. An external DASH manifest, a cipher,
    // or ambiguous URLs prevent an exact count from this response alone.
    private static HashSet<string>? DirectCandidates(JsonObject streaming, ref ReachabilityFallback fallback)
    {
        if (!string.IsNullOrWhiteSpace(Text(streaming["dashManifestUrl"])))
        {
            fallback = ReachabilityFallback.ExternalManifest;
            return null;
        }
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "formats", "adaptiveFormats" })
        {
            if (streaming[name] is not JsonArray entries) continue;
            foreach (var node in entries)
            {
                if (node is not JsonObject entry) { fallback = ReachabilityFallback.InvalidStreamEntry; return null; }
                if (entry["cipher"] is not null || entry["signatureCipher"] is not null)
                {
                    fallback = ReachabilityFallback.CipheredUrl;
                    return null;
                }
                var url = Text(entry["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PublicTransport.IsCdn(uri))
                { fallback = ReachabilityFallback.UnsupportedStreamUrl; return null; }
                if (uri.Query.TrimStart('?').Split('&').Any(p => System.Net.WebUtility.UrlDecode(p.Split('=', 2)[0]) == "range"))
                { fallback = ReachabilityFallback.ExistingRangeParameter; return null; }
                if (!urls.Add(uri.AbsoluteUri)) { fallback = ReachabilityFallback.DuplicateStreamUrl; return null; }
            }
        }
        if (urls.Count > 0) return urls;
        fallback = ReachabilityFallback.NoDirectCandidates;
        return null;
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
