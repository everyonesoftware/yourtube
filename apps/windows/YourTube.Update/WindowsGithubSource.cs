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
    private readonly Dictionary<GithubRelease, string> versions = new();
    public WindowsGithubSource(IFileDownloader? downloader = null) : base(Repository, null, false, downloader) { }
    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (channel != Channel || appId is not null && appId != PackageId)
            throw new InvalidDataException("Unexpected Windows update identity.");
        var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        foreach (var asset in feed.Assets)
        {
            if (asset is not GitBaseAsset entry || !versions.TryGetValue(entry.Release, out string? version) || asset.PackageId != PackageId || asset.Version.ToString() != version || asset.Type != VelopackAssetType.Full || asset.FileName != $"{PackageId}-{version}-{Channel}-full.nupkg" || !entry.Release.Assets.Any(a => a.Name == asset.FileName))
                throw new InvalidDataException("Unexpected Windows update package.");
        }
        if (versions.Count > 0 && feed.Assets.Length != 1)
            throw new InvalidDataException("Windows release must contain exactly one matching full package.");
        return feed;
    }
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        versions.Clear();
        var candidates = new List<(GithubRelease Release, Version Version)>();
        for (int page = 1; page <= 10; page++)
        {
            string json = await Downloader.DownloadString($"https://api.github.com/repos/everyonesoftware/yourtube/releases?per_page=100&page={page}", GetRequestHeaders("application/vnd.github+json"));
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
                string? tag = item.GetProperty("tag_name").GetString();
                if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Major != 0 || version.Minor != 1 || version.Build is < 1 or > 65534 || version.Revision != -1 || tag[1..] != version.ToString()) continue;
                var release = item.Deserialize(UpdateJsonContext.Default.GithubRelease) ?? throw new InvalidDataException("Missing Windows release.");
                if (!release.Assets.Any(a => a.Name == $"releases.{Channel}.json")) continue;
                versions.Add(release, version.ToString());
                candidates.Add((release, version));
            }
            if (items.Length < 100)
            {
                var ordered = candidates.OrderByDescending(c => c.Version).ToArray();
                if (ordered.Length > 1 && ordered[0].Version == ordered[1].Version)
                    throw new InvalidDataException("Ambiguous Windows release inventory; retry the check.");
                var selected = ordered.Take(1).Select(c => c.Release).ToArray();
                foreach (var release in selected)
                {
                    string prefix = $"{Repository}/releases/download/v{versions[release]}/";
                    if (release.Assets.Any(a => a.BrowserDownloadUrl != prefix + a.Name) || release.Assets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                        throw new InvalidDataException("Unexpected or ambiguous Windows asset origin.");
                }
                return selected;
            }
        }
        throw new InvalidDataException("GitHub release inventory exceeded the safe paging limit.");
    }
}
[JsonSerializable(typeof(GithubRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext { }
