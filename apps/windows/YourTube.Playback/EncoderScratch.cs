using Microsoft.Win32.SafeHandles;

namespace YourTube.Playback;

// Windows profiles retain an identity-bound cleanup record before the first media write.
internal sealed class EncoderScratch : IDisposable
{
    private readonly string directory;
    private readonly MediaExportOption option;
    private readonly DownloadJournal? journal;
    private readonly Guid id = Guid.NewGuid();
    private readonly SafeFileHandle? active;
    private readonly ExportFileIdentity directoryIdentity;
    private readonly List<(string Path, ExportFileIdentity Identity)> reserved = [];
    private readonly List<CompletedDownloadLease> leases = [];
    private CompletedDownloadLease? outputLease;
    private bool recorded;

    internal string Audio => Path.Combine(directory, "audio.m4a");
    internal string? Video => option.Height is null ? null : Path.Combine(directory, "video.mp4");
    internal string Output => Path.Combine(directory, "output" + option.Extension);
    internal bool ReservedOutput => OperatingSystem.IsWindows() || journal is not null;

    internal EncoderScratch(MediaExportOption option, string destination, string title, DownloadJournal? journal)
    {
        this.option = option;
        this.journal = journal;
        directory = journal is null ? MediaExportStaging.CreateEncoderScratch().FullName : journal.CreateScratch(id);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                active = WindowsExportJournalFiles.OpenScratchDirectory(directory, remove: false);
                directoryIdentity = WindowsExportJournalFiles.Capture(directory, active, directory: true);
            }
            else directoryIdentity = PortableFixtureIdentity();

            Reserve(Audio);
            ExportFileIdentity? video = Video is { } path ? Reserve(path) : null;
            if (ReservedOutput)
            {
                var output = Reserve(Output);
                if (journal is not null)
                {
                    journal.Begin(id, title, option, destination, directoryIdentity, new(reserved[0].Identity, video, output));
                    recorded = true;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private ExportFileIdentity Reserve(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var identity = OperatingSystem.IsWindows()
            ? WindowsExportJournalFiles.Capture(path, file.SafeFileHandle) : PortableFixtureIdentity();
        reserved.Add((path, identity));
        return identity;
    }

    internal FileStream OpenInput(string path)
    {
        var identity = reserved.Single(item => item.Path == path).Identity;
        // Open without truncating; prove identity on the handle before handing it to the writer.
        var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows() && WindowsExportJournalFiles.Capture(path, file.SafeFileHandle) != identity)
                throw new IOException("The reserved export input changed.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal void ProtectForEncoding()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var item in reserved)
        {
            bool output = item.Path == Output;
            var lease = WindowsExportJournalFiles.AcquireFile(item.Path, item.Identity, output ? 0 : null,
                output ? FileShare.ReadWrite : FileShare.Read) ?? throw new IOException("The reserved export file changed.");
            if (output) outputLease = lease;
            else leases.Add(lease);
        }
    }

    internal FileStream OpenEncoded()
    {
        // Drop write sharing once the converter exits. Verify the new read handle itself before
        // copying in case the output name was substituted during the share-mode transition.
        outputLease?.Dispose();
        outputLease = null;
        var file = new FileStream(Output, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows() && WindowsExportJournalFiles.Capture(Output, file.SafeFileHandle) !=
                reserved.Single(item => item.Path == Output).Identity)
                throw new IOException("The reserved export output changed.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static ExportFileIdentity PortableFixtureIdentity() => new(Guid.NewGuid(), 1, 1, 1, 1);

    internal static ExportFileState Clean(string path, MediaExportOption option, ExportFileIdentity identity, ScratchFiles scratch)
    {
        List<(string, ExportFileIdentity)> children = [(Path.Combine(path, "audio.m4a"), scratch.Audio),
            (Path.Combine(path, "output" + option.Extension), scratch.Output)];
        if (scratch.Video is { } video) children.Add((Path.Combine(path, "video.mp4"), video));
        return WindowsExportJournalFiles.CleanScratch(path, identity, children);
    }

    public void Dispose()
    {
        outputLease?.Dispose();
        foreach (var lease in leases) lease.Dispose();
        active?.Dispose();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (recorded) journal!.RetryCleanup(id);
                else if (directoryIdentity != default) WindowsExportJournalFiles.CleanScratch(directory, directoryIdentity, reserved);
            }
            else // Portable codec fixture path; never used for restart deletion authority.
            {
                foreach (var item in reserved) File.Delete(item.Path);
                if (!ReservedOutput) File.Delete(Output);
                Directory.Delete(directory);
                if (recorded) journal!.NoteCleaned(id);
            }
        }
        catch (Exception e) when (DownloadJournal.IsStorageError(e)) { }
    }
}
