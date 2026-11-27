using System.IO.Compression;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class PortableFfmpegTests : IDisposable
{
    private readonly string folder = Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetTempPath(),
        "yourtube-ffmpeg-" + Guid.NewGuid().ToString("N"));
    private readonly Feed feed = new();

    [Fact]
    public async Task DiscoveryDoesNotInstallAndExplicitSetupSurvivesRestart()
    {
        using var store = new PortableFfmpeg(folder, feed);
        Assert.Null(await store.ReadAsync(default));
        var release = await store.FindAsync(default);
        Assert.Equal(new Version(9, 1, 0), release.Version);
        Assert.Equal(new[] { "/ffmpeg/builds/release-version", "/ffmpeg/builds/packages/ffmpeg-9.1-essentials_build.zip.sha256" }, feed.Requests);
        Assert.False(Directory.Exists(folder));
        var installed = await store.InstallAsync(release, null, default);
        using var reopened = new PortableFfmpeg(folder, feed);
        Assert.Equal(installed, await reopened.ReadAsync(default));
        using var lease = await reopened.OpenAsync(default);
        Assert.Equal(installed.Path, lease?.Installation.Path);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.Path))!, "LICENSE")));
        Assert.Empty(Directory.GetFiles(folder, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FailedUpdatePreservesTheConfiguredCopyAndRetryCanSwitchVersions()
    {
        using var store = new PortableFfmpeg(folder, feed);
        var original = await store.InstallAsync(await store.FindAsync(default), null, default);
        using var priorLease = await store.OpenAsync(default);
        feed.Version = "9.1.1";
        var update = await store.FindAsync(default);
        feed.CorruptDownload = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(update, null, default));
        Assert.Equal(original, await store.ReadAsync(default));
        feed.CorruptDownload = false;
        var replacement = await store.InstallAsync(update, null, default);
        Assert.Equal(new Version(9, 1, 1), replacement.Version);
        Assert.NotEqual(original.Path, replacement.Path);
        Assert.True(File.Exists(original.Path));
        Assert.Equal(replacement, await store.ReadAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(update, null, default));
    }

    [Theory]
    [InlineData("9.1/../../outside", false)]
    [InlineData("9.1", true)]
    public async Task InvalidVersionOrRedirectNeverReachesAnAsset(string version, bool redirect)
    {
        feed.Version = version;
        feed.Redirect = redirect;
        using var store = new PortableFfmpeg(folder, feed);
        await Assert.ThrowsAnyAsync<Exception>(() => store.FindAsync(default));
        Assert.Single(feed.Requests);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task DuplicateExecutableAndTamperedInstalledBinaryAreNeverUsed()
    {
        using var store = new PortableFfmpeg(folder, feed);
        feed.DuplicateEntry = true;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.InstallAsync(await store.FindAsync(default), null, default));
        Assert.Null(await store.ReadAsync(default));
        feed.DuplicateEntry = false;
        var installed = await store.InstallAsync(await store.FindAsync(default), null, default);
        await File.WriteAllTextAsync(installed.Path, "substituted");
        Assert.Null(await store.OpenAsync(default));
        Assert.Null(await store.ReadAsync(default));
    }

    [Fact]
    public async Task CancellationBeforePublicationLeavesNoConfiguredCopy()
    {
        using var store = new PortableFfmpeg(folder, feed);
        using var canceled = new CancellationTokenSource();
        var release = await store.FindAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(release,
            (verifying, _) => { if (verifying) canceled.Cancel(); }, canceled.Token));
        Assert.Null(await store.ReadAsync(default));
    }

    [Fact]
    public async Task ManagedCopyDoesNotAllowUnexpectedFilesBesideTheExecutable()
    {
        using var store = new PortableFfmpeg(folder, feed);
        var installed = await store.InstallAsync(await store.FindAsync(default), null, default);
        var extra = Path.Combine(Path.GetDirectoryName(installed.Path)!, "unexpected.dll");
        await File.WriteAllBytesAsync(extra, [0]);
        Assert.Null(await store.OpenAsync(default));
        Assert.True(File.Exists(extra));
    }

    public sealed class LiveFfmpegFactAttribute : FactAttribute
    {
        public LiveFfmpegFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("YOURTUBE_FFMPEG_LIVE_PROOF") != "1")
                Skip = "Opt-in live Gyan.dev package download.";
        }
    }

    public sealed class WindowsFfmpegFactAttribute : FactAttribute
    {
        public WindowsFfmpegFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows directory handles and CreateProcess.";
        }
    }

    [WindowsFfmpegFact]
    public async Task WindowsLeaseAllowsLaunchButDeniesParentRename()
    {
        // Use a Windows system binary rather than fetching or running an external build in CI.
        feed.Executable = await File.ReadAllBytesAsync(Path.Combine(Environment.SystemDirectory, "whoami.exe"));
        using var store = new PortableFfmpeg(folder, feed);
        var installed = await store.InstallAsync(await store.FindAsync(default), null, default);
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(installed.Path))!;
        using (var lease = Assert.IsType<PortableFfmpeg.Lease>(await store.OpenAsync(default)))
        {
            var failure = Record.Exception(() => Directory.Move(parent, parent + "-moved"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var start = new ProcessStartInfo(lease.Installation.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start)!;
            Assert.NotEmpty(await process.StandardOutput.ReadToEndAsync(budget.Token));
            await process.WaitForExitAsync(budget.Token);
            Assert.Equal(0, process.ExitCode);
        }
        Directory.Move(parent, parent + "-moved");
    }

    [LiveFfmpegFact]
    public async Task LiveWindowsPackageIsVerifiedAndCanBeReopened()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var store = new PortableFfmpeg(folder);
        var latest = await store.FindAsync(token.Token);
        Assert.False(Directory.Exists(folder));
        var installed = await store.InstallAsync(latest, null, token.Token);
        using var reopened = new PortableFfmpeg(folder);
        using var lease = Assert.IsType<PortableFfmpeg.Lease>(await reopened.OpenAsync(token.Token));
        Assert.Equal(installed, lease.Installation);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(installed.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(token.Token);
            await process.WaitForExitAsync(token.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith($"ffmpeg version {latest.Label}-", output, StringComparison.Ordinal);
        }
        Console.WriteLine($"Verified provider package {latest.Label}; ran executable: {OperatingSystem.IsWindows()}.");
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    private sealed class Feed : HttpMessageHandler
    {
        public string Version = "9.1";
        public bool CorruptDownload, Redirect, DuplicateEntry;
        public byte[]? Executable;
        public readonly List<string> Requests = [];
        private readonly Dictionary<(string Version, bool DuplicateEntry), byte[]> archives = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("www.gyan.dev", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var path = request.RequestUri.AbsolutePath;
            Requests.Add(path);
            if (Redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.invalid/untrusted") } });
            var archive = $"/ffmpeg/builds/packages/ffmpeg-{Version}-essentials_build.zip";
            byte[] bytes = path switch
            {
                "/ffmpeg/builds/release-version" => Encoding.ASCII.GetBytes(Version + "\r\n"),
                var p when p == archive + ".sha256" => Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Archive()))),
                var p when p == archive => CorruptDownload ? [1, 2, 3] : Archive(),
                _ => throw new InvalidOperationException("Unexpected request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }

        private byte[] Archive()
        {
            var key = (Version, DuplicateEntry);
            if (!archives.TryGetValue(key, out var archive)) archives.Add(key, archive = BuildZip());
            return archive;
        }

        private byte[] BuildZip()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var prefix = $"ffmpeg-{Version}-essentials_build/";
                var pe = new ManagedPEBuilder(new PEHeaderBuilder(machine: Machine.Amd64, imageCharacteristics: Characteristics.ExecutableImage),
                    new MetadataRootBuilder(new MetadataBuilder()), new BlobBuilder());
                var image = new BlobBuilder();
                pe.Serialize(image);
                Add(prefix + "bin/ffmpeg.exe", Executable ?? image.ToArray());
                if (DuplicateEntry) Add(prefix + "bin/FFMPEG.EXE", "duplicate"u8);
                Add(prefix + "LICENSE", "fixture license"u8);
                Add("../../outside.txt", "must never be extracted"u8);
                void Add(string name, ReadOnlySpan<byte> content)
                {
                    var entry = zip.CreateEntry(name);
                    entry.LastWriteTime = DateTimeOffset.UnixEpoch.AddYears(40);
                    using var output = entry.Open();
                    output.Write(content);
                }
            }
            return stream.ToArray();
        }
    }
}
