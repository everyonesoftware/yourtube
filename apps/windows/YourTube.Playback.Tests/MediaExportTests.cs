using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class MediaExportTests : IDisposable
{
    private readonly DirectoryInfo destination = Directory.CreateTempSubdirectory("yourtube-export-test-");

    [Fact]
    public async Task CatalogUsesValidatedTracksAndHighestBitratePerHeightWithoutFurtherRequests()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        int before = fixture.Requests.Count;
        Array.Clear(media.Manifest);

        Assert.Equal(new MediaExportOption[]
        {
            new(MediaExportFormat.Mp4, 90, fixture.Size("video-small") + fixture.Size("audio")),
            new(MediaExportFormat.Mp4, 180, fixture.Size("video") + fixture.Size("audio")),
            new(MediaExportFormat.WebM, 90, fixture.Size("video-small") + fixture.Size("audio")),
            new(MediaExportFormat.WebM, 180, fixture.Size("video") + fixture.Size("audio")),
            new(MediaExportFormat.M4a, null, fixture.Size("audio")),
            new(MediaExportFormat.Mp3, null, fixture.Size("audio"))
        }, fixture.Resolver.GetExportOptions(media).Where(option => option.AudioBitrateKbps is null));
        Assert.Equal(new int?[] { 128, 192, 256 }, fixture.Resolver.GetExportOptions(media)
            .Where(option => option.AudioBitrateKbps is not null && option.Format == MediaExportFormat.Mp3)
            .Select(option => option.AudioBitrateKbps));
        Assert.Empty(fixture.Resolver.GetExportOptions(Forged(media)));
        Assert.Equal(before, fixture.Requests.Count);
    }

    [Fact]
    public async Task M4aCopiesOnlyOriginalAudioWithoutFfmpegAndPublishesAtCompletion()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string path = Path.Combine(destination.FullName, "audio.m4a");
        var option = Option(fixture, media, MediaExportFormat.M4a);
        List<(double Value, bool Published)> observations = [];
        var progress = new InlineProgress(value => observations.Add((value, File.Exists(path))));
        fixture.Requests.Clear();
        await fixture.Resolver.ExportAsync(media, option, path, null, progress);

        Assert.Equal(fixture.Bytes("audio"), await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(destination.FullName));
        Assert.All(fixture.Requests, request => Assert.Equal("audio", request.Track));
        Assert.Equal(0UL, fixture.Requests[0].Start);
        Assert.Equal((ulong)fixture.Size("audio") - 1, fixture.Requests[^1].End);
        Assert.All(observations, item => Assert.Equal(item.Value == 1, item.Published));
        var values = observations.Select(item => item.Value).ToArray();
        Assert.Equal(0, values[0]);
        Assert.Equal(1, values[^1]);
        Assert.Equal(values.Order(), values);
        Assert.Single(values, value => value == 1);
    }

    [Fact]
    public async Task LargerTrackUsesContiguousBoundedRangesIncludingShortTail()
    {
        using var fixture = new ExportFixture(padAudio: true);
        var media = await fixture.ResolveAsync();
        var path = Path.Combine(destination.FullName, "audio.m4a");
        fixture.Requests.Clear();
        await fixture.Resolver.ExportAsync(media, Option(fixture, media, MediaExportFormat.M4a), path, null);

        const ulong chunk = PlaybackResolver.ExportChunkSize;
        Assert.Equal(new[] { ("audio", 0UL, chunk - 1), ("audio", chunk, (ulong)fixture.Size("audio") - 1) }, fixture.Requests);
        Assert.Equal(fixture.Bytes("audio"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task JournalWriteFailureStopsBeforeAnyDestinationBytesAreTransferred()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string path = Path.Combine(destination.FullName, "audio.m4a");
        string blocked = Path.Combine(destination.FullName, "not-a-directory");
        File.WriteAllText(blocked, "untouched");
        fixture.Requests.Clear();
        var journal = new DownloadJournal(blocked, new FixtureJournalFiles());

        var error = await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media,
            Option(fixture, media, MediaExportFormat.M4a), path, null, journal: journal));
        Assert.Equal("export_journal_failed", error.Code);
        Assert.Empty(fixture.Requests);
        Assert.Equal(new[] { blocked }, Directory.GetFileSystemEntries(destination.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalTracksTransferThenRetainsOnlyPublishedReceipts(bool cancel)
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string path = Path.Combine(destination.FullName, "audio.m4a");
        string store = Path.Combine(destination.FullName, "journal");
        var journal = new DownloadJournal(store, new FixtureJournalFiles());
        using var cancellation = new CancellationTokenSource();
        DownloadSnapshot? during = null;
        var progress = new InlineProgress(value =>
        {
            if (value is <= 0 or >= 0.99) return;
            during = journal.Read();
            if (cancel) cancellation.Cancel();
        });
        var work = fixture.Resolver.ExportAsync(media, Option(fixture, media, MediaExportFormat.M4a), path, null,
            progress, cancellation.Token, journal);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await work;

        Assert.NotNull(during);
        var obligation = Assert.Single(during.Cleanup);
        Assert.Empty(during.Completed);
        var after = new DownloadJournal(store, new FixtureJournalFiles()).Read();
        Assert.Empty(after.Cleanup);
        if (cancel)
        {
            Assert.False(File.Exists(path));
            Assert.Empty(after.Completed);
        }
        else
        {
            var receipt = Assert.Single(after.Completed);
            Assert.Equal(obligation.Id, receipt.Id);
            Assert.Equal((long)fixture.Size("audio"), receipt.Bytes);
            Assert.Equal(media.Title, receipt.Title);
            Assert.Equal(fixture.Bytes("audio"), File.ReadAllBytes(path));
            string persisted = File.ReadAllText(Assert.Single(Directory.GetFiles(store, "*.json")));
            Assert.DoesNotContain("https://", persisted);
            Assert.DoesNotContain(media.VideoId, persisted);
        }
    }

    [Fact]
    public async Task CommitProgressPrecedesJournalWriteEvenWhenTheWriteCannotAcquireTheGate()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string path = Path.Combine(destination.FullName, "audio.m4a");
        string store = Path.Combine(destination.FullName, "journal");
        var journal = new DownloadJournal(store, new FixtureJournalFiles());
        FileStream? heldGate = null;
        List<double> reports = [];
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            if (value is > 0 and <= 0.95)
                heldGate = new FileStream(Path.Combine(store, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        try
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media,
                Option(fixture, media, MediaExportFormat.M4a), path, null, progress, journal: journal));
            Assert.Equal("export_journal_failed", error.Code);
            Assert.Equal(0.99, reports[^1]);
            Assert.False(File.Exists(path));
        }
        finally { heldGate?.Dispose(); }
    }

    [Fact]
    public async Task EncoderInputsCannotBeDownloadedBeforeScratchJournalIsWritable()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string final = Path.Combine(destination.FullName, "audio.mp3");
        string blocked = Path.Combine(destination.FullName, "not-a-directory");
        File.WriteAllText(blocked, "untouched");
        fixture.Requests.Clear();
        var error = await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media,
            Option(fixture, media, MediaExportFormat.Mp3), final, blocked, journal: new(blocked, new FixtureJournalFiles())));
        Assert.Equal("export_journal_failed", error.Code);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CodecTransferHasADurableScratchObligationBeforeWritingMedia()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string final = Path.Combine(destination.FullName, "audio.mp3");
        string store = Path.Combine(destination.FullName, "journal");
        string encoder = Path.Combine(destination.FullName, "unused.exe");
        File.WriteAllText(encoder, "not an executable");
        using var canceled = new CancellationTokenSource();
        DownloadCleanup? recorded = null;
        var progress = new InlineProgress(value =>
        {
            if (value <= 0) return;
            recorded = new DownloadJournal(store).Read().Cleanup.SingleOrDefault();
            canceled.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ExportAsync(media,
            Option(fixture, media, MediaExportFormat.Mp3), final, encoder, progress, canceled.Token,
            new DownloadJournal(store)));
        Assert.NotNull(recorded);
        Assert.False(File.Exists(final));
        Assert.Empty(new DownloadJournal(store).Read().Cleanup);
        Assert.Empty(Directory.GetDirectories(store));
    }

    [Fact]
    public async Task OldDriveInspectionDoesNotBlockAHealthyExportOrItsLocalRefresh()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string store = Path.Combine(destination.FullName, "journal");
        string old = Path.Combine(destination.FullName, "old.m4a");
        string final = Path.Combine(destination.FullName, "healthy.m4a");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var files = new FixtureJournalFiles { Final = ExportFileState.Owned, OnInspect = path =>
        {
            if (path != old) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        } };
        var journal = new DownloadJournal(store, files);
        var option = Option(fixture, media, MediaExportFormat.M4a);
        Guid id = Guid.NewGuid();
        journal.Begin(id, "Old download", option, old, files.Capture(null!));
        journal.PreparePublication(id, 1, DateTimeOffset.UtcNow);
        var recovery = Task.Run(journal.RecoverPending);
        Task<DownloadSnapshot>? refresh = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await fixture.Resolver.ExportAsync(media, option, final, null, journal: new(store, files));
            Assert.Equal(fixture.Bytes("audio"), File.ReadAllBytes(final));
            refresh = Task.Run(journal.ReadLocal);
            var snapshot = await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(media.Title, Assert.Single(snapshot.Completed).Title);
            Assert.Equal(id, Assert.Single(snapshot.Cleanup).Id);
            Assert.False(recovery.IsCompleted);
        }
        finally
        {
            release.Set();
            await recovery;
            if (refresh is not null) await refresh;
        }
        var settled = journal.ReadLocal();
        Assert.Contains(settled.Completed, receipt => receipt.Id == id);
        Assert.Contains(settled.Completed, receipt => receipt.Title == media.Title);
        Assert.Empty(settled.Cleanup);
    }

    [Fact]
    public async Task ReceiptWriteFailureAfterRenameDoesNotFailExport()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        string path = Path.Combine(destination.FullName, "audio.m4a");
        string store = Path.Combine(destination.FullName, "journal");
        var files = new FixtureJournalFiles();
        var journal = new DownloadJournal(store, files);
        int writes = 0;
        bool receiptWriteAttempted = false;
        double last = 0;
        files.BeforeMove = () =>
        {
            if (++writes != 3) return;
            receiptWriteAttempted = true;
            Assert.True(File.Exists(path));
            throw new IOException("Injected receipt write failure.");
        };
        await fixture.Resolver.ExportAsync(media, Option(fixture, media, MediaExportFormat.M4a), path, null,
            new InlineProgress(value => last = value), journal: journal);
        Assert.True(receiptWriteAttempted);
        Assert.Equal(1, last);
        Assert.Equal(fixture.Bytes("audio"), File.ReadAllBytes(path));

        var visible = journal.ReadLocal();
        Assert.Single(visible.Completed);
        Assert.Empty(visible.Cleanup);
        Assert.Single(new DownloadJournal(store, files).Read().Cleanup);
        files.Final = ExportFileState.Owned;
        var reopened = new DownloadJournal(store, files);
        reopened.RecoverPending();
        Assert.Single(reopened.ReadLocal().Completed);
    }

    private sealed class FixtureJournalFiles : IExportJournalFiles
    {
        public ExportFileState Final { get; set; } = ExportFileState.Unavailable;
        public Action<string>? OnInspect { get; init; }
        public Action? BeforeMove { get; set; }
        public void MoveRecord(string source, string destination, bool overwrite)
        {
            BeforeMove?.Invoke();
            if (OperatingSystem.IsWindows()) WindowsExportJournalFiles.MoveJournalRecord(source, destination, overwrite);
            else File.Move(source, destination, overwrite);
        }
        public ExportFileIdentity Capture(MediaExportStaging stage) => new(new Guid("10000000-0000-0000-0000-000000000001"), 1, 2, 3, 4);
        public ExportFileState Inspect(string path, ExportFileIdentity identity, long? bytes, bool remove)
        {
            OnInspect?.Invoke(path);
            return bytes is not null ? Final : ExportFileState.Unavailable;
        }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("existing")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task InterruptedTransferLeavesNoPartialOutputOrStaging(string mode)
    {
        using var fixture = new ExportFixture(padAudio: true);
        using var cancellation = new CancellationTokenSource();
        var media = await fixture.ResolveAsync();
        var path = Path.Combine(destination.FullName, "audio.m4a");
        var option = Option(fixture, media, MediaExportFormat.M4a);
        List<double> values = [];
        var onProgress = new InlineProgress(value =>
        {
            values.Add(value);
            if (values.Count != 2) return;
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "expire") fixture.Clock.Now = media.ExpiresAt;
            if (mode == "existing") File.WriteAllText(path, "existing");
        });
        fixture.Requests.Clear();
        if (mode is "short" or "long") fixture.BodyDelta = mode == "short" ? -1 : 1;
        var export = fixture.Resolver.ExportAsync(media, option, path, null, onProgress, cancellation.Token);
        if (mode == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        else
            Assert.Equal(mode switch { "expire" => "expired", "existing" => "destination_exists", _ => "invalid_range" },
                (await Assert.ThrowsAsync<PlaybackException>(() => export)).Code);

        Assert.Equal(mode is "cancel" or "expire" ? 1 : 2, fixture.Requests.Count);
        Assert.DoesNotContain(1d, values);
        if (mode == "existing")
        {
            Assert.Equal("existing", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(destination.FullName));
        }
        else Assert.Empty(Directory.GetFileSystemEntries(destination.FullName));
        if (mode == "expire") Assert.Empty(fixture.Resolver.GetExportOptions(media));
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightResponseBodyAndDeletesStaging()
    {
        using var fixture = new ExportFixture(padAudio: true);
        using var cancellation = new CancellationTokenSource();
        var media = await fixture.ResolveAsync();
        var path = Path.Combine(destination.FullName, "audio.m4a");
        var option = Option(fixture, media, MediaExportFormat.M4a);
        using var body = new HeldBody();
        fixture.TailBody = body;
        var export = fixture.Resolver.ExportAsync(media, option, path, null, cancellationToken: cancellation.Token);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(path));
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFileSystemEntries(destination.FullName));
    }

    [Fact]
    public async Task RejectsForgedMediaAlteredOptionsExistingPathsAndMissingFfmpegBeforeTransfer()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        var path = Path.Combine(destination.FullName, "audio.m4a");
        var option = Option(fixture, media, MediaExportFormat.M4a);
        var video = Option(fixture, media, MediaExportFormat.Mp4, 180);
        fixture.Requests.Clear();

        Assert.Equal("unsafe_resource", (await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(Forged(media), option, path, null))).Code);
        Assert.Equal("unsupported_export", (await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media, option with { SourceBytes = 1 }, path, null))).Code);
        Assert.Equal("invalid_destination", (await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media, option, "audio.m4a", null))).Code);
        Assert.Equal("ffmpeg_required", (await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media, video, Path.ChangeExtension(path, ".mp4"), "ffmpeg"))).Code);
        File.WriteAllText(path, "keep");
        Assert.Equal("destination_exists", (await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media, option, path, null))).Code);

        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(destination.FullName));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAndPublicationHaveOneWinner(bool commitStarted, bool collision)
    {
        using var fixture = new ExportFixture();
        using var cancellation = new CancellationTokenSource();
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        var media = await fixture.ResolveAsync();
        var path = Path.Combine(destination.FullName, "audio.m4a");
        var values = new List<double>();
        bool released = false;
        var progress = new InlineProgress(value =>
        {
            values.Add(value);
            if (commitStarted ? value != 0.99 : value is <= 0.94 or > 0.96) return;
            entered.TrySetResult();
            released = resume.Wait(timeout);
        });
        var export = Task.Run(() => fixture.Resolver.ExportAsync(media, Option(fixture, media, MediaExportFormat.M4a),
            path, null, progress, cancellation.Token));

        try
        {
            await Task.WhenAny(entered.Task, export).WaitAsync(timeout);
            Assert.True(entered.Task.IsCompletedSuccessfully, "The export did not report the expected publication phase.");
            Assert.False(File.Exists(path));
            var stagingPath = Assert.Single(Directory.GetFileSystemEntries(destination.FullName));
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<IOException>(() =>
                {
                    using var other = File.Open(stagingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                });
            }

            if (collision) File.WriteAllText(path, "existing");
            cancellation.Cancel();
        }
        finally { resume.Set(); }

        if (!commitStarted)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(timeout));
            Assert.DoesNotContain(0.99, values);
            Assert.DoesNotContain(1d, values);
            Assert.Empty(Directory.GetFileSystemEntries(destination.FullName));
        }
        else if (collision)
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => export.WaitAsync(timeout));
            Assert.Equal("destination_exists", error.Code);
            Assert.DoesNotContain(1d, values);
            Assert.Equal("existing", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(destination.FullName));
        }
        else
        {
            await export.WaitAsync(timeout);
            Assert.Equal(new[] { 0.99, 1d }, values.TakeLast(2));
            Assert.Equal(fixture.Bytes("audio"), File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(destination.FullName));
        }
        Assert.True(released, "The fixture did not release the publication gate in time.");
    }

    [Fact]
    public async Task EncoderLaunchFailureIsSanitizedAndRemovesStaging()
    {
        using var fixture = new ExportFixture();
        var media = await fixture.ResolveAsync();
        var bin = Path.Combine(destination.FullName, "private marker.exe");
        File.WriteAllText(bin, "not an executable");
        string[]? entriesWhenReadyToEncode = null;
        var progress = new InlineProgress(value =>
        {
            if (value is > 0.94 and <= 0.96) entriesWhenReadyToEncode = Directory.GetFileSystemEntries(destination.FullName);
        });
        var error = await Assert.ThrowsAsync<PlaybackException>(() => fixture.Resolver.ExportAsync(media,
            Option(fixture, media, MediaExportFormat.Mp3), Path.Combine(destination.FullName, "audio.mp3"), bin, progress));
        Assert.Equal("ffmpeg_failed", error.Code);
        Assert.Equal(new[] { bin }, entriesWhenReadyToEncode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private marker", error.ToString());
        Assert.Equal(new[] { bin }, Directory.GetFileSystemEntries(destination.FullName));
    }

    [Theory]
    [InlineData(MediaExportFormat.Mp4, 90, "h264", "aac")]
    [InlineData(MediaExportFormat.WebM, 180, "vp9", "opus")]
    [InlineData(MediaExportFormat.Mp3, null, null, "mp3")]
    public void EncoderArgumentsKeepExplicitMapsAndAllowOnlyLocalMovInputs(MediaExportFormat format, int? height,
        string? videoCodec, string audioCodec)
    {
        var audio = Path.Combine(destination.FullName, "source & audio.m4a");
        var video = height is null ? null : Path.Combine(destination.FullName, "source & video.mp4");
        var output = Path.Combine(destination.FullName, "stage.part");
        var executable = Path.Combine(destination.FullName, "ffmpeg test.exe");
        var start = MediaExportEncoder.CreateStartInfo(executable, format, video, audio, output);
        var args = start.ArgumentList.ToArray();
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(executable, start.FileName);
        Assert.False(start.Environment.ContainsKey("FFREPORT"));
        Assert.Contains("-nostdin", args);
        Assert.Contains("-n", args);
        Assert.DoesNotContain("-y", args);
        var inputs = args.Select((value, index) => (value, index)).Where(arg => arg.value == "-i")
            .Select(arg => args[(arg.index - 6)..(arg.index + 2)]).ToArray();
        Assert.Equal(video is null ? 1 : 2, inputs.Length);
        Assert.Equal(new[] { "-protocol_whitelist", "file", "-enable_drefs", "0", "-f", "mov", "-i", audio }, inputs[^1]);
        if (video is not null)
        {
            Assert.Equal(new[] { "-protocol_whitelist", "file", "-enable_drefs", "0", "-f", "mov", "-i", video }, inputs[0]);
            Assert.Contains("0:v:0", args);
        }
        Assert.Contains(video is null ? "0:a:0" : "1:a:0", args);
        if (videoCodec is not null) Assert.Equal(videoCodec == "h264" ? "copy" : "libvpx-vp9", args[Array.IndexOf(args, "-c:v") + 1]);
        Assert.Equal(audioCodec switch { "aac" => "copy", "opus" => "libopus", _ => "libmp3lame" }, args[Array.IndexOf(args, "-c:a") + 1]);
        Assert.Equal(output, args[^1]);
    }

    [ExportCodecProof]
    public async Task ExportCodecProofWritesFilesWithActualExpectedStreams()
    {
        using var fixture = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var media = await fixture.ResolveAsync();
        string encoder = Environment.GetEnvironmentVariable("YOURTUBE_EXPORT_FFMPEG")!;
        string probe = Environment.GetEnvironmentVariable("YOURTUBE_EXPORT_FFPROBE")!;
        string store = Path.Combine(destination.FullName, "journal");
        var journal = OperatingSystem.IsWindows() ? new DownloadJournal(store) : new(store, new FixtureJournalFiles());
        foreach (var (format, height, bitrate, videoCodec, audioCodec, downloaded) in new (MediaExportFormat, int?, int?, string?, string, string[])[]
        {
            (MediaExportFormat.Mp4, 90, null, "h264", "aac", ["video-small", "audio"]),
            (MediaExportFormat.WebM, 180, null, "vp9", "opus", ["video", "audio"]),
            (MediaExportFormat.M4a, null, null, null, "aac", ["audio"]),
            (MediaExportFormat.Mp3, null, null, null, "mp3", ["audio"]),
            (MediaExportFormat.Mp3, null, 128, null, "mp3", ["audio"]),
            (MediaExportFormat.Mp3, null, 192, null, "mp3", ["audio"]),
            (MediaExportFormat.Mp3, null, 256, null, "mp3", ["audio"])
        })
        {
            fixture.Requests.Clear();
            var option = Assert.Single(fixture.Resolver.GetExportOptions(media), option =>
                option.Format == format && option.Height == height && option.AudioBitrateKbps == bitrate);
            var path = Path.Combine(destination.FullName, $"exported space & media{(bitrate is int rate ? $" {rate}" : "")}" + option.Extension);
            await fixture.Resolver.ExportAsync(media, option, path, encoder, cancellationToken: deadline.Token, journal: journal);
            Assert.Equal(downloaded, fixture.Requests.Select(request => request.Track));
            var start = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment.Remove("FFREPORT");
            foreach (string arg in new[] { "-v", "quiet", "-show_entries", "stream=codec_type,codec_name,height,bit_rate:format=duration", "-of", "json", path })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await discard;
            Assert.Equal(0, process.ExitCode);
            using var json = JsonDocument.Parse(await output);
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Equal(videoCodec is null ? 1 : 2, streams.Length);
            var audio = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
            Assert.Equal(audioCodec, audio.GetProperty("codec_name").GetString());
            if (bitrate is int kbps)
                Assert.Equal((kbps * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), audio.GetProperty("bit_rate").GetString());
            if (videoCodec is not null)
            {
                var video = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "video");
                Assert.Equal(videoCodec, video.GetProperty("codec_name").GetString());
                Assert.Equal(height, video.GetProperty("height").GetInt32());
            }
            Assert.InRange(double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture), 17.9, 18.2);
        }
        Assert.Equal(7, Directory.GetFiles(destination.FullName).Length);
        Assert.Equal(7, journal.Read().Completed.Count);
        Assert.Empty(journal.Read().Cleanup);
        Assert.Empty(Directory.GetDirectories(store));
    }

    private sealed class ExportCodecProofAttribute : FactAttribute
    {
        public ExportCodecProofAttribute()
        {
            if (Environment.GetEnvironmentVariable("YOURTUBE_EXPORT_FFMPEG") is null ||
                Environment.GetEnvironmentVariable("YOURTUBE_EXPORT_FFPROBE") is null)
                Skip = "Set YOURTUBE_EXPORT_FFMPEG and YOURTUBE_EXPORT_FFPROBE to run the offline codec proof.";
        }
    }

    private static PlaybackMedia Forged(PlaybackMedia media) => new(media.VideoId, media.Title, media.Author,
        media.Description, media.Duration, media.ExpiresAt, media.Manifest, media.ManifestUri, media.Qualities);

    private static MediaExportOption Option(ExportFixture fixture, PlaybackMedia media, MediaExportFormat format,
        int? height = null) => Assert.Single(fixture.Resolver.GetExportOptions(media), o => o.Format == format && o.Height == height && o.AudioBitrateKbps is null);

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class ExportFixture : IDisposable
    {
        private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "ExportFixtures");
        private readonly Dictionary<string, byte[]> bytes = new()
        {
            ["video"] = File.ReadAllBytes(Path.Combine(FixtureDir, "video.mp4")),
            ["video-small"] = File.ReadAllBytes(Path.Combine(FixtureDir, "video-90p.mp4")),
            ["video-lower"] = File.ReadAllBytes(Path.Combine(FixtureDir, "video-90p.mp4")),
            ["audio"] = File.ReadAllBytes(Path.Combine(FixtureDir, "audio.mp4")),
            ["invalid"] = File.ReadAllBytes(Path.Combine(FixtureDir, "audio.mp4"))
        };
        public List<(string Track, ulong Start, ulong End)> Requests { get; } = [];
        public PlaybackResolver Resolver { get; }
        public TestClock Clock { get; } = new();
        public int BodyDelta { get; set; }
        public Stream? TailBody { get; set; }

        public ExportFixture(bool padAudio = false)
        {
            if (padAudio)
            {
                var audio = bytes["audio"];
                Array.Resize(ref audio, (int)PlaybackResolver.ExportChunkSize + 117);
                bytes["audio"] = audio;
            }
            Resolver = new(new Handler(Respond), Clock);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var query = request.RequestUri!.Query[1..].Split('&').ToDictionary(p => p.Split('=')[0], p => p.Split('=')[1]);
            string name = query["id"];
            var range = query["range"].Split('-');
            ulong start = ulong.Parse(range[0]), end = ulong.Parse(range[1]);
            Requests.Add((name, start, end));
            var full = bytes[name];
            byte[] payload = full[(int)start..((int)end + 1)];
            HttpContent content;
            if (TailBody is not null && start > 0) content = new StreamContent(TailBody);
            else if (BodyDelta != 0 && start > 0)
            {
                Array.Resize(ref payload, payload.Length + BodyDelta);
                content = new StreamContent(new NonSeekableBody(payload));
            }
            else content = new ByteArrayContent(payload);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue((long)start, (long)end, full.Length);
            return response;
        }

        public Task<PlaybackMedia> ResolveAsync() => Resolver.AssembleAsync("FIXTURE0001", "Fixture export title", "", "", TimeSpan.FromSeconds(18),
            [Track("video", 320, 180, 900_000), Track("video-small", 160, 90, 600_000),
                Track("video-lower", 320, 180, 500_000), Track("invalid", 640, 360, 2_000_000), Track("audio", 0, 0, 128_000)]);

        private MediaTrack Track(string name, int width, int height, uint bitrate) => new(ResolverTests.Url(name),
            (ulong)Size(name), height == 0 ? "mp4a.40.2" : "avc1.64001f", bitrate, width, height);

        public long Size(string name) => bytes[name].Length;
        public byte[] Bytes(string name) => bytes[name];
        public void Dispose() => Resolver.Dispose();
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class HeldBody : MemoryStream
    {
        public override bool CanSeek => false;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() => destination.Delete(recursive: true);
}