using YourTube.Playback;

namespace YourTube.App;

internal readonly record struct PlaybackStartupSettings(int AutoHeight, int ReachabilityLimit);

internal static class PlaybackProgressSummary
{
    public static IReadOnlyList<string> StartupLabels { get; } = Array.AsReadOnly(new[]
    {
        "Find video streams", "Prepare stream data", "Set up the player", "Load initial audio & video", "Start playback"
    });
    public static IReadOnlyList<string> DiscoveryLabels { get; } = Array.AsReadOnly(new[]
    {
        "Get stream list from YouTube", "Check that streams are reachable", "Confirm compatible audio and video"
    });

    // Only typed, non-identifying values enter the summary; never copy UI errors or provider text.
    public static IReadOnlyList<string> ReachabilityLines(StreamDiscoverySnapshot discovery, PlaybackStartupSettings settings, bool includeCount = true)
    {
        var lines = new List<string> { FormattableString.Invariant($"Configured reachability limit: {settings.ReachabilityLimit}") };
        var progress = discovery.ReachabilityProgress;
        if (includeCount && progress is { } count)
            lines.Add(FormattableString.Invariant($"Streams checked: {count.Checked}/{count.Total}"));
        if (discovery.ReachabilityDiagnostics is not { } diagnostics) return lines;

        string mode = diagnostics.Mode switch
        {
            ReachabilityMode.Sequential => "Sequential",
            ReachabilityMode.Parallel => "Parallel",
            ReachabilityMode.Mixed => "Mixed",
            _ => "Unknown"
        };
        lines.Add(diagnostics.PeakInFlight == 0 ? "No CDN checks dispatched" :
            FormattableString.Invariant($"Actual mode: {mode} · Peak in flight: {diagnostics.PeakInFlight}"));
        if (diagnostics.Fallback != ReachabilityFallback.None)
            lines.Add("Fallback reason: " + (diagnostics.Fallback switch
            {
                ReachabilityFallback.ConfiguredSequential => "Sequential limit configured",
                ReachabilityFallback.MissingLength => "Missing stream length",
                ReachabilityFallback.ExternalManifest => "External manifest",
                ReachabilityFallback.CipheredUrl => "Ciphered stream URL",
                ReachabilityFallback.UnpredictableRequests => "Unpredictable requests",
                ReachabilityFallback.RequestMismatch => "Request mismatch",
                ReachabilityFallback.NoPlayerResponse => "No successful player response before checks",
                ReachabilityFallback.NoStreamList => "No stream list in player response",
                ReachabilityFallback.UnreadablePlayerResponse => "Unreadable or ambiguous player response",
                ReachabilityFallback.PlayerNotConfirmedPlayable => "Player response did not confirm playability",
                ReachabilityFallback.InvalidStreamEntry => "Invalid stream entry",
                ReachabilityFallback.UnsupportedStreamUrl => "Unsupported stream URL",
                ReachabilityFallback.ExistingRangeParameter => "Stream URL already has a byte-range parameter",
                ReachabilityFallback.DuplicateStreamUrl => "Duplicate stream URL",
                ReachabilityFallback.NoDirectCandidates => "No direct stream URLs",
                ReachabilityFallback.CandidateCountOutsideWindow => "Parallel checks require 2–128 candidates",
                ReachabilityFallback.MissingDirectUrl => "A stream entry has no direct URL",
                ReachabilityFallback.InvalidFormatId => "Missing or invalid stream format ID",
                ReachabilityFallback.DuplicateFormatId => "Duplicate stream format ID",
                ReachabilityFallback.InvalidLength => "Invalid stream length",
                ReachabilityFallback.ExistingLanguageParameter => "Stream URL already has a language parameter",
                _ => "Unknown"
            }));
        lines.Add(FormattableString.Invariant($"CDN requests: {diagnostics.TailRequests} tail · {diagnostics.SizeRequests} size · {diagnostics.OtherRequests} other · {diagnostics.RedirectHops} redirect hops"));
        lines.Add(FormattableString.Invariant($"Retries: {diagnostics.ProbeRetries} probe · {diagnostics.PlayerRetries} player"));
        var prefetch = FormattableString.Invariant($"Prefetched tail requests: {diagnostics.PrefetchedTailRequests}");
        if (diagnostics.RepeatedFormatEntriesInPlan is int repeated)
            prefetch += FormattableString.Invariant($" · Repeated format entries in latest plan: {repeated}");
        lines.Add(prefetch);
        var samples = new List<string>(2);
        if (diagnostics.MedianCheckMs is long median) samples.Add(FormattableString.Invariant($"Median check: {median} ms"));
        if (diagnostics.SlowestCheckMs is long slowest) samples.Add(FormattableString.Invariant($"Slowest check: {slowest} ms"));
        if (samples.Count > 0) lines.Add(string.Join(" · ", samples));
        if (progress is { Total: >= 4 } full && full.Checked == full.Total && diagnostics.FinalQuarterMs is long finalQuarter)
            lines.Add(FormattableString.Invariant($"Final quarter: {finalQuarter} ms"));
        return lines;
    }

    public static string Format(PlaybackStartupSnapshot state, PlaybackStartupSettings settings, Version version)
    {
        string result = state.Failed ? "Couldn’t start playback" : state.EndedUnconfirmed ? "Video ended before startup was confirmed" :
            state.IsFinal ? "Playback started" : FormattableString.Invariant($"{state.Completed} of {PlaybackStartupSnapshot.StepCount} steps complete");
        var lines = new List<string>
        {
            $"YourTube {version.ToString(3)} — Playback progress",
            $"Startup: {result}",
            $"Total startup time: {Seconds(state.ElapsedMs)}",
            "Auto startup quality preference: " + (settings.AutoHeight == 0 ? "Let Windows decide" : FormattableString.Invariant($"{settings.AutoHeight}p"))
        };
        for (int i = 0; i < StartupLabels.Count; i++)
        {
            lines.Add($"{StartupLabels[i]}: {Stage(state, state.StepDurations[i], i == state.Completed)}");
            if (i != 0) continue;
            for (int j = 0; j < DiscoveryLabels.Count; j++)
            {
                var duration = state.Discovery.Durations[j];
                string label = duration is null && state.Completed > 0 ? "Not needed" : Stage(state, duration, state.Discovery.Current == (StreamDiscoveryStep)j);
                lines.Add($"  {DiscoveryLabels[j]}: {label}");
            }
            lines.AddRange(ReachabilityLines(state.Discovery, settings).Select(line => "  " + line));
        }
        return string.Join('\n', lines);
    }

    private static string Stage(PlaybackStartupSnapshot state, long? elapsed, bool current)
    {
        if (elapsed is not long ms) return state.IsFinal ? "Not run" : "Waiting";
        return Seconds(ms) + (!current ? string.Empty : state.Failed ? " (failed)" : state.EndedUnconfirmed ? " (not confirmed)" : " (so far)");
    }

    private static string Seconds(long ms) => FormattableString.Invariant($"{ms / 1000m:0.000} s");
}
