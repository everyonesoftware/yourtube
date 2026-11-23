using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YourTube.App;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class LibraryThumbnailsTests
{
    private const string VideoId = "Abcd_ef-gh1";
    private static readonly byte[] Image = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8Af//Z");

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/fixture", "image/jpeg")]
    [InlineData("https://yt3.ggpht.com/fixture", "image/png")]
    [InlineData("https://yt3.googleusercontent.com/fixture", "image/webp")]
    public async Task AvatarUsesCredentialFreeImageTransport(string url, string contentType)
    {
        var uri = new Uri(url);
        var http = new ThumbnailHttp((request, _) =>
        {
            var response = Jpeg();
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }, url);
        using var thumbnails = new LibraryThumbnails(http);
        Assert.Equal(Image, await thumbnails.LoadAvatarAsync(uri, CancellationToken.None));
        Assert.Equal(1, http.RequestCount);
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com.example.com/fixture")]
    [InlineData("http://lh3.googleusercontent.com/fixture")]
    [InlineData("https://user@lh3.googleusercontent.com/fixture")]
    [InlineData("https://lh3.googleusercontent.com:8443/fixture")]
    [InlineData("https://lh3.googleusercontent.com/fixture#fragment")]
    public async Task UnsafeAvatarUriNeverReachesTransport(string url)
    {
        var http = new ThumbnailHttp((_, _) => throw new InvalidOperationException("Unexpected request"));
        using var thumbnails = new LibraryThumbnails(http);
        Assert.Null(await thumbnails.LoadAvatarAsync(new Uri(url), CancellationToken.None));
        Assert.Equal(0, http.RequestCount);
    }

    [Fact]
    public async Task LoadsJpegViaCanonicalCredentialFreeGet()
    {
        var http = new ThumbnailHttp((_, _) => Task.FromResult(Jpeg()));
        using var thumbnails = new LibraryThumbnails(http);

        Assert.Equal(Image, await thumbnails.LoadAsync(VideoId, CancellationToken.None));
        Assert.Equal(1, http.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aaaaaaaaaa/")]
    [InlineData("aaaaaaaaaa?")]
    [InlineData("aaaaaaaaaaé")]
    [InlineData("aaaaaaaaaaaa")]
    public async Task InvalidVideoIdsNeverReachTransport(string? id)
    {
        var http = new ThumbnailHttp((_, _) => throw new InvalidOperationException("Unexpected request"));
        using var thumbnails = new LibraryThumbnails(http);

        Assert.Null(await thumbnails.LoadAsync(id, CancellationToken.None));
        Assert.Equal(0, http.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "image/jpeg")]
    [InlineData(HttpStatusCode.NotFound, "image/jpeg")]
    [InlineData(HttpStatusCode.PartialContent, "image/jpeg")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    [InlineData(HttpStatusCode.OK, null)]
    public async Task RejectsNonJpegResponsesBeforeReadingBody(HttpStatusCode status, string? mediaType)
    {
        var body = new CountingBody(Image.Length);
        var http = new ThumbnailHttp((_, _) => Task.FromResult(Response(status, body, mediaType)));
        using var thumbnails = new LibraryThumbnails(http);

        Assert.Null(await thumbnails.LoadAsync(VideoId, CancellationToken.None));
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, http.RequestCount);
    }

    private const int Limit = 256 * 1024;

    [Theory]
    [InlineData(Limit, false, Limit)]
    [InlineData(Limit + 1, true, 0)]
    [InlineData(Limit + 10000, false, Limit + 1)]
    [InlineData(0, false, 0)]
    public async Task BoundsActualBodyEvenWithoutContentLength(int length, bool declared, int expectedBytesRead)
    {
        var body = new CountingBody(length);
        var response = Response(HttpStatusCode.OK, body, "image/jpeg");
        if (declared) response.Content.Headers.ContentLength = length;
        var http = new ThumbnailHttp((_, _) => Task.FromResult(response));
        using var thumbnails = new LibraryThumbnails(http);

        var loaded = await thumbnails.LoadAsync(VideoId, CancellationToken.None);
        if (length == Limit) Assert.Equal(Limit, loaded?.Length);
        else Assert.Null(loaded);
        Assert.Equal(expectedBytesRead, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task NetworkFailureReturnsNoThumbnailAndDoesNotPoisonRetry()
    {
        var attempts = 0;
        var http = new ThumbnailHttp((_, _) => ++attempts == 1
            ? throw new HttpRequestException("Synthetic network failure") : Task.FromResult(Jpeg()));
        using var thumbnails = new LibraryThumbnails(http);

        Assert.Null(await thumbnails.LoadAsync(VideoId, CancellationToken.None));
        Assert.Equal(Image, await thumbnails.LoadAsync(VideoId, CancellationToken.None));
        Assert.Equal(2, http.RequestCount);
    }

    [Fact]
    public async Task LimitsConcurrentBodiesAndCancelsQueuedLoadWithoutSending()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bodies = Enumerable.Range(0, 4).Select(_ => new HeldBody()).ToArray();
        var requests = 0;
        var http = new ThumbnailHttp((_, _) =>
        {
            var index = Interlocked.Increment(ref requests) - 1;
            return Task.FromResult(index < bodies.Length ? Response(HttpStatusCode.OK, bodies[index], "image/jpeg") : Jpeg());
        });
        using var thumbnails = new LibraryThumbnails(http);
        var active = bodies.Select(_ => thumbnails.LoadAsync(VideoId, deadline.Token)).ToArray();
        await Task.WhenAll(bodies.Select(b => b.Started.Task)).WaitAsync(deadline.Token);

        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelled = thumbnails.LoadAsync(VideoId, queuedCancellation.Token);
        var next = thumbnails.LoadAsync(VideoId, deadline.Token);
        Assert.Equal(4, http.RequestCount);
        await queuedCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(deadline.Token));
        Assert.Equal(4, http.RequestCount);

        bodies[0].Release.SetResult();
        Assert.Equal(Image, await next.WaitAsync(deadline.Token));
        foreach (var body in bodies.Skip(1)) body.Release.SetResult();
        await Task.WhenAll(active).WaitAsync(deadline.Token);
        Assert.Equal(5, http.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationDuringBodyPropagatesAndDisposesResponse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var body = new HeldBody();
        using var thumbnails = new LibraryThumbnails(new ThumbnailHttp((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body, "image/jpeg"))));

        var load = thumbnails.LoadAsync(VideoId, caller.Token);
        await body.Started.Task.WaitAsync(deadline.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(deadline.Token));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task DisposingLoaderCancelsPendingBody()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = new HeldBody();
        using var thumbnails = new LibraryThumbnails(new ThumbnailHttp((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body, "image/jpeg"))));

        var load = thumbnails.LoadAsync(VideoId, deadline.Token);
        await body.Started.Task.WaitAsync(deadline.Token);
        thumbnails.Dispose();
        Assert.Null(await load.WaitAsync(deadline.Token));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task DeadlineAppliesAfterHeaders()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = new HeldBody();
        using var thumbnails = new LibraryThumbnails(new ThumbnailHttp((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body, "image/jpeg"))), TimeSpan.FromMilliseconds(200));

        var load = thumbnails.LoadAsync(VideoId, deadline.Token);
        await body.Started.Task.WaitAsync(deadline.Token);
        Assert.Null(await load.WaitAsync(deadline.Token));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task ProductionTransportDoesNotFollowRedirectOrReplayCookiesOverHttps()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=i.ytimg.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel cannot use the ephemeral key attached by CreateSelfSigned. Import the disposable
        // fixture key without EphemeralKeySet or PersistKeySet; no certificate trust is installed.
        var pfx = generated.Export(X509ContentType.Pkcs12);
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, (string?)null);
        CryptographicOperations.ZeroMemory(pfx);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        using var transport = LibraryThumbnails.CreateTransport();
        transport.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) => cert.Equals(presented);
        transport.ConnectCallback = async (context, ct) =>
        {
            Assert.InRange(Interlocked.Increment(ref connections), 1, 2);
            Assert.Equal("i.ytimg.com", context.DnsEndPoint.Host);
            Assert.Equal(443, context.DnsEndPoint.Port);
            Assert.Equal($"/vi/{VideoId}/mqdefault.jpg", context.InitialRequestMessage.RequestUri?.AbsolutePath);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(listener.LocalEndpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        using var thumbnails = new LibraryThumbnails(transport);

        var redirect = ExchangeAsync("302 Found", "Location: https://i.ytimg.com/vi/Abcd_ef-gh1/unexpected.jpg\r\nSet-Cookie: fixture=only; Path=/; Secure\r\n", []);
        Assert.Null(await thumbnails.LoadAsync(VideoId, token));
        await redirect;
        Assert.Equal(1, Volatile.Read(ref connections));

        var success = ExchangeAsync("200 OK", "Content-Type: image/jpeg\r\n", Image);
        Assert.Equal(Image, await thumbnails.LoadAsync(VideoId, token));
        await success;
        Assert.Equal(2, Volatile.Read(ref connections));

        async Task ExchangeAsync(string status, string responseHeaders, byte[] payload)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var tls = new SslStream(peer.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, token);
            using var reader = new StreamReader(tls, leaveOpen: true);
            Assert.Equal($"GET /vi/{VideoId}/mqdefault.jpg HTTP/1.1", await reader.ReadLineAsync(token));
            Assert.Equal("Host: i.ytimg.com", await reader.ReadLineAsync(token));
            Assert.Equal("", await reader.ReadLineAsync(token));
            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n{responseHeaders}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), token);
            await tls.WriteAsync(payload, token);
        }
    }

    private sealed class HeldBody : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, Stream body, string? mediaType)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        if (mediaType is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://example.invalid/redirect.jpg");
        return response;
    }

    private sealed class CountingBody(int length) : MemoryStream(new byte[length])
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }


    private static HttpResponseMessage Jpeg() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Image) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }
    };

    // A strict HTTP boundary: no fallback to the network and no substitute loader.
    private sealed class ThumbnailHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string expectedUrl = $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg")
        : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref requestCount);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(expectedUrl, request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Empty(request.Headers);
            return respond(request, token);
        }
    }
}