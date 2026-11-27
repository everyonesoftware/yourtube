using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace YourTube.Playback;

/// <summary>A per-profile portable binary; discovery never writes or starts a download.</summary>
public sealed class PortableFfmpeg : IDisposable
{
    private const string Source = "https://www.gyan.dev/ffmpeg/builds/";
    private const long MaxPackage = 256L * 1024 * 1024;
    private readonly string folder;
    private readonly HttpClient http;
    private string Marker => Path.Combine(folder, "current.txt");

    public PortableFfmpeg(string folder) : this(folder, new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        DefaultProxyCredentials = null
    }) { }

    internal PortableFfmpeg(string folder, HttpMessageHandler transport)
    {
        this.folder = Path.GetFullPath(folder);
        http = new HttpClient(transport, disposeHandler: transport is HttpClientHandler);
    }

    public sealed record Release(Version Version, string Label, string Digest);
    public sealed record Installation(Version Version, string Path);
    public sealed class Lease : IDisposable
    {
        private readonly FileStream handle;
        private readonly PortableFfmpegDirectories directories;
        public Installation Installation { get; }
        internal Lease(Installation installation, FileStream handle, PortableFfmpegDirectories directories) =>
            (Installation, this.handle, this.directories) = (installation, handle, directories);
        public void Dispose() { handle.Dispose(); directories.Dispose(); }
    }

    public async Task<Release> FindAsync(CancellationToken token)
    {
        using var deadline = Deadline(token, TimeSpan.FromSeconds(20));
        string label = await ReadTextAsync("release-version", deadline.Token).ConfigureAwait(false);
        var version = ParseVersion(label);
        var digest = ParseDigest(await ReadTextAsync(Package(label) + ".sha256", deadline.Token).ConfigureAwait(false));
        return new Release(version, label, digest);
    }

    public async Task<Installation?> ReadAsync(CancellationToken token)
    {
        using var lease = await OpenAsync(token).ConfigureAwait(false);
        return lease?.Installation;
    }

    public async Task<Lease?> OpenAsync(CancellationToken token)
    {
        FileStream? file = null;
        PortableFfmpegDirectories? directories = new();
        try
        {
            directories.Pin(folder, privateDirectory: true);
            NoLink(Marker);
            string[] parts;
            await using (var marker = new FileStream(Marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                parts = (await ReadSmallAsync(marker, 160, token).ConfigureAwait(false)).Split('\n');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var id)) return null;
            var version = ParseVersion(parts[0]);
            var digest = ParseDigest(parts[2]);
            var install = Path.Combine(folder, id.ToString("N"));
            var bin = Path.Combine(install, "bin");
            var path = Path.Combine(bin, "ffmpeg.exe");
            directories.Pin(install, privateDirectory: true);
            directories.Pin(bin, privateDirectory: true);
            NoLink(path);
            if (Directory.EnumerateFileSystemEntries(bin).Any(entry => !string.Equals(entry, path, StringComparison.OrdinalIgnoreCase))) return null;
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (OperatingSystem.IsWindows() && !WindowsExportJournalFiles.TryIdentify(file.SafeFileHandle, path, out _, out _)) return null;
            if (file.Length is <= 0 or > MaxPackage || !string.Equals(digest,
                Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), StringComparison.OrdinalIgnoreCase)) return null;
            var lease = new Lease(new Installation(version, path), file, directories);
            file = null;
            directories = null;
            return lease;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        finally { file?.Dispose(); directories?.Dispose(); }
    }

    public async Task<Installation> InstallAsync(Release release, Action<bool, double?>? progress, CancellationToken token)
    {
        using var deadline = Deadline(token, TimeSpan.FromMinutes(10));
        token = deadline.Token;
        token.ThrowIfCancellationRequested();
        if (ParseVersion(release.Label) != release.Version) throw new InvalidDataException("Mismatched version.");
        var expected = ParseDigest(release.Digest);
        using var directories = new PortableFfmpegDirectories();
        directories.Pin(folder, create: true, privateDirectory: true);
        var lockPath = Path.Combine(folder, "install.lock");
        if (File.Exists(lockPath)) NoLink(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var current = await OpenAsync(token).ConfigureAwait(false);
        if (current is not null && current.Installation.Version >= release.Version)
            throw new InvalidOperationException("The installed version is already current or newer.");

        var id = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(folder, id);
        var bin = Path.Combine(staged, "bin");
        var path = Path.Combine(bin, "ffmpeg.exe");
        var archivePath = Path.Combine(staged, "download.zip");
        var licensePath = Path.Combine(staged, "LICENSE");
        var newMarker = Path.Combine(staged, "current.txt");
        bool committed = false;
        bool stageOwned = false, binOwned = false;
        using var staging = new PortableFfmpegDirectories();
        try
        {
            if (Directory.Exists(staged)) throw new IOException("FFmpeg staging already exists.");
            staging.Pin(staged, create: true, privateDirectory: true);
            stageOwned = true;
            staging.Pin(bin, create: true, privateDirectory: true);
            binOwned = true;
            await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                using var response = await GetAsync(Package(release.Label), token).ConfigureAwait(false);
                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var length = response.Content.Headers.ContentLength;
                var actual = await CopyAsync(body, archive, MaxPackage, length, value => progress?.Invoke(false, value), token).ConfigureAwait(false);
                progress?.Invoke(true, null);
                token.ThrowIfCancellationRequested();
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FFmpeg package checksum mismatch.");
                archive.Position = 0;
                using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
                string prefix = PackageStem(release.Label) + "/";
                var executable = Entry(zip, prefix + "bin/ffmpeg.exe");
                var license = Entry(zip, prefix + "LICENSE");
                var binaryDigest = await ExtractAsync(executable, path, MaxPackage, token).ConfigureAwait(false);
                await ExtractAsync(license, licensePath, 256 * 1024, token).ConfigureAwait(false);
                using (var candidate = File.OpenRead(path))
                using (var pe = new PEReader(candidate))
                    if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus || pe.PEHeaders.IsDll)
                        throw new InvalidDataException("Expected an x64 Windows executable.");
                await using var configuration = new FileStream(newMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await configuration.WriteAsync(Encoding.ASCII.GetBytes($"{release.Label}\n{id}\n{binaryDigest}"), token).ConfigureAwait(false);
                configuration.Flush(flushToDisk: true);
            }
            File.Delete(archivePath);
            token.ThrowIfCancellationRequested();
            // One rename changes which verified executable new exports use. Never mutate an old path.
            File.Move(newMarker, Marker, overwrite: true);
            committed = true;
            return new Installation(release.Version, path);
        }
        finally
        {
            if (!committed && stageOwned)
            {
                if (binOwned) TryClean(() => File.Delete(path));
                TryClean(() => File.Delete(licensePath));
                TryClean(() => File.Delete(archivePath));
                TryClean(() => File.Delete(newMarker));
                staging.Dispose();
                if (binOwned) TryClean(() => Directory.Delete(bin, recursive: false));
                TryClean(() => Directory.Delete(staged, recursive: false));
            }
        }
    }

    private static void TryClean(Action cleanup)
    {
        try { cleanup(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string name)
    {
        var entries = zip.Entries.Where(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (entries.Length != 1 || entries[0].FullName != name || ((entries[0].ExternalAttributes >> 16) & 0xf000) == 0xa000)
            throw new InvalidDataException("Expected one regular file in the release archive.");
        return entries[0];
    }

    private static async Task<string> ExtractAsync(ZipArchiveEntry entry, string path, long cap, CancellationToken token)
    {
        await using var body = entry.Open();
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        var digest = await CopyAsync(body, file, cap, entry.Length, null, token).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
        return digest;
    }

    private static async Task<string> CopyAsync(Stream input, Stream output, long cap, long? length, Action<double?>? progress, CancellationToken token)
    {
        if (length is <= 0 || length > cap) throw new InvalidDataException("Invalid FFmpeg file size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long written = 0, reported = 0;
        progress?.Invoke(length is null ? null : 0);
        while (true)
        {
            int count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            written += count;
            if (written > cap || written > length) throw new InvalidDataException("FFmpeg file exceeds its size limit.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (length is long total && (written - reported >= 512 * 1024 || written == total))
            {
                progress?.Invoke((double)written / total);
                reported = written;
            }
        }
        if (written == 0 || (length is long declared && declared != written)) throw new InvalidDataException("Incomplete FFmpeg file.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<HttpResponseMessage> GetAsync(string relative, CancellationToken token)
    {
        var response = await http.GetAsync(Source + relative, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK) return response;
        response.Dispose();
        throw new HttpRequestException("FFmpeg source did not return an OK response.");
    }

    private async Task<string> ReadTextAsync(string relative, CancellationToken token)
    {
        using var response = await GetAsync(relative, token).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadSmallAsync(body, 128, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadSmallAsync(Stream input, int cap, CancellationToken token)
    {
        var bytes = new byte[cap + 1];
        var count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
        if (count > cap) throw new InvalidDataException("FFmpeg metadata is too large.");
        return Encoding.ASCII.GetString(bytes, 0, count).Trim(' ', '\t', '\r', '\n');
    }

    private static Version ParseVersion(string label)
    {
        var fields = label.Split('.');
        if (fields.Length is < 2 or > 3 || fields.Any(field => field.Length is < 1 or > 4 || !field.All(char.IsAsciiDigit)) ||
            !Version.TryParse(label, out var parsed) || parsed.Major == 0)
            throw new InvalidDataException("Invalid FFmpeg release version.");
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
    }

    private static string ParseDigest(string digest) => digest.Length == 64 && digest.All(Uri.IsHexDigit) ? digest :
        throw new InvalidDataException("Invalid FFmpeg SHA-256.");
    private static string PackageStem(string label) => $"ffmpeg-{label}-essentials_build";
    private static string Package(string label) => $"packages/{PackageStem(label)}.zip";
    private static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected link in FFmpeg storage.");
    }

    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan delay)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(delay);
        return source;
    }

    public void Dispose() => http.Dispose();
}
