using System.Globalization;
using YourTube.App;
using YourTube.Playback;

internal static class PlaybackProgressSummaryTests
{
    public static void Run()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var version = new Version(1, 2, 3, 456);
            var settings = new PlaybackStartupSettings(720, 8);
            var diagnostics = new StreamReachabilityDiagnostics(4, ReachabilityMode.Mixed, ReachabilityFallback.MissingLength,
                19, 3, 1, 2, 1, 1, 84, 315, 123) { PrefetchedTailRequests = 12, RepeatedFormatEntriesInPlan = 2 };
            var discovery = new StreamDiscoverySnapshot(null, [450, 1000, 50])
            {
                ReachabilityProgress = new(20, 20),
                ReachabilityDiagnostics = diagnostics
            };
            var success = new PlaybackStartupSnapshot(5, 2680, [1500, 180, 300, 400, 300], false, false) { Discovery = discovery };

            Equal("""
                YourTube 1.2.3 — Playback progress
                Startup: Playback started
                Total startup time: 2.680 s
                Auto startup quality preference: 720p
                Find video streams: 1.500 s
                  Get stream list from YouTube: 0.450 s
                  Check that streams are reachable: 1.000 s
                  Confirm compatible audio and video: 0.050 s
                  Configured reachability limit: 8
                  Streams checked: 20/20
                  Actual mode: Mixed · Peak in flight: 4
                  Fallback reason: Missing stream length
                  CDN requests: 19 tail · 3 size · 1 other · 2 redirect hops
                  Retries: 1 probe · 1 player
                  Prefetched tail requests: 12 · Repeated format entries in latest plan: 2
                  Median check: 84 ms · Slowest check: 315 ms
                  Final quarter: 123 ms
                Prepare stream data: 0.180 s
                Set up the player: 0.300 s
                Load initial audio & video: 0.400 s
                Start playback: 0.300 s
                """.ReplaceLineEndings("\n"), PlaybackProgressSummary.Format(success, settings, version), "exact allowlisted summary and invariant timing");

            var failed = new PlaybackStartupSnapshot(0, 890, [890, null, null, null, null], true, false)
            {
                Discovery = new(StreamDiscoveryStep.Reachability, [450, 440, null])
                {
                    ReachabilityProgress = new(8, 20),
                    ReachabilityDiagnostics = diagnostics with { MedianCheckMs = null }
                }
            };
            var partial = PlaybackProgressSummary.Format(failed, new(0, 1), version);
            Check(partial.Contains("Startup: Couldn’t start playback\nTotal startup time: 0.890 s") &&
                partial.Contains("Auto startup quality preference: Let Windows decide") &&
                partial.Contains("Find video streams: 0.890 s (failed)") &&
                partial.Contains("Check that streams are reachable: 0.440 s (failed)") &&
                partial.Contains("Streams checked: 8/20") && partial.Contains("Slowest check: 315 ms") &&
                !partial.Contains("Median check") && !partial.Contains("Final quarter") &&
                partial.Contains("Prepare stream data: Not run"), "a partial failure keeps observed details without a final-quarter claim");

            var loading = failed with { Failed = false, Discovery = failed.Discovery with { ReachabilityProgress = null, ReachabilityDiagnostics = null } };
            var loadingText = PlaybackProgressSummary.Format(loading, settings, version);
            Check(loadingText.Contains("Startup: 0 of 5 steps complete") &&
                loadingText.Contains("Find video streams: 0.890 s (so far)") &&
                loadingText.Contains("Prepare stream data: Waiting") && !loadingText.Contains("Streams checked") &&
                !loadingText.Contains("Actual mode") && !loadingText.Contains("Final quarter"), "missing data has no fabricated counts, mode, or timings");
            var ended = PlaybackProgressSummary.Format(loading with { EndedUnconfirmed = true }, settings, version);
            Check(ended.Contains("Startup: Video ended before startup was confirmed") &&
                ended.Contains("Find video streams: 0.890 s (not confirmed)") && !ended.Contains("so far"),
                "ended but unconfirmed is distinct from active and failed");

            var smallList = PlaybackProgressSummary.ReachabilityLines(discovery with { ReachabilityProgress = new(3, 3) }, settings);
            Check(!smallList.Any(line => line.StartsWith("Final quarter:", StringComparison.Ordinal)), "fewer than four checks cannot show a final quarter");
            var visibleDetails = PlaybackProgressSummary.ReachabilityLines(discovery, settings, includeCount: false);
            Check(!visibleDetails.Contains("Streams checked: 20/20") && visibleDetails.Contains("Actual mode: Mixed · Peak in flight: 4"),
                "the disclosure retains diagnostics without repeating the checked count shown by its reachability row");

            var specific = discovery with { ReachabilityDiagnostics = diagnostics with { Fallback = ReachabilityFallback.ExistingLanguageParameter } };
            const string specificLabel = "Fallback reason: Stream URL already has a language parameter";
            Check(PlaybackProgressSummary.ReachabilityLines(specific, settings).Contains(specificLabel), "the disclosure shows the fixed eligibility label");
            Equal(PlaybackProgressSummary.Format(success, settings, version).Replace("Fallback reason: Missing stream length", specificLabel, StringComparison.Ordinal),
                PlaybackProgressSummary.Format(success with { Discovery = specific }, settings, version), "the eligibility label is the only copied change");

            foreach (var (mode, label) in new[] { (ReachabilityMode.Sequential, "Sequential"), (ReachabilityMode.Parallel, "Parallel"), ((ReachabilityMode)999, "Unknown") })
            {
                var lines = PlaybackProgressSummary.ReachabilityLines(discovery with
                {
                    ReachabilityDiagnostics = diagnostics with { Mode = mode, Fallback = (ReachabilityFallback)999 }
                }, settings);
                Check(lines.Contains($"Actual mode: {label} · Peak in flight: 4") && lines.Contains("Fallback reason: Unknown"),
                    "unknown enum values are not interpolated");
            }
            Console.WriteLine("Playback progress summary tests passed.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    static void Equal(string expected, string actual, string description) => Check(expected == actual, $"{description}\nExpected:\n{expected}\nActual:\n{actual}");
}
