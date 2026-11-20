using System.Net;
using YourTube.Core;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void SecretModelsHaveRedactedStringRepresentations()
    {
        var auth = new DeviceAuthorization("private-user", new Uri("https://www.youtube.com/activate"), DateTimeOffset.UtcNow, 5, "private-device");
        var session = new YouTubeSession("private-access", "private-refresh", DateTimeOffset.UtcNow);
        Assert.Equal("DeviceAuthorization [redacted]", auth.ToString());
        Assert.Equal("YouTubeSession [redacted]", session.ToString());
    }
    [Theory]
    [InlineData("begin")]
    [InlineData("refresh")]
    [InlineData("accounts")]
    [InlineData("history")]
    [InlineData("poll")]
    public async Task CancellationPreventsAllNetworkRequests(string operation)
    {
        using var transport = new ClientTests.Transport();
        using var http = new HttpClient(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var session = new YouTubeSession("access", "refresh", clock.Now.AddHours(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "begin": await client.BeginSignInAsync("uuid", cancellation.Token); break;
                case "refresh": await client.RefreshSessionAsync("refresh", cancellation.Token); break;
                case "accounts": await client.GetAccountsAsync(session, cancellation.Token); break;
                case "history": await client.GetHistoryAsync(session, new("owner", null, null), cancellationToken: cancellation.Token); break;
                case "poll": await client.CompleteSignInAsync(new("user", new Uri("https://www.youtube.com/activate"), clock.Now.AddHours(1), 5, "device"), cancellation.Token); break;
            }
        });
        Assert.Empty(transport.Requests);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HttpAuthenticationFailuresAreSafeEvenWithNonJsonBodies(int status)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script);
        transport.Responses.Enqueue(((HttpStatusCode)status, "private-response"));
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetAccountsAsync(new("access", "refresh", clock.Now.AddHours(1))));
        Assert.Equal("auth_required", error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData("UNAUTHENTICATED")]
    [InlineData("PERMISSION_DENIED")]
    public async Task EmbeddedInnertubeAuthenticationErrorsRequireSignIn(string status)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, "{\"error\":{\"code\":401,\"status\":\"" + status + "\",\"message\":\"private-response\"}}");
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetAccountsAsync(new("access", "refresh", clock.Now.AddHours(1))));
        Assert.Equal("auth_required", error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
    }
    [Fact]
    public async Task CancellationWhileWaitingPreventsTokenPoll()
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device);
        using var http = new HttpClient(transport);
        using var cancellation = new CancellationTokenSource();
        var clock = new ClientTests.Clock();
        var client = new YouTubeClient(http, () => clock.Now, (_, ct) => { cancellation.Cancel(); return Task.FromCanceled(ct); });
        var auth = await client.BeginSignInAsync("uuid");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteSignInAsync(auth, cancellation.Token));
        Assert.Equal(3, transport.Requests.Count);
    }
    private sealed class ThrowingTransport(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw (timeout ? new TaskCanceledException("private-response") : new HttpRequestException("private-response"));
    }
    [Theory]
    [InlineData(true, "timeout")]
    [InlineData(false, "service_unavailable")]
    public async Task TransportFailuresNeverLeakExceptionBodies(bool timeout, string expected)
    {
        using var http = new HttpClient(new ThrowingTransport(timeout));
        var error = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeClient(http).BeginSignInAsync("uuid"));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.Null(error.InnerException);
    }
}
