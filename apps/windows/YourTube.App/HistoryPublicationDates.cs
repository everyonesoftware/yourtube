using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace YourTube.App;

internal sealed class HistoryPublicationDates : IDisposable
{
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int MaxCachedDates = 256;
    private const string Watch = "https://www.youtube.com/watch?v=";
    private const string DateTag = "<meta itemprop=\"datePublished\" content=\"";
    // Match known watch-page head tags, not text inside scripts, comments or other attributes.
    private static readonly Regex HeadTags = new(
        "<!--[\\s\\S]*?(?:-->|\\z)|<(script|style|title|template)\\b[^>]*>[\\s\\S]*?(?:</\\1\\s*>|\\z)|</?[a-z][a-z0-9:-]*(?:\"[^\"]*\"|'[^']*'|[^'\">])*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2, 2);
    private readonly Dictionary<string, DateOnly> cache = new(StringComparer.Ordinal);

    public HistoryPublicationDates() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseDrainSize = 0,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli
    }) { }

    internal HistoryPublicationDates(HttpMessageHandler transport) => http = new(transport)
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaxBytes
    };

    public async Task<DateOnly?> LoadAsync(string videoId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (videoId.Length != 11 || !videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        lock (cache) if (cache.TryGetValue(videoId, out var cached)) return cached;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await slots.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                lock (cache) if (cache.TryGetValue(videoId, out var cached)) return cached;
                using var request = new HttpRequestMessage(HttpMethod.Get, Watch + videoId + "&hl=en");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                var content = response.Content;
                if (response.StatusCode != HttpStatusCode.OK || content.Headers.ContentType?.MediaType != "text/html") return null;
                await content.LoadIntoBufferAsync(MaxBytes, budget.Token).ConfigureAwait(false);
                var html = await content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                var date = ReadDate(html, videoId);
                budget.Token.ThrowIfCancellationRequested();
                if (date is { } value)
                {
                    lock (cache)
                    {
                        if (cache.Count == MaxCachedDates) cache.Remove(cache.Keys.First());
                        cache[videoId] = value;
                    }
                }
                return date;
            }
            finally { slots.Release(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or InvalidDataException or RegexMatchTimeoutException or InvalidOperationException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static DateOnly? ReadDate(string html, string videoId)
    {
        var inHead = false;
        var canonical = false;
        DateOnly? published = null;
        foreach (var match in HeadTags.EnumerateMatches(html))
        {
            var tag = html.AsSpan(match.Index, match.Length);
            if (!inHead)
            {
                if (tag.Equals("<head>", StringComparison.OrdinalIgnoreCase)) inHead = true;
                continue;
            }
            if (tag.Equals("</head>", StringComparison.OrdinalIgnoreCase)) return canonical ? published : null;
            if (tag.StartsWith("<link rel=\"canonical\"", StringComparison.Ordinal))
            {
                if (canonical || !tag.SequenceEqual($"<link rel=\"canonical\" href=\"{Watch}{videoId}\">")) return null;
                canonical = true;
            }
            if (!tag.StartsWith(DateTag, StringComparison.Ordinal) || !tag.EndsWith("\">", StringComparison.Ordinal)) continue;
            if (published is not null) return null;
            var field = tag[DateTag.Length..^2];
            if (DateOnly.TryParseExact(field, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) published = date;
            else if (DateTimeOffset.TryParseExact(field, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                published = DateOnly.FromDateTime(timestamp.DateTime);
            else return null;
        }
        return null;
    }

    internal static string? FormatAge(DateOnly published, DateOnly today)
    {
        var days = today.DayNumber - published.DayNumber;
        if (days < 0) return null;
        if (days == 0) return "Today";
        var months = (today.Year - published.Year) * 12 + today.Month - published.Month;
        if (published.AddMonths(months) > today) months--;
        var (count, unit) = months switch
        {
            >= 12 => (months / 12, "year"),
            >= 1 => (months, "month"),
            _ when days >= 7 => (days / 7, "week"),
            _ => (days, "day")
        };
        return $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }

    public void Dispose() => http.Dispose();
}
