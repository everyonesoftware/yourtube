using YourTube.Core;
using System.Text.Json;
namespace YourTube.Probe;

internal static class ProbeRunner
{
    internal static bool IsLookupBatch(IReadOnlyList<string> videos) => videos.Count is >= 1 and <= 8 &&
        videos.All(YouTubeClient.IsWatchVideoId);

    internal static async Task<int> RunAsync(HttpMessageHandler transport, TextWriter output, bool signIn, Action<DeviceAuthorization>? activate, CancellationToken ct, IReadOnlyList<string>? lookupVideos = null, bool includePlaybackContext = false)
    {
        if (lookupVideos is not null && (!signIn || !IsLookupBatch(lookupVideos)))
        {
            await output.WriteLineAsync("probe_error=invalid_lookup_batch");
            return 2;
        }
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var observer = new Observer(transport, stopped, !signIn);
        using var http = new HttpClient(observer) { Timeout = TimeSpan.FromSeconds(45) };
        var client = new YouTubeClient(http);
        try
        {
            var auth = await client.BeginSignInAsync(Guid.NewGuid().ToString(), stopped.Token);
            await output.WriteLineAsync("device_authorization=issued");
            if (signIn) activate?.Invoke(auth);
            YouTubeSession session;
            try { session = await client.CompleteSignInAsync(auth, stopped.Token); }
            catch (OperationCanceledException) when (observer.Pending && !ct.IsCancellationRequested)
            {
                await output.WriteLineAsync("token_poll=pending");
                await output.WriteLineAsync($"http_requests={observer.Requests}");
                return 0;
            }
            if (!signIn) { await output.WriteLineAsync("probe_error=unexpected_authorization"); return 1; }
            await output.WriteLineAsync("sign_in=authorized");
            var accounts = await client.GetAccountsAsync(session, stopped.Token);
            await output.WriteLineAsync($"accounts={accounts.Count}");
            if (lookupVideos is not null)
            {
                if (accounts.Count != 1)
                {
                    await output.WriteLineAsync("probe_error=single_test_identity_required");
                    return 1;
                }
                for (int i = 0; i < lookupVideos.Count; i++)
                {
                    var timestamp = includePlaybackContext ? await PlayerContextDiscovery.DiscoverAsync(http, lookupVideos[i], stopped.Token) : null;
                    var result = await client.CompareWatchLookupsAsync(session, accounts[0], lookupVideos[i], stopped.Token);
                    await PrintLookupAsync(output, i + 1, "baseline", result.Baseline);
                    await PrintLookupAsync(output, i + 1, "tv_fallback", result.Candidate);
                    if (timestamp is int value)
                    {
                        var candidate = await client.InspectTvWatchContextAsync(session, accounts[0], lookupVideos[i], value, stopped.Token);
                        await PrintLookupAsync(output, i + 1, "tv_with_context", candidate);
                    }
                    else if (includePlaybackContext)
                        await output.WriteLineAsync($"case={i + 1} profile=tv_with_context status=SKIPPED context=not_discovered");
                }
                await output.WriteLineAsync($"http_requests={observer.Requests}");
                return 0;
            }
            var pages = 0;
            var videos = 0;
            foreach (var account in accounts)
            {
                string? next = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < 10; i++)
                {
                    var page = await client.GetHistoryAsync(session, account, next, stopped.Token);
                    pages++;
                    videos += page.Videos.Count;
                    next = page.Continuation;
                    if (next is null || !seen.Add(next)) break;
                }
            }
            await output.WriteLineAsync($"history_pages={pages} videos={videos}");
            await output.WriteLineAsync($"http_requests={observer.Requests}");
            return 0;
        }
        catch (YouTubeException error) { await output.WriteLineAsync($"probe_error={error.Code}"); return 1; }
        catch (OperationCanceledException) { await output.WriteLineAsync("probe_cancelled=true"); return 130; }
    }
    private static Task PrintLookupAsync(TextWriter output, int testCase, string profile, WatchLookupResult result) =>
        output.WriteLineAsync($"case={testCase} profile={profile} status={result.Status} tracking_usable={(result.TrackingUsable ? "yes" : "no")} playback_start_field={(result.PlaybackStartField ? "present" : "absent")} watch_time_field={(result.WatchTimeField ? "present" : "absent")}");

    private sealed class Observer(HttpMessageHandler inner, CancellationTokenSource stop, bool stopOnPending) : DelegatingHandler(inner)
    {
        public bool Pending { get; private set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.AbsolutePath == "/o/oauth2/token")
            {
                try
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "authorization_pending")
                    { Pending = true; if (stopOnPending) stop.Cancel(); }
                }
                catch (JsonException) { /* Core provides the safe error. */ }
            }
            return response;
        }
    }
}
