using System.Text.Json;
using Velopack;

namespace YourTube.Update;

internal static class WindowsChangelogReader
{
    internal const int MaximumManifestBytes = 1024 * 1024;

    internal static WindowsChangelog? Read(byte[] bytes, JsonElement publication, VelopackAsset offer,
        string feedSha, long feedSize, Version installedVersion, string installedSourceSha)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        RequireUniqueProperties(root);
        var version = PublicationVersion(root);
        string sourceSha = Sha(root, "sourceSha");
        string tag = "v" + version;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 && Text(root, "platform") == "windows"
            && Text(root, "packageId") == WindowsGithubSource.PackageId && Text(root, "channel") == WindowsGithubSource.Channel
            && Text(root, "tag") == tag && Text(publication, "tag_name") == tag
            && offer.Version.ToString() == version.ToString());
        ValidateInventory(root, publication, offer, feedSha, feedSize);

        var log = root.GetProperty("changelog");
        var head = log.GetProperty("head");
        Require(log.GetProperty("schema").GetInt32() == 1 && Text(log, "platform") == "windows"
            && PublicationVersion(head) == version && Sha(head, "sha") == sourceSha);

        var improvements = new List<string>();
        var fixes = new List<string>();
        var commits = new List<WindowsChangeCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Version? lastVersion = null;
        string? lastSource = null;
        int lastCount = 0;
        bool installedFound = false;
        foreach (var entry in log.GetProperty("releases").EnumerateArray())
        {
            var entryVersion = PublicationVersion(entry);
            string entrySha = Sha(entry, "sha");
            int count = entry.GetProperty("count").GetInt32();
            var segment = entry.GetProperty("commits");
            Require(count > lastCount && (lastVersion is null || entryVersion > lastVersion));
            Require(segment.GetArrayLength() == (lastVersion is null ? 0 : count - lastCount));
            if (lastVersion is null) seen.Add(entrySha);
            string? finalCommit = null;
            foreach (var item in segment.EnumerateArray())
            {
                string commitSha = Sha(item, "sha");
                string subject = Text(item, "subject");
                // Originals may be empty or contain CR/NUL. LF is outside the Git first line.
                Require(seen.Add(commitSha) && !subject.Contains('\n'));
                var kind = item.GetProperty("kind").GetString();
                var text = item.GetProperty("text").GetString();
                if (kind is null) Require(text is null);
                else
                {
                    Require(kind is "improvement" or "fix" && !string.IsNullOrWhiteSpace(text)
                        && text.AsSpan().IndexOfAny('\r', '\n', '\0') < 0);
                    if (installedFound) (kind == "fix" ? fixes : improvements).Add(text!);
                }
                if (installedFound) commits.Add(new(commitSha, subject));
                finalCommit = commitSha;
            }
            Require(lastVersion is null || finalCommit == entrySha);
            if (entryVersion == installedVersion && entrySha == installedSourceSha) installedFound = true;
            (lastVersion, lastSource, lastCount) = (entryVersion, entrySha, count);
        }
        Require(lastVersion == version && lastSource == sourceSha);
        return installedFound && installedVersion < version
            ? new(installedSourceSha, sourceSha, improvements.AsReadOnly(), fixes.AsReadOnly(), commits.AsReadOnly())
            : null;
    }

    private static void ValidateInventory(JsonElement root, JsonElement publication, VelopackAsset offer, string feedSha, long feedSize)
    {
        string[] names = [$"{WindowsGithubSource.PackageId}-{WindowsGithubSource.Channel}-Setup.exe", $"releases.{WindowsGithubSource.Channel}.json", offer.FileName];
        var assets = root.GetProperty("assets");
        Require(assets.GetArrayLength() == names.Length);
        foreach (string name in names)
        {
            var artifact = assets.EnumerateArray().Single(a => Text(a, "name") == name);
            string hash = Text(artifact, "sha256");
            var asset = PublishedAsset(publication, name);
            long size = asset.GetProperty("size").GetInt64();
            Require(IsLowerHex(hash, 64) && Text(asset, "digest") == "sha256:" + hash);
            _ = PublishedAsset(publication, name + ".sha256");
            if (name == offer.FileName) Require(size == offer.Size && string.Equals(hash, offer.SHA256, StringComparison.OrdinalIgnoreCase));
            if (name == names[1]) Require(size == feedSize && hash == feedSha);
        }
    }

    internal static JsonElement PublishedAsset(JsonElement publication, string name)
    {
        var asset = publication.GetProperty("assets").EnumerateArray().Single(a => string.Equals(Text(a, "name"), name, StringComparison.OrdinalIgnoreCase));
        RequireUniqueProperties(asset);
        string tag = Text(publication, "tag_name");
        Require(Text(asset, "name") == name && Text(asset, "state") == "uploaded" && asset.GetProperty("size").GetInt64() > 0
            && Text(asset, "browser_download_url") == $"{WindowsGithubSource.Repository}/releases/download/{tag}/{name}");
        return asset;
    }

    private static Version PublicationVersion(JsonElement element)
    {
        var version = ParsePublicationVersion(Text(element, "version"));
        return version ?? throw new InvalidDataException("Invalid Windows changelog version.");
    }

    internal static Version? ParsePublicationVersion(string? text) =>
        Version.TryParse(text, out var version) && version.Major == 0 && version.Minor == 1
        && version.Build is >= 1 and <= 65534 && version.Revision == -1 && version.ToString() == text ? version : null;

    private static string Sha(JsonElement element, string name)
    {
        string sha = Text(element, name);
        Require(IsLowerHex(sha, 40));
        return sha;
    }

    internal static bool IsLowerHex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing Windows changelog identity.");
    internal static void Require(bool valid) { if (!valid) throw new InvalidDataException("Invalid Windows changelog metadata."); }

    internal static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(seen.Add(property.Name));
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
    }
}
