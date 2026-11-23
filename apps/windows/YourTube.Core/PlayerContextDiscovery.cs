using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YourTube.Core;

internal static class PlayerContextDiscovery
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex PlayerPath = new("\"(?:jsUrl|PLAYER_JS_URL)\"\\s*:\\s*\"(/s/player/[A-Za-z0-9_-]+/player_ias(?:_[A-Za-z0-9_-]+)?\\.vflset/[A-Za-z0-9_-]+/base\\.js)\"", RegexOptions.CultureInvariant, ParseTimeout);
    // Recognize literal keys in all three forms; retain malformed values as empty captures.
    private static readonly Regex Timestamp = new("""(?:"signatureTimestamp"|'signatureTimestamp'|(?<![\w$])signatureTimestamp)\s*:(?:\s*([1-9][0-9]{0,8})(?=\s*[,}]))?""", RegexOptions.CultureInvariant, ParseTimeout);

    internal static async Task<int?> DiscoverAsync(HttpClient http, string videoId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!YouTubeClient.IsWatchVideoId(videoId)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var page = await ReadAsync(http, "/watch?v=" + videoId, deadline.Token);
            if (page is null) return null;
            var paths = PlayerPath.Matches(page.Replace(@"\/", "/", StringComparison.Ordinal))
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (paths.Length != 1) return null;
            var script = await ReadAsync(http, paths[0], deadline.Token);
            if (script is null) return null;
            var timestamps = Timestamp.Matches(script).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return timestamps.Length == 1 && int.TryParse(timestamps[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or RegexMatchTimeoutException) { return null; }
    }

    private static async Task<string?> ReadAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync("https://www.youtube.com" + path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
