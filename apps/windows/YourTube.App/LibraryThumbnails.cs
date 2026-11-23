using System.Net;

namespace YourTube.App;

internal sealed class LibraryThumbnails : IDisposable
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxConcurrentLoads = 4;
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim slots = new(MaxConcurrentLoads, MaxConcurrentLoads);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private bool disposed;

    public LibraryThumbnails() : this(CreateTransport()) { }

    internal LibraryThumbnails(HttpMessageHandler transport, TimeSpan? timeout = null)
    {
        http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        this.timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    internal static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        // Do not drain rejected/oversized responses in the background for connection reuse.
        MaxResponseDrainSize = 0
    };

    // Caller cancellation propagates. Invalid IDs, unavailable images, timeout and shutdown return null.
    // Returns encoded bytes only; the caller owns image decoding and controls whether a result is still current.
    public Task<byte[]?> LoadAsync(string? videoId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<byte[]?>(cancellationToken);
        if (videoId is not { Length: 11 } ||
            !videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return Task.FromResult<byte[]?>(null);

        return LoadImageAsync(new Uri($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"), false, cancellationToken);
    }

    public Task<byte[]?> LoadAvatarAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<byte[]?>(cancellationToken);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.IdnHost is not ("lh3.googleusercontent.com" or "yt3.ggpht.com" or "yt3.googleusercontent.com"))
            return Task.FromResult<byte[]?>(null);
        return LoadImageAsync(uri, true, cancellationToken);
    }

    private Task<byte[]?> LoadImageAsync(Uri uri, bool avatar, CancellationToken cancellationToken)
    {
        CancellationTokenSource operation;
        lock (sync)
        {
            if (disposed) return Task.FromResult<byte[]?>(null);
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        }
        return RunAsync(uri, avatar, cancellationToken, operation);
    }

    private async Task<byte[]?> RunAsync(Uri uri, bool avatar, CancellationToken caller, CancellationTokenSource operation)
    {
        using var scope = operation;
        operation.CancelAfter(timeout);
        var token = operation.Token;
        try
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var image = await FetchAsync(uri, avatar, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return image;
            }
            finally { slots.Release(); }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException ||
                                      error is ObjectDisposedException && lifetime.IsCancellationRequested)
        {
            caller.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<byte[]?> FetchAsync(Uri uri, bool avatar, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var content = response.Content;
        var length = content.Headers.ContentLength;
        var mediaType = content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (response.StatusCode != HttpStatusCode.OK || length is 0 or > MaxBytes ||
            (mediaType != "image/jpeg" && !(avatar && mediaType is "image/png" or "image/webp")))
            return null;

        var body = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var image = new MemoryStream((int)(length ?? 0));
        var buffer = new byte[8192];
        var limit = length ?? MaxBytes;
        while (true)
        {
            var count = (int)Math.Min(buffer.Length, limit + 1 - image.Length);
            var read = await body.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0)
                return image.Length > 0 && (length is null || length == image.Length) ? image.ToArray() : null;
            if (image.Length + read > limit) return null;
            image.Write(buffer, 0, read);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }

        try { lifetime.Cancel(); }
        finally
        {
            http.Dispose();
            lifetime.Dispose();
            // A cancelled load can still unwind and release its slot; no wait handle was allocated.
        }
    }
}