using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using YourTube.Update;
using Xunit;
namespace YourTube.Update.Tests;
public sealed class WindowsFeedTests
{
    [Fact]
    public async Task Finds_numeric_highest_stable_windows_feed_beyond_first_page()
    {
        var downloader = new Downloader();
        downloader.Pages[1] = Enumerable.Repeat(Release("v0.1.99", ["other.apk"]), 99).Prepend(Release("v0.1.9")).ToArray();
        downloader.Pages[2] = [Release("v0.1.20"), Release("v0.1.98", prerelease: true), Release("v0.1.97", draft: true)];
        var source = new WindowsGithubSource(downloader);
        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "YourTubeWindows", "win-x64");
        var entry = Assert.Single(feed.Assets);
        Assert.Equal("0.1.20", entry.Version.ToString());
        await source.DownloadReleaseEntry(NullVelopackLogger.Instance, entry, "unused", _ => {}, CancellationToken.None);
        Assert.Equal(4, downloader.Requests.Count);
        Assert.EndsWith("/v0.1.20/YourTubeWindows-0.1.20-win-x64-full.nupkg", downloader.Requests.Last());
        Assert.DoesNotContain(downloader.Requests, u => u.Contains("/latest"));
        Assert.False(downloader.HasAuthorization);
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("origin")]
    [InlineData("version")]
    [InlineData("package")]
    [InlineData("architecture")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task Rejects_unsafe_selected_feed_without_stale_fallback(string fault)
    {
        var d = new Downloader();
        d.Pages[1] = [Release("v0.1.20", wrongOrigin: fault == "origin"), Release("v0.1.19")];
        if (fault == "missing") d.Pages[1] = [Release("v0.1.20", ["releases.win-x64.json"]), Release("v0.1.19")];
        if (fault == "version") d.Feed = d.Feed.Replace("0.1.20", "0.1.21");
        if (fault == "package") d.Feed = d.Feed.Replace("YourTubeWindows", "OtherApp");
        if (fault == "architecture") d.Feed = d.Feed.Replace("win-x64", "win-arm64");
        if (fault == "empty") d.Feed = "{\"Assets\":[]}";
        if (fault == "duplicate") d.Pages[1] = [Release("v0.1.20", ["releases.win-x64.json", "RELEASES.WIN-X64.JSON", "YourTubeWindows-0.1.20-win-x64-full.nupkg"])];
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsGithubSource(d).GetReleaseFeed(NullVelopackLogger.Instance, fault == "identity" ? "OtherApp" : "YourTubeWindows", "win-x64"));
        Assert.DoesNotContain(d.Requests, u => u.Contains("/v0.1.19/"));
    }
    [Fact]
    public async Task Full_tenth_page_fails_closed()
    {
        var d = new Downloader();
        for (int page = 1; page <= 10; page++) d.Pages[page] = Enumerable.Repeat(Release("v0.1.99", ["other.apk"]), 100).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsGithubSource(d).GetReleaseFeed(NullVelopackLogger.Instance, "YourTubeWindows", "win-x64"));
        Assert.Equal(10, d.Requests.Count);
    }
    [Fact]
    public async Task Unpublished_inventory_is_empty_not_invented()
    {
        var d = new Downloader(); d.Pages.Clear();
        Assert.Empty((await new WindowsGithubSource(d).GetReleaseFeed(NullVelopackLogger.Instance, "YourTubeWindows", "win-x64")).Assets);
        Assert.Single(d.Requests);
    }
    [Fact]
    public async Task Duplicate_selected_tag_is_ambiguous_not_arbitrarily_selected()
    {
        var d = new Downloader(); d.Pages[1] = [Release("v0.1.20"), Release("v0.1.20")];
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsGithubSource(d).GetReleaseFeed(NullVelopackLogger.Instance, "YourTubeWindows", "win-x64"));
    }
    internal static object Release(string tag, string[]? names = null, bool prerelease = false, bool draft = false, bool wrongOrigin = false) => new {
        tag_name = tag, prerelease, draft,
        assets = (names ?? ["releases.win-x64.json", $"YourTubeWindows-{tag[1..]}-win-x64-full.nupkg"])
            .Select(name => new { name, browser_download_url = (wrongOrigin ? "https://example.com" : WindowsGithubSource.Repository) + $"/releases/download/{tag}/{name}" }).ToArray()
    };
    internal sealed class Downloader : IFileDownloader
    {
        public Dictionary<int, object[]> Pages = new() { [1] = [Release("v0.1.20")] };
        public List<string> Requests = [];
        public bool HasAuthorization;
        public string Feed = """{"Assets":[{"PackageId":"YourTubeWindows","Version":"0.1.20","Type":"Full","FileName":"YourTubeWindows-0.1.20-win-x64-full.nupkg","Size":100,"SHA256":"ABCD"}]}""";
        private void Track(string url, IDictionary<string, string>? headers) { Requests.Add(url); HasAuthorization |= headers?.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true; }
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) { Track(url, headers); return Task.FromResult(JsonSerializer.Serialize(Pages.GetValueOrDefault(int.Parse(url.Split("&page=")[1]), []))); }
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) { Track(url, headers); return Task.FromResult(Encoding.UTF8.GetBytes(Feed)); }
        public Task DownloadFile(string url, string targetFile, Action<int>? progress = null, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) { Track(url, headers); return Task.CompletedTask; }
    }
}
