using System.Globalization;
using System.Text.Json;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Search;

namespace YourTube.Playback;

public sealed record PublicVideoSearchResult(string VideoId, string Title, string Channel, string Duration);

public sealed class PublicVideoSearch
{
    private const int MaxQueryLength = 256;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxResults = 50;
    private static readonly string[] SearchEnvelopePath =
        ["twoColumnSearchResultsRenderer", "primaryContents", "sectionListRenderer", "contents"];
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(10);
    private static readonly HttpClient PublicClient = CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = false,
        MaxResponseDrainSize = 0,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });
    private readonly SearchClient search;

    public PublicVideoSearch() : this(PublicClient) { }
    internal PublicVideoSearch(HttpClient http) => search = new SearchClient(http);
    internal static HttpClient CreateClient(HttpMessageHandler handler) => new(new SearchTransport(handler))
    {
        Timeout = Timeout.InfiniteTimeSpan, // SearchAsync provides a single deadline through parsing.
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    /// <summary>Returns up to 50 video results from the first public search page, without account personalization.</summary>
    public async Task<IReadOnlyList<PublicVideoSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = query?.Trim();
        if (text is null || text.Length is 0 or > MaxQueryLength)
            throw new PlaybackException("search_invalid_query", "Enter a search query of 1–256 characters.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SearchBudget);
        try
        {
            // GetVideosAsync flattens batches; when a first batch is empty it can
            // advance the cursor before yielding even one video. Stop at the batch boundary.
            await foreach (var batch in search.GetResultBatchesAsync(text, SearchFilter.Video, budget.Token).ConfigureAwait(false))
            {
                var results = batch.Items.OfType<VideoSearchResult>()
                    .Take(MaxResults)
                    .Select(video => new PublicVideoSearchResult(video.Id.Value, video.Title, video.Author.ChannelTitle, FormatDuration(video.Duration)))
                    .ToArray();
                budget.Token.ThrowIfCancellationRequested();
                return Array.AsReadOnly(results);
            }
            throw InvalidResponse();
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlaybackException("search_timeout", "YouTube search timed out. Try again.");
        }
        catch (Exception ex) when (ex is PlaybackException or JsonException or YoutubeExplodeException or
            ArgumentException or InvalidOperationException or FormatException or OverflowException or HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is PlaybackException) throw;
            if (ex is HttpRequestException or IOException) throw Unavailable();
            throw InvalidResponse();
        }
    }

    private static string FormatDuration(TimeSpan? duration) => duration switch
    {
        null => "",
        { TotalHours: >= 1 } value => string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"),
        { Ticks: >= 0 } value => value.ToString(@"m\:ss", CultureInfo.InvariantCulture),
        _ => "",
    };

    private static PlaybackException InvalidResponse() => new("search_invalid_response", "YouTube returned an unsupported search response.");
    private static PlaybackException Unavailable() => new("search_unavailable", "YouTube search is unavailable. Try again.");

    // Match the pinned library's video discovery scope, but inspect raw IDs before
    // its VideoId type can interpret URLs or accept non-ASCII letters as IDs.
    private static void ValidateVideoIds(JsonElement item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.ValueKind == JsonValueKind.Array)
            foreach (var child in item.EnumerateArray()) ValidateVideoIds(child, token);
        if (item.ValueKind != JsonValueKind.Object) return;
        foreach (var property in item.EnumerateObject())
        {
            if (property.NameEquals("videoRenderer") &&
                (property.Value.ValueKind != JsonValueKind.Object ||
                 !property.Value.TryGetProperty("videoId", out var field) || field.ValueKind != JsonValueKind.String ||
                 field.GetString() is not { Length: 11 } id ||
                 !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                throw InvalidResponse();
            ValidateVideoIds(property.Value, token);
        }
    }

    private sealed class SearchTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://www.youtube.com/youtubei/v1/search" ||
                request.Headers.Contains("Authorization") || request.Headers.Contains("Proxy-Authorization") || request.Headers.Contains("Cookie"))
                throw Unavailable();

            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            try
            {
                if (!response.IsSuccessStatusCode) throw Unavailable();
                await response.Content.LoadIntoBufferAsync(MaxResponseBytes, token).ConfigureAwait(false);
                using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(body, new() { AllowDuplicateProperties = false }, token).ConfigureAwait(false);
                // The library treats unknown envelopes as empty; only expose an
                // empty result for a recognized first-page search envelope.
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || !root.TryGetProperty("contents", out var contents))
                    throw InvalidResponse();
                var section = contents;
                foreach (var name in SearchEnvelopePath)
                {
                    if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(name, out section))
                        throw InvalidResponse();
                }
                if (section.ValueKind != JsonValueKind.Array) throw InvalidResponse();
                foreach (var entry in section.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                    if (entry.TryGetProperty("itemSectionRenderer", out var itemSection) &&
                        (itemSection.ValueKind != JsonValueKind.Object ||
                         !itemSection.TryGetProperty("contents", out var items) || items.ValueKind != JsonValueKind.Array ||
                         items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)))
                        throw InvalidResponse();
                }
                ValidateVideoIds(contents, token);
                return response;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                response.Dispose();
                throw InvalidResponse();
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
