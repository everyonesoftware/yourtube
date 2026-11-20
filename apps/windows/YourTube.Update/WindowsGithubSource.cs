using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
namespace YourTube.Update;

/// <summary>Public, credential-free Windows feed. Never relies on GitHub Latest or release ordering.</summary>
public sealed class WindowsGithubSource : GithubSource
{
    public const string Repository = "https://github.com/everyonesoftware/yourtube";
    public const string Channel = "win-x64";
    public const string PackageId = "YourTubeWindows";
    private const string ManifestName = "yourtube-windows.json";
    private static readonly HttpClient PublicManifestHttp = new();
    private GithubRelease? selectedRelease;
    private Version? selectedVersion;
    private readonly FeedFingerprintDownloader downloads;
    private readonly HttpClient manifestHttp;
    private JsonElement publication;
    private VelopackAsset? feedOffer;

    public WindowsGithubSource(IFileDownloader? downloader = null) : this(downloader, PublicManifestHttp) { }
    internal WindowsGithubSource(IFileDownloader? downloader, HttpClient manifestHttp)
        : this(new FeedFingerprintDownloader(downloader ?? new HttpClientFileDownloader()), manifestHttp) { }
    private WindowsGithubSource(FeedFingerprintDownloader downloads, HttpClient manifestHttp) : base(Repository, null, false, downloads)
    {
        this.downloads = downloads;
        this.manifestHttp = manifestHttp;
    }
    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        feedOffer = null;
        selectedRelease = null;
        selectedVersion = null;
        publication = default;
        downloads.Feed = null;
        if (channel != Channel || appId is not null && appId != PackageId)
            throw new InvalidDataException("Unexpected Windows update identity.");
        var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        foreach (var asset in feed.Assets)
        {
            if (asset is not GitBaseAsset entry || !ReferenceEquals(entry.Release, selectedRelease) || asset.PackageId != PackageId || asset.Version.ToString() != selectedVersion?.ToString() || asset.Type != VelopackAssetType.Full || asset.FileName != $"{PackageId}-{selectedVersion}-{Channel}-full.nupkg" || !entry.Release.Assets.Any(a => a.Name == asset.FileName))
                throw new InvalidDataException("Unexpected Windows update package.");
        }
        if (selectedRelease is not null && feed.Assets.Length != 1)
            throw new InvalidDataException("Windows release must contain exactly one matching full package.");
        feedOffer = feed.Assets.SingleOrDefault();
        return feed;
    }
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        Version? bestVersion = null;
        JsonElement bestRelease = default;
        bool ambiguous = false;
        for (int page = 1; page <= 10; page++)
        {
            string json = await Downloader.DownloadString($"https://api.github.com/repos/everyonesoftware/yourtube/releases?per_page=100&page={page}", GetRequestHeaders("application/vnd.github+json"));
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
                string? tag = item.GetProperty("tag_name").GetString();
                if (tag is null || !tag.StartsWith('v') || WindowsChangelogReader.ParsePublicationVersion(tag[1..]) is not { } version) continue;
                if (version < bestVersion || !item.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").ValueEquals($"releases.{Channel}.json"))) continue;
                if (version == bestVersion) { ambiguous = true; continue; }
                bestVersion = version;
                bestRelease = item.Clone();
                ambiguous = false;
            }
            if (items.Length < 100)
            {
                if (ambiguous)
                    throw new InvalidDataException("Ambiguous Windows release inventory; retry the check.");
                if (bestVersion is null) return [];
                string prefix = $"{Repository}/releases/download/v{bestVersion}/";
                // Exclude notes before deserialization: even malformed notes asset metadata cannot invalidate the feed.
                var packageAssets = bestRelease.GetProperty("assets").EnumerateArray()
                    .Where(a => !string.Equals(a.GetProperty("name").GetString(), ManifestName, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Deserialize(UpdateJsonContext.Default.GithubReleaseAsset) ?? throw new InvalidDataException("Missing Windows asset.")).ToArray();
                if (packageAssets.Any(a => a.BrowserDownloadUrl != prefix + a.Name) || packageAssets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                    throw new InvalidDataException("Unexpected or ambiguous Windows asset origin.");
                publication = bestRelease;
                selectedVersion = bestVersion;
                selectedRelease = new GithubRelease { Assets = packageAssets };
                return [selectedRelease];
            }
        }
        throw new InvalidDataException("GitHub release inventory exceeded the safe paging limit.");
    }

    internal async Task<WindowsChangelog?> GetChangelogAsync(Version installedVersion, string? installedSourceSha, VelopackAsset offer, CancellationToken token)
    {
        if (!ReferenceEquals(feedOffer, offer) || downloads.Feed is not { } feed
            || !WindowsChangelogReader.IsLowerHex(installedSourceSha, 40)
            || WindowsChangelogReader.ParsePublicationVersion(offer.Version.ToString()) is not { } offeredVersion
            || offeredVersion <= installedVersion)
            return null;
        var selected = publication;
        try
        {
            WindowsChangelogReader.RequireUniqueProperties(selected);
            var manifest = WindowsChangelogReader.PublishedAsset(selected, ManifestName);
            long size = manifest.GetProperty("size").GetInt64();
            WindowsChangelogReader.Require(size <= WindowsChangelogReader.MaximumManifestBytes);
            // The publisher verifies the real Git tag. target_commitish is not a tag identity.
            string digest = WindowsChangelogReader.Text(manifest, "digest");
            var bytes = await ReadManifestAsync(WindowsChangelogReader.Text(manifest, "browser_download_url"), (int)size, token);
            WindowsChangelogReader.Require(digest == "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var notes = WindowsChangelogReader.Read(bytes, selected, offer, feed.Sha, feed.Size, installedVersion, installedSourceSha!);
            return ReferenceEquals(feedOffer, offer) ? notes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return null; // Notes are optional; do not replace them with release-body or live Git history.
        }
    }

    private async Task<byte[]> ReadManifestAsync(string url, int size, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("YourTubeWindows-Updates/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await manifestHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        WindowsChangelogReader.Require(response.Content.Headers.ContentLength is not long length || length == size);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, timeout.Token);
        WindowsChangelogReader.Require(await stream.ReadAsync(new byte[1], timeout.Token) == 0);
        return bytes;
    }

    // Fingerprint the exact feed Velopack parsed, without a second request or a second feed parser.
    private sealed class FeedFingerprintDownloader(IFileDownloader inner) : IFileDownloader
    {
        public (string Sha, long Size)? Feed { get; set; }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            var bytes = await inner.DownloadBytes(url, headers, timeout);
            if (url.EndsWith($"/releases.{Channel}.json", StringComparison.Ordinal))
                Feed = (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
            return bytes;
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            inner.DownloadString(url, headers, timeout);

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null,
            double timeout = 30, CancellationToken cancelToken = default) => inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);
    }
}
[JsonSerializable(typeof(GithubReleaseAsset))]
internal partial class UpdateJsonContext : JsonSerializerContext { }
