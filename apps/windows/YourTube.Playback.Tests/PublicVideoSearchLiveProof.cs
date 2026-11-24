using Xunit;
using Xunit.Abstractions;

namespace YourTube.Playback.Tests;

public sealed class PublicVideoSearchLiveProof(ITestOutputHelper output)
{
    [LiveSearchFact]
    public async Task CredentialFreePublicSearchReturnsAKnownVideo()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = await new PublicVideoSearch().SearchAsync("Me at the zoo jawed", deadline.Token);
        var knownVideoFound = results.Any(item => item.VideoId == "jNQXAC9IVRw" &&
            !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Channel));
        output.WriteLine($"public_video_results={results.Count} known_video_with_metadata={knownVideoFound}");
        Assert.True(knownVideoFound, "The known public video with title and channel was not returned.");
    }

    private sealed class LiveSearchFactAttribute : FactAttribute
    {
        public LiveSearchFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("YOURTUBE_PUBLIC_SEARCH_PROOF") != "1")
                Skip = "Opt in with YOURTUBE_PUBLIC_SEARCH_PROOF=1.";
        }
    }
}
