using System.Text.Json.Nodes;
using Xunit;

namespace YourTube.Playback.Tests;

public sealed class DownloadJournalTests : IDisposable
{
    private readonly DirectoryInfo root = OperatingSystem.IsWindows()
        ? Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            $"yourtube-journal-test-{Guid.NewGuid():N}")) // %TEMP% can contain an 8.3 alias.
        : Directory.CreateTempSubdirectory("yourtube-journal-test-");
    private static readonly ExportFileIdentity Identity = new(Guid.NewGuid(), 1, 2, 3, 4);
    private readonly Files files = new();
    private string Store => Path.Combine(root.FullName, "journal");
    private string Final => Path.Combine(root.FullName, "saved.m4a");
    private DownloadJournal Open() => new(Store, files);

    private Guid Begin()
    {
        var id = Guid.NewGuid();
        Open().Begin(id, "Export title", new(MediaExportFormat.M4a, null, 100), Final, Identity);
        return id;
    }

    [Fact]
    public async Task JournalReadWaitsForLocalWriterGateInsteadOfOpeningTheRecord()
    {
        Guid id = Begin();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DownloadSnapshot> read;
        using (new FileStream(Path.Combine(Store, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            read = Task.Run(() =>
            {
                started.SetResult();
                return Open().Read();
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = Task.Delay(100);
            Assert.Same(deadline, await Task.WhenAny(read, deadline));
        }
        Assert.Equal(id, Assert.Single((await read.WaitAsync(TimeSpan.FromSeconds(5))).Cleanup).Id);
    }

    [Fact]
    public void PendingObligationSurvivesReopenUntilOwnedCleanupSucceeds()
    {
        Guid id = Begin();
        Assert.Equal(new DownloadCleanup(id, "Export title"), Assert.Single(Open().Read().Cleanup));
        files.Stage = ExportFileState.Owned;
        Assert.Equal(CleanupResult.Removed, Open().RetryCleanup(id));
        Assert.Equal(DownloadJournal.StagePath(Final, id), Assert.Single(files.Removed));
        Assert.Empty(Open().Read().Cleanup);
    }

    [Theory]
    [InlineData(0, CleanupResult.Removed)]
    [InlineData(1, CleanupResult.Removed)]
    [InlineData(2, CleanupResult.Unavailable)]
    [InlineData(3, CleanupResult.IdentityMismatch)]
    public void ResultsPreserveAmbiguousObligations(int fileState, CleanupResult expected)
    {
        Guid id = Begin();
        var state = (ExportFileState)fileState;
        files.Stage = state;
        Assert.Equal(expected, Open().RetryCleanup(id));
        Assert.Equal(expected != CleanupResult.Removed, Open().Read().Cleanup.Count == 1);
        if (state != ExportFileState.Owned) Assert.Empty(files.Removed);
    }

    [Fact]
    public void CommittingRecordRecoversCompletedReceiptWithoutDeletingFinal()
    {
        Guid id = Begin();
        var time = DateTimeOffset.UnixEpoch.AddDays(20000);
        Open().PreparePublication(id, 72, time);
        files.Stage = ExportFileState.Missing;
        files.Final = ExportFileState.Owned;
        var opened = Open();
        var snapshot = opened.Read();
        Assert.Empty(snapshot.Cleanup);
        Assert.Equal(new CompletedDownload(id, "Export title", MediaExportFormat.M4a, null, 72, time), Assert.Single(snapshot.Completed));
        using (var lease = Open().AcquireCompleted(id)) Assert.Equal(Final, lease?.Path);
        Assert.Equal(CleanupResult.Unavailable, opened.RetryCleanup(id));
        Assert.Empty(files.Removed);
        files.Final = ExportFileState.Mismatch;
        Assert.Null(Open().AcquireCompleted(id));
        Assert.Single(Open().Read().Completed);
    }

    [Fact]
    public void ConfirmedPublicationSurvivesAnInconclusiveReceiptRead()
    {
        Guid id = Begin();
        var journal = Open();
        var acknowledge = journal.PreparePublication(id, 72, DateTimeOffset.UtcNow);
        string record = Path.Combine(Store, $"{id:N}.json");
        // Simulate a transiently inaccessible local record during post-rename promotion.
        using (new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            acknowledge();
        var visible = journal.ReadLocal();
        Assert.Equal(id, Assert.Single(visible.Completed).Id);
        Assert.Empty(visible.Cleanup);
        Assert.Equal(id, Assert.Single(Open().ReadLocal().Cleanup).Id);
    }

    [Fact]
    public void MissingStageDuringCommitDoesNotDiscardReceiptWhenFinalIsLocked()
    {
        Guid id = Begin();
        Open().PreparePublication(id, 72, DateTimeOffset.UtcNow);
        files.Stage = ExportFileState.Missing;
        files.Final = ExportFileState.Unavailable;
        Assert.Equal(CleanupResult.Unavailable, Open().RetryCleanup(id));
        Assert.Single(Open().Read().Cleanup);
    }

    [Theory]
    [InlineData("Destination", "relative.m4a")]
    [InlineData("Destination", "executable.exe")]
    [InlineData("Schema", 42)]
    [InlineData("Id", "1df731137d0b4d31a14d9538fae72c67")]
    public void MalformedRecordsNeverReachFilesystemAuthority(string property, object value)
    {
        Guid id = Begin();
        string path = Assert.Single(Directory.GetFiles(Store, "*.json"));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[property] = JsonValue.Create(value);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Empty(Open().Read().Cleanup);
        Assert.Null(Open().AcquireCompleted(id));
        Assert.Equal(CleanupResult.Unavailable, Open().RetryCleanup(id));
        Assert.Empty(files.Inspected);
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file handles.";
        }
    }

    [WindowsFact]
    public void VerifiedLaunchLeaseDeniesChangesUntilDisposed()
    {
        var journal = new DownloadJournal(Store);
        Guid id = Guid.NewGuid();
        using (var stage = new MediaExportStaging(Final, id))
        {
            journal.Begin(id, "Export title", new(MediaExportFormat.M4a, null, 100), Final, journal.Capture(stage));
            stage.Stream.WriteByte(42);
            stage.Stream.Flush(flushToDisk: true);
            journal.PreparePublication(id, 1, DateTimeOffset.UtcNow);
            stage.Publish(Final);
        }
        using (var lease = journal.AcquireCompleted(id))
        {
            Assert.NotNull(lease);
            Assert.Equal(Final, lease.Path);
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(lease.Path));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Final, [99]));
            Assert.ThrowsAny<IOException>(() => File.Move(Final, Final + ".old"));
        }
        File.Move(Final, Final + ".old");
        File.WriteAllBytes(Final, [99]);
        Assert.Null(journal.AcquireCompleted(id));
    }

    [WindowsFact]
    public void WindowsScratchRecoveryTouchesOnlyRecordedInactiveFiles()
    {
        var journal = new DownloadJournal(Store);
        Guid id = Guid.NewGuid();
        var option = new MediaExportOption(MediaExportFormat.Mp3, null, 100);
        string scratch = journal.CreateScratch(id);
        string audio = Path.Combine(scratch, "audio.m4a");
        string output = Path.Combine(scratch, "output.mp3");
        string unknown = Path.Combine(scratch, "unowned.mp3");
        using (var active = WindowsExportJournalFiles.OpenScratchDirectory(scratch, remove: false))
        using (var input = new FileStream(audio, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var encoded = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var identities = new ScratchFiles(WindowsExportJournalFiles.Capture(audio, input.SafeFileHandle), null,
                WindowsExportJournalFiles.Capture(output, encoded.SafeFileHandle));
            journal.Begin(id, "Export title", option, Path.ChangeExtension(Final, "mp3"),
                WindowsExportJournalFiles.Capture(scratch, active, directory: true), identities);
            input.WriteByte(42);
            encoded.WriteByte(43);
        }
        string original = output + ".original";
        File.Move(output, original);
        File.WriteAllBytes(output, [99]);
        var reopened = new DownloadJournal(Store);
        Assert.Equal(id, Assert.Single(reopened.Read().Cleanup).Id);
        using (WindowsExportJournalFiles.OpenScratchDirectory(scratch, remove: false))
        {
            Assert.Equal(CleanupResult.Unavailable, reopened.RetryCleanup(id));
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(audio));
        }
        Assert.Equal(CleanupResult.IdentityMismatch, reopened.RetryCleanup(id));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 43 }, File.ReadAllBytes(original));
        File.Delete(output);
        File.Move(original, output);
        File.WriteAllBytes(unknown, [88]);
        Assert.Equal(CleanupResult.Unavailable, reopened.RetryCleanup(id));
        Assert.Equal(new byte[] { 88 }, File.ReadAllBytes(unknown));
        File.Delete(unknown);
        Assert.Equal(CleanupResult.Removed, reopened.RetryCleanup(id));
        Assert.False(Directory.Exists(scratch));
        Assert.Empty(reopened.Read().Cleanup);
    }

    [WindowsFact]
    public void WindowsCleanupChecksExclusiveHandleAndRefusesReplacement()
    {
        var journal = new DownloadJournal(Store);
        Guid id = Guid.NewGuid();
        string path = DownloadJournal.StagePath(Final, id);
        using (var stage = new MediaExportStaging(Final, id))
        {
            journal.Begin(id, "Export title", new(MediaExportFormat.M4a, null, 100), Final, journal.Capture(stage));
            Assert.Equal(CleanupResult.Unavailable, new DownloadJournal(Store).RetryCleanup(id));
            stage.Stream.WriteByte(42);
            stage.Stream.Flush(flushToDisk: true);
            // Release only the OS handle, simulating process death before Dispose could clean up.
            stage.Stream.Dispose();
        }
        string moved = path + ".original";
        File.Move(path, moved);
        File.WriteAllBytes(path, [99]);
        Assert.Equal(CleanupResult.IdentityMismatch, new DownloadJournal(Store).RetryCleanup(id));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path));
        File.Delete(path);
        File.Move(moved, path);
        Assert.Equal(CleanupResult.Removed, new DownloadJournal(Store).RetryCleanup(id));
        Assert.False(File.Exists(path));
    }

    [WindowsFact]
    public void WindowsRecoversRenameWithoutTouchingFinalAndDoesNotResolveModifiedLength()
    {
        var journal = new DownloadJournal(Store);
        Guid id = Guid.NewGuid();
        using (var stage = new MediaExportStaging(Final, id))
        {
            journal.Begin(id, "Export title", new(MediaExportFormat.M4a, null, 100), Final, journal.Capture(stage));
            stage.Stream.WriteByte(42);
            stage.Stream.Flush(flushToDisk: true);
            journal.PreparePublication(id, 1, DateTimeOffset.UtcNow);
            stage.Publish(Final);
            Assert.Equal(CleanupResult.Unavailable, journal.RetryCleanup(id));
        }
        var reopened = new DownloadJournal(Store);
        Assert.Equal(id, Assert.Single(reopened.ReadLocal().Cleanup).Id);
        reopened.RecoverPending();
        var recovered = reopened.ReadLocal();
        Assert.Equal(id, Assert.Single(recovered.Completed).Id);
        Assert.Empty(recovered.Cleanup);
        using (var lease = reopened.AcquireCompleted(id)) Assert.Equal(Final, lease?.Path);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Final));
        File.WriteAllBytes(Final, [42, 99]);
        Assert.Null(reopened.AcquireCompleted(id));
        Assert.Equal(CleanupResult.Unavailable, reopened.RetryCleanup(id));
        Assert.Equal(new byte[] { 42, 99 }, File.ReadAllBytes(Final));
    }

    private sealed class Files : IExportJournalFiles
    {
        public ExportFileState Stage = ExportFileState.Unavailable;
        public ExportFileState Final = ExportFileState.Missing;
        public List<string> Removed { get; } = [];
        public List<string> Inspected { get; } = [];
        public ExportFileIdentity Capture(MediaExportStaging stage) => Identity;
        public CompletedDownloadLease? Acquire(string path, ExportFileIdentity identity, long bytes) =>
            Inspect(path, identity, bytes, remove: false) == ExportFileState.Owned
                ? new(path, Stream.Null, Stream.Null) : null;
        public ExportFileState Inspect(string path, ExportFileIdentity identity, long? bytes, bool remove)
        {
            Assert.Equal(Identity, identity);
            Inspected.Add(path);
            if (Path.GetExtension(path) == ".part")
            {
                if (remove && Stage == ExportFileState.Owned) Removed.Add(path);
                return Stage;
            }
            Assert.False(remove);
            Assert.Equal(72L, bytes);
            return Final;
        }
    }

    public void Dispose() => root.Delete(recursive: true);
}
