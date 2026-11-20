using YourTube.Probe;
using Xunit;

namespace YourTube.Core.Tests;

public sealed class ProbeTests
{
    [Fact]
    public async Task DefaultProbeIssuesAndObservesPendingWithoutSecretOutputOrActivation()
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), "{\"error\":\"authorization_pending\"}");
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var activated = false;
        var result = await ProbeRunner.RunAsync(transport, output, false, _ => activated = true, timeout.Token);
        Assert.Equal(0, result);
        Assert.False(activated);
        Assert.Equal(4, transport.Requests.Count);
        Assert.Contains("device_authorization=issued", output.ToString());
        Assert.Contains("token_poll=pending", output.ToString());
        foreach (var secret in new[] { "TEST-CODE", "private-device", "first.apps", "test_secret", "test-access", "test-refresh" }) Assert.DoesNotContain(secret, output.ToString());
    }
    [Fact]
    public async Task ProbeReportsSafeFailuresWithNonzeroExit()
    {
        using var transport = new ClientTests.Transport("private-response");
        using var output = new StringWriter();
        var result = await ProbeRunner.RunAsync(transport, output, false, null, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.Contains("probe_error=bootstrap_failed", output.ToString());
        Assert.DoesNotContain("private-response", output.ToString());
    }
    [Fact]
    public async Task OptionalSignInActivatesLocallyThenCountsRealAccountHistoryPagesWithoutPrintingThem()
    {
        using var transport = new ClientTests.Transport(ClientTests.Html, ClientTests.Script, ClientTests.Device.Replace("\"interval\":5", "\"interval\":1"), ClientTests.Token,
            "{\"contents\":[{\"accountItem\":{\"accountName\":{\"simpleText\":\"Private name\"}}}]}",
            "{\"contents\":{\"items\":[{\"gridVideoRenderer\":{\"videoId\":\"abcdefghijk\",\"title\":{\"simpleText\":\"Private title\"}}}],\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"next\"}}]}}", "{\"continuationContents\":{\"gridContinuation\":{\"items\":[]}}}");
        using var output = new StringWriter();
        var activated = false;
        var result = await ProbeRunner.RunAsync(transport, output, true, _ => activated = true, CancellationToken.None);
        Assert.Equal(0, result);
        Assert.True(activated);
        Assert.Contains("accounts=1", output.ToString());
        Assert.Contains("history_pages=2 videos=1", output.ToString());
        foreach (var secret in new[] { "Private", "abcdefghijk", "test-access", "test-refresh", "TEST-CODE", "private-device" }) Assert.DoesNotContain(secret, output.ToString());
    }
    [Fact]
    public async Task CancelledProbeReturnsSafeNonzeroStatus()
    {
        using var transport = new ClientTests.Transport();
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(130, await ProbeRunner.RunAsync(transport, output, false, null, cancellation.Token));
        Assert.Contains("probe_cancelled=true", output.ToString());
        Assert.Empty(transport.Requests);
    }
}
