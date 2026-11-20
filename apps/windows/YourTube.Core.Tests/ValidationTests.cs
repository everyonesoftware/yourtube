using YourTube.Core;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("begin")]
    [InlineData("refresh")]
    [InlineData("accounts")]
    [InlineData("history")]
    [InlineData("poll")]
    public async Task InvalidInputsAreRejectedBeforeNetworkRequests(string operation)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device, ClientTests.Token);
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var client = clock.Client(http);
        var expired = new YouTubeSession("access", "refresh", clock.Now.AddSeconds(-1));
        var error = await Assert.ThrowsAsync<YouTubeException>(async () =>
        {
            switch (operation)
            {
                case "begin": await client.BeginSignInAsync(" "); break;
                case "refresh": await client.RefreshSessionAsync(""); break;
                case "accounts": await client.GetAccountsAsync(expired); break;
                case "history": await client.GetHistoryAsync(expired, new("owner", null, null)); break;
                case "poll": await client.CompleteSignInAsync(new("test", new Uri("https://www.youtube.com/activate"), clock.Now.AddHours(1), 0, "test")); break;
            }
        });
        Assert.Equal(operation is "accounts" or "history" ? "auth_required" : "invalid_argument", error.Code);
        Assert.Empty(transport.Requests);
    }
    [Theory]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"token_type\":\"MAC\"}")]
    [InlineData("{\"access_token\":\"\",\"refresh_token\":\"refresh\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":0}")]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":\"3600\"}")]
    public async Task InvalidTokenResponsesCannotCreateUsableSessions(string response)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, response);
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeClient(http).RefreshSessionAsync("refresh"));
        Assert.Equal("invalid_response", error.Code);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unexpected\":\"data\"}")]
    public async Task UnknownAccountsSchemaIsNotSilentlyPresentedAsNoAccounts(string response)
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, response);
        using var http = new HttpClient(transport);
        var clock = new ClientTests.Clock();
        var error = await Assert.ThrowsAsync<YouTubeException>(() => clock.Client(http).GetAccountsAsync(new("access", "refresh", clock.Now.AddHours(1))));
        Assert.Equal("invalid_response", error.Code);
    }
}
