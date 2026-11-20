using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;
using YourTube.App;

namespace YourTube.Update.Tests;

public sealed class WindowsChangelogTests
{
    [Fact]
    public async Task Delayed_notes_do_not_block_the_offer_or_verified_download()
    {
        var response = new TaskCompletionSource();
        using var fixture = new Fixture { HoldManifest = response.Task };
        using var flow = new WindowsUpdateFlow(fixture.Backend());
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Changed += () => { if (flow.State.Changelog is not null) loaded.TrySetResult(); };
        try
        {
            await flow.CheckAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UpdatePhase.Available, flow.State.Phase);
            Assert.True(flow.State.NotesLoading);
            Assert.EndsWith("/yourtube-windows.json", fixture.Requests[^1]);
            await flow.DownloadAsync();
            Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
        }
        finally { response.SetResult(); }
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<WindowsChangelog>(flow.State.Changelog);
        Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
    }

    [Fact]
    public async Task Published_manifest_does_not_require_target_commitish_to_be_a_sha()
    {
        using var fixture = new Fixture { Fault = "branch-target" };
        var backend = fixture.Backend();
        Assert.Equal(new Version(0, 1, 20), await backend.CheckAsync());
        Assert.NotNull(await backend.LoadChangelogAsync(default));
    }

    [Fact]
    public async Task Retry_only_fetches_notes_for_the_verified_offer()
    {
        using var fixture = new Fixture { Fault = "http" };
        using var flow = new WindowsUpdateFlow(fixture.Backend());
        await flow.CheckAsync();
        Assert.Equal(UpdatePhase.Available, flow.State.Phase);
        Assert.False(flow.State.NotesLoading);
        Assert.Null(flow.State.Changelog);

        fixture.Fault = null;
        await flow.RetryNotesAsync();
        Assert.Equal(UpdatePhase.Available, flow.State.Phase);
        Assert.IsType<WindowsChangelog>(flow.State.Changelog);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.EndsWith("/yourtube-windows.json", fixture.Requests[^1]);
        Assert.Equal(fixture.Requests[^2], fixture.Requests[^1]);
        await flow.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
    }

    [Fact]
    public async Task Real_manager_propagates_complete_skipped_range_without_history_requests()
    {
        using var fixture = new Fixture();
        var backend = fixture.Backend();
        Assert.Equal(new Version(0, 1, 20), await backend.CheckAsync());
        var notes = Assert.IsType<WindowsChangelog>(await backend.LoadChangelogAsync(default));
        Assert.Equal(Fixture.Base, notes.BaseRevision);
        Assert.Equal(Fixture.Head, notes.HeadRevision);
        Assert.Equal(["Playback improvement"], notes.Improvements);
        Assert.Equal(["Correct rendering"], notes.Fixes);
        Assert.Equal([Fixture.Intervening, Fixture.Middle, Fixture.Head], notes.Commits.Select(c => c.Sha));
        Assert.Equal(["[verified] docs: original\r\0", "feat(player): original", "fix(ui): original"], notes.Commits.Select(c => c.Subject));

        Assert.Collection(fixture.Requests,
            r => Assert.EndsWith("/releases?per_page=100&page=1", r),
            r => Assert.EndsWith("/v0.1.20/releases.win-x64.json", r),
            r => Assert.EndsWith("/v0.1.20/yourtube-windows.json", r));
    }

    [Fact]
    public async Task Intermediate_install_excludes_earlier_segments_and_allows_empty_summary()
    {
        using var fixture = new Fixture { Fault = "details-only" };
        var backend = fixture.Backend("0.1.9", Fixture.Middle);
        await backend.CheckAsync();
        var notes = Assert.IsType<WindowsChangelog>(await backend.LoadChangelogAsync(default));
        Assert.Equal(Fixture.Middle, notes.BaseRevision);
        Assert.Empty(notes.Improvements);
        Assert.Empty(notes.Fixes);
        var commit = Assert.Single(notes.Commits);
        Assert.Equal(Fixture.Head, commit.Sha);
        Assert.Equal("", commit.Subject);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("no-manifest")]
    [InlineData("http")]
    [InlineData("nonjson")]
    [InlineData("too-large")]
    [InlineData("duplicate-field")]
    [InlineData("invalid-root-version")]
    [InlineData("invalid-root-schema")]
    [InlineData("invalid-root-platform")]
    [InlineData("invalid-root-package")]
    [InlineData("invalid-root-channel")]
    [InlineData("invalid-tag")]
    [InlineData("no-manifest-digest")]
    [InlineData("bad-manifest-digest")]
    [InlineData("invalid-source")]
    [InlineData("invalid-head")]
    [InlineData("invalid-schema")]
    [InlineData("invalid-platform")]
    [InlineData("baseline-commits")]
    [InlineData("invalid-version-order")]
    [InlineData("invalid-count-order")]
    [InlineData("omitted-commit")]
    [InlineData("invalid-tail")]
    [InlineData("duplicate-commit")]
    [InlineData("duplicate-source")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-text")]
    [InlineData("invalid-subject")]
    [InlineData("invalid-kind-text-pair")]
    [InlineData("bad-feed-digest")]
    [InlineData("bad-package-hash")]
    [InlineData("wrong-package-size")]
    [InlineData("duplicate-artifact")]
    [InlineData("missing-checksum")]
    [InlineData("wrong-manifest-origin")]
    [InlineData("malformed-manifest-url")]
    [InlineData("ambiguous-manifest")]
    [InlineData("wrong-manifest-size")]
    public async Task Invalid_or_absent_notes_do_not_invalidate_offer(string fault)
    {
        using var fixture = new Fixture { Fault = fault };
        var backend = fixture.Backend();
        Assert.Equal(new Version(0, 1, 20), await backend.CheckAsync());
        Assert.Null(await backend.LoadChangelogAsync(default));
        if (fault is "no-manifest" or "wrong-manifest-origin" or "malformed-manifest-url" or "ambiguous-manifest" or "too-large")
            Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("1111111111111111111111111111111111111111", "0.1.9")]
    [InlineData("2222222222222222222222222222222222222222", "0.1.8")]
    [InlineData(null, "0.1.8")]
    [InlineData("1111111111111111111111111111111111111111", "0.1.20")]
    public async Task Missing_exact_installed_mapping_never_invents_notes(string? source, string installedVersion)
    {
        using var fixture = new Fixture();
        var backend = fixture.Backend(installedVersion, source);
        var offer = await backend.CheckAsync();
        Assert.Equal(installedVersion == "0.1.20" ? null : new Version(0, 1, 20), offer);
        Assert.Null(await backend.LoadChangelogAsync(default));
        if (source is null || installedVersion == "0.1.20") Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task Same_version_recheck_drops_notes_from_previous_response()
    {
        using var fixture = new Fixture();
        var backend = fixture.Backend();
        await backend.CheckAsync();
        Assert.NotNull(await backend.LoadChangelogAsync(default));
        fixture.Fault = "http";
        Assert.Equal(new Version(0, 1, 20), await backend.CheckAsync());
        Assert.Null(await backend.LoadChangelogAsync(default));
    }

    [Fact]
    public async Task Real_manager_downloads_and_verifies_offered_bytes_when_notes_are_unavailable()
    {
        using var fixture = new Fixture { Fault = "legacy" };
        var backend = fixture.Backend();
        var version = Assert.IsType<Version>(await backend.CheckAsync());
        Assert.Null(await backend.LoadChangelogAsync(default));
        Assert.Equal(3, fixture.Requests.Count);
        int progress = 0;
        await backend.DownloadAsync(version, value => progress = value, CancellationToken.None);
        Assert.Equal(100, progress);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.EndsWith("/YourTubeWindows-0.1.20-win-x64-full.nupkg", fixture.Requests[^1]);
    }

    private sealed class Fixture : HttpMessageHandler, IFileDownloader
    {
        internal const string Base = "1111111111111111111111111111111111111111";
        internal const string Intervening = "2222222222222222222222222222222222222222";
        internal const string Middle = "3333333333333333333333333333333333333333";
        internal const string Head = "4444444444444444444444444444444444444444";
        private const string Setup = "YourTubeWindows-win-x64-Setup.exe";
        private const string Package = "YourTubeWindows-0.1.20-win-x64-full.nupkg";
        private const string Feed = "releases.win-x64.json";
        private const string Notes = "yourtube-windows.json";
        private static readonly byte[] PackageBytes = new byte[100]; // Checksum fixture, not an installable package.
        private static readonly string PackageHash = Hash(PackageBytes);
        private static readonly string SetupHash = new('b', 64);
        private static readonly byte[] FeedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Assets = new[] { new { PackageId = "YourTubeWindows", Version = "0.1.20", Type = "Full", FileName = Package, Size = 100, SHA256 = PackageHash.ToUpperInvariant() } } }));
        private const string Prefix = WindowsGithubSource.Repository + "/releases/download/v0.1.20/";
        private readonly string directory = Directory.CreateTempSubdirectory("yourtube-update-tests-").FullName;
        internal readonly List<string> Requests = [];
        internal string? Fault;
        internal Task? HoldManifest;
        private HttpClient? http;

        internal WindowsUpdateBackend Backend(string installedVersion = "0.1.8", string? installedSha = Base)
        {
            http = new HttpClient(this, disposeHandler: false);
            var source = new WindowsGithubSource(this, http);
            var locator = new TestVelopackLocator("YourTubeWindows", installedVersion, directory, logger: NullVelopackLogger.Instance);
            var manager = new UpdateManager(source, WindowsUpdateBackend.CreateOptions(), locator);
            return new(() => new VelopackUpdateEngine(source, manager, installedSha));
        }

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private byte[] Manifest()
        {
            var manifest = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, platform = "windows", packageId = "YourTubeWindows", channel = "win-x64", version = "0.1.20", tag = "v0.1.20", sourceSha = Head,
                assets = new[] { new { name = Setup, sha256 = SetupHash }, new { name = Feed, sha256 = Hash(FeedBytes) }, new { name = Package, sha256 = PackageHash } },
                changelog = new
                {
                    schema = 1, platform = "windows", head = new { version = "0.1.20", sha = Head },
                    releases = new[]
                    {
                        new { version = "0.1.8", sha = Base, count = 100, commits = Array.Empty<object>() },
                        new { version = "0.1.9", sha = Middle, count = 102, commits = new object[] {
                            new { sha = Intervening, subject = "[verified] docs: original\r\0", kind = (string?)null, text = (string?)null },
                            new { sha = Middle, subject = "feat(player): original", kind = "improvement", text = "Playback improvement" } } },
                        new { version = "0.1.20", sha = Head, count = 103, commits = new object[] {
                            new { sha = Head, subject = "fix(ui): original", kind = "fix", text = "Correct rendering" } } }
                    }
                }
            })!.AsObject();
            var log = manifest["changelog"]!;
            var releases = log["releases"]!;
            var commit = releases[1]!["commits"]![0]!;
            switch (Fault)
            {
                case "details-only":
                    var tip = releases[2]!["commits"]![0]!;
                    tip["subject"] = ""; tip["kind"] = null; tip["text"] = null;
                    break;
                case "legacy": manifest.Remove("changelog"); break;
                case "nonjson": return "{"u8.ToArray();
                case "too-large": return new byte[1024 * 1024 + 1];
                case "duplicate-field": return Encoding.UTF8.GetBytes(manifest.ToJsonString().Replace("\"count\":102", "\"count\":101,\"count\":102", StringComparison.Ordinal));
                case "invalid-root-version": manifest["version"] = "0.1.020"; break;
                case "invalid-root-schema": manifest["schemaVersion"] = 2; break;
                case "invalid-root-platform": manifest["platform"] = "android"; break;
                case "invalid-root-package": manifest["packageId"] = "other"; break;
                case "invalid-root-channel": manifest["channel"] = "win-arm64"; break;
                case "invalid-tag": manifest["tag"] = "v0.1.19"; break;
                case "invalid-source": manifest["sourceSha"] = Head + "\n"; break;
                case "invalid-head": log["head"]!["sha"] = Base; break;
                case "invalid-schema": log["schema"] = 2; break;
                case "invalid-platform": log["platform"] = "android"; break;
                case "baseline-commits": releases[0]!["commits"]!.AsArray().Add(commit.DeepClone()); break;
                case "invalid-version-order": releases[1]!["version"] = "0.1.7"; break;
                case "invalid-count-order": releases[1]!["count"] = 99; break;
                case "omitted-commit": releases[1]!["commits"]!.AsArray().RemoveAt(0); break;
                case "invalid-tail": releases[1]!["commits"]![1]!["sha"] = new string('9', 40); break;
                case "duplicate-commit": commit["sha"] = Base; break;
                case "duplicate-source": releases[1]!["sha"] = Base; break;
                case "invalid-kind": commit["kind"] = "feature"; commit["text"] = "Subject"; break;
                case "invalid-text": commit["kind"] = "fix"; commit["text"] = "one\ntwo"; break;
                case "invalid-subject": commit["subject"] = "one\ntwo"; break;
                case "invalid-kind-text-pair": commit["text"] = "Unpaired text"; break;
                case "bad-feed-digest": manifest["assets"]![1]!["sha256"] = SetupHash; break;
                case "bad-package-hash": manifest["assets"]![2]!["sha256"] = SetupHash; break;
                case "duplicate-artifact": manifest["assets"]![2] = manifest["assets"]![0]!.DeepClone(); break;
            }
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(Prefix + Notes, request.RequestUri!.AbsoluteUri);
            Requests.Add(request.RequestUri.AbsoluteUri);
            var response = new HttpResponseMessage(Fault == "http" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest()) };
            if (HoldManifest is { } hold) await hold;
            return response;
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Assert.Equal("https://api.github.com/repos/everyonesoftware/yourtube/releases?per_page=100&page=1", url);
            Requests.Add(url);
            var bytes = Manifest();
            var assets = new List<object>();
            void Asset(string name, long size, string? hash = null, string? origin = null) => assets.Add(new { name, size, state = "uploaded", digest = hash is null ? null : "sha256:" + hash, browser_download_url = name == Notes && Fault == "malformed-manifest-url" ? (object)123 : (origin ?? Prefix) + name });
            Asset(Setup, 200, SetupHash);
            Asset(Package, Fault == "wrong-package-size" ? 99 : 100, PackageHash);
            Asset(Feed, FeedBytes.Length, Fault == "bad-feed-digest" ? SetupHash : Hash(FeedBytes));
            Asset(Setup + ".sha256", 100);
            if (Fault != "missing-checksum") Asset(Package + ".sha256", 100);
            Asset(Feed + ".sha256", 100);
            if (Fault != "no-manifest") Asset(Notes, bytes.Length + (Fault == "wrong-manifest-size" ? 1 : 0), Fault switch { "no-manifest-digest" => null, "bad-manifest-digest" => SetupHash, _ => Hash(bytes) }, Fault == "wrong-manifest-origin" ? "https://example.test/" : null);
            if (Fault == "ambiguous-manifest") Asset(Notes.ToUpperInvariant(), 100);
            return Task.FromResult(JsonSerializer.Serialize(new[] { new { tag_name = "v0.1.20", target_commitish = Fault == "branch-target" ? "main" : Head, draft = false, prerelease = false, assets } }));
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Assert.Equal(Prefix + Feed, url);
            Requests.Add(url);
            return Task.FromResult(FeedBytes);
        }

        public Task DownloadFile(string url, string targetFile, Action<int>? progress = null, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            Assert.Equal(Prefix + Package, url);
            Requests.Add(url);
            return File.WriteAllBytesAsync(targetFile, PackageBytes, cancelToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                http?.Dispose();
                Directory.Delete(directory, recursive: true);
            }
            base.Dispose(disposing);
        }
    }
}
