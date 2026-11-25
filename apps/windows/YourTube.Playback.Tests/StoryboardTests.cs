using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class StoryboardTests
{
    private const string Id = "aqz-KE-bpKQ";
    private const string Template = "https://i.ytimg.com/sb/" + Id + "/storyboard3_L$L/$N.jpg?sqp=fixture";
    private const string Levels = "|48#27#100#10#10#0#default#rs$zero|80#45#128#10#10#5000#M$M#rs$one|160#90#128#5#5#5000#M$M#rs$two";

    [Fact]
    public void MapsTimeToSheetAndTileAndPreservesOriginalLevelIndex()
    {
        var storyboard = Assert.IsType<Storyboard>(Storyboard.Read(Player(Template + Levels), Id));
        Assert.Equal(new StoryboardFrame(0, 0, 0, 160, 90), storyboard.FrameAt(0));
        Assert.Equal(new StoryboardFrame(0, 4, 4, 160, 90), storyboard.FrameAt(124.999));
        Assert.Equal(new StoryboardFrame(1, 0, 0, 160, 90), storyboard.FrameAt(125));
        Assert.Equal(new StoryboardFrame(5, 2, 0, 160, 90), storyboard.FrameAt(639));
        Assert.Null(storyboard.FrameAt(640));
        Assert.Null(storyboard.FrameAt(-1));
        Assert.Null(storyboard.FrameAt(double.NaN));
        Assert.Null(storyboard.FrameAt(double.PositiveInfinity));
        Assert.Equal("https://i.ytimg.com/sb/" + Id + "/storyboard3_L2/M5.jpg?sqp=fixture&sigh=rs$two", storyboard.SheetUri(5)!.AbsoluteUri);
        Assert.Null(storyboard.SheetUri(6));
        Assert.DoesNotContain("fixture", storyboard.ToString());
    }

    [Theory]
    [InlineData("https://i.ytimg.com.evil.test/")]
    [InlineData("https://user@i.ytimg.com/")]
    [InlineData("https://i.ytimg.com:444/")]
    [InlineData("http://i.ytimg.com/")]
    public void RejectsNonProviderOrigin(string origin) =>
        Assert.Null(Storyboard.Read(Player(Template.Replace("https://i.ytimg.com/", origin) + Levels), Id));

    [Theory]
    [InlineData("/sb/", "/other/")]
    [InlineData("aqz-KE-bpKQ", "jNQXAC9IVRw")]
    [InlineData("/sb/", "/other/../sb/")]
    [InlineData("$N.jpg", "$N.jpg#fragment")]
    [InlineData("sqp=", "sqp=bad&sigh=")]
    [InlineData("rs$two", "bad&sigh=second")]
    [InlineData("#5#5#5000", "#5000#5#5000")]
    [InlineData("#5#5#5000", "#5#5#0")]
    public void RejectsUnsafeTemplateOrGeometry(string oldValue, string replacement)
    {
        var spec = (Template + "|160#90#128#5#5#5000#M$M#rs$two").Replace(oldValue, replacement);
        Assert.Null(Storyboard.Read(Player(spec), Id));
    }

    [Fact]
    public void RejectsUnboundBlockedLiveAndMalformedResponses()
    {
        Assert.Null(Storyboard.Read(Player(Template + Levels, "jNQXAC9IVRw"), Id));
        Assert.Null(Storyboard.Read(Player(Template + Levels, status: "LOGIN_REQUIRED"), Id));
        Assert.Null(Storyboard.Read(Player(Template + Levels, live: true), Id));
        Assert.Null(Storyboard.Read("{"u8.ToArray(), Id));
        Assert.Null(Storyboard.Read(Player(Template + Levels + new string('a', 20_000)), Id));
        Assert.Null(Storyboard.Read(Player(null), Id));
    }

    [Fact]
    public void UsesTheObservedEvenPixelGridForAnOddNominalWidth()
    {
        var storyboard = Assert.IsType<Storyboard>(Storyboard.Read(Player(Template + Levels.Replace("160#90", "159#90")), Id));
        Assert.Equal(new StoryboardFrame(0, 4, 4, 160, 90), storyboard.FrameAt(124));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("partial")]
    [InlineData("html")]
    [InlineData("encoded")]
    [InlineData("truncated")]
    [InlineData("wrong-length")]
    [InlineData("oversized-length")]
    [InlineData("oversized-stream")]
    [InlineData("oversized-dimensions")]
    public async Task RejectsUnsafeSheetResponseWithoutFollowingRedirects(string scenario)
    {
        var storyboard = Assert.IsType<Storyboard>(Storyboard.Read(Player(Template + Levels), Id));
        int requests = 0;
        using var transport = new HttpMessageInvoker(new Handler(_ =>
        {
            requests++;
            var image = ImageResponse();
            switch (scenario)
            {
                case "redirect": image.StatusCode = HttpStatusCode.Found; image.Headers.Location = new("https://i.ytimg.com/other.jpg"); break;
                case "partial": image.StatusCode = HttpStatusCode.PartialContent; break;
                case "html": image.Content.Headers.ContentType = new("text/html"); break;
                case "encoded": image.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "wrong-length": image.Content.Headers.ContentLength = Jpeg.Length + 1; break;
                case "oversized-length": image.Content.Headers.ContentLength = StoryboardSheet.MaximumBytes + 1; break;
                default:
                    image.Content.Dispose();
                    var bytes = scenario switch
                    {
                        "oversized-stream" => new byte[StoryboardSheet.MaximumBytes + 1],
                        "truncated" => Jpeg[..^2],
                        _ => Jpeg
                    };
                    if (scenario == "oversized-dimensions") bytes[9] = 100;
                    image.Content = new StreamContent(new ChunkedStream(bytes)) { Headers = { ContentType = new("image/jpeg") } };
                    break;
            }
            return image;
        }));
        Assert.Null(await StoryboardSheet.ReadAsync(transport, storyboard, 0, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DoesNotCarryAStoryboardPastAReplacementPlayerResponse()
    {
        int requests = 0;
        using var transport = new PublicTransport(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Player(++requests == 1 ? Template + Levels : null))
        }));
        using var http = new HttpClient(transport);
        const string player = "https://www.youtube.com/youtubei/v1/player";
        var result = await transport.DiscoverAsync(Id, null, 1, CancellationToken.None, async () =>
        {
            using var first = await http.PostAsync(player, null);
            using var fallback = await http.PostAsync(player, null);
            return true;
        });
        Assert.Null(result.Storyboard);
        Assert.Equal(2, requests);
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    internal static byte[] Player(string? spec, string id = Id, string status = "OK", bool live = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        videoDetails = new { videoId = id, isLiveContent = live },
        playabilityStatus = new { status },
        storyboards = new { playerStoryboardSpecRenderer = new { spec } }
    });

    // Minimal synthetic JPEG marker envelope; tests transport checks, not image decoding.
    internal static byte[] Jpeg => [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 90, 0, 160, 1, 1, 0x11, 0, 0xff, 0xd9];
    internal static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Jpeg) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }
    };
}
