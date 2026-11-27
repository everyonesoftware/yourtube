using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YourTube.Playback;

public sealed record CompletedDownload(Guid Id, string Title, MediaExportFormat Format, int? Height, long Bytes, DateTimeOffset SavedAt);
public sealed record DownloadCleanup(Guid Id, string Title);
public sealed record DownloadSnapshot(IReadOnlyList<CompletedDownload> Completed, IReadOnlyList<DownloadCleanup> Cleanup);
public enum CleanupResult { Removed, Unavailable, IdentityMismatch }

public sealed class CompletedDownloadLease : IDisposable
{
    private readonly IDisposable file;
    private readonly IDisposable parent;
    internal CompletedDownloadLease(string path, IDisposable file, IDisposable parent) =>
        (Path, this.file, this.parent) = (path, file, parent);
    public string Path { get; }
    public void Dispose()
    {
        try { file.Dispose(); }
        finally { parent.Dispose(); }
    }
}

internal readonly record struct ExportFileIdentity(Guid Volume, ulong Serial, ulong FileLow, ulong FileHigh, long CreationTime);
internal enum ExportFileState { Owned, Missing, Unavailable, Mismatch }
internal sealed record ScratchFiles(ExportFileIdentity Audio, ExportFileIdentity? Video, ExportFileIdentity Output);
internal interface IExportJournalFiles
{
    ExportFileIdentity Capture(MediaExportStaging stage);
    ExportFileState Inspect(string path, ExportFileIdentity identity, long? bytes, bool remove);
    CompletedDownloadLease? Acquire(string path, ExportFileIdentity identity, long bytes) => null;
    void MoveRecord(string source, string destination, bool overwrite)
    {
        if (OperatingSystem.IsWindows()) WindowsExportJournalFiles.MoveJournalRecord(source, destination, overwrite);
        else File.Move(source, destination, overwrite);
    }
}

/// <summary>Local export receipts and cleanup obligations. Use an app-private directory under the local user profile.</summary>
/// <remarks>Operations perform filesystem I/O. Call off the UI thread. Reading can reconcile a completed rename,
/// but never removes media; retry is explicit and affects only the exact recorded staging entry.</remarks>
public sealed class DownloadJournal
{
    private const int Schema = 1;
    private const int MaxRecordBytes = 64 * 1024;
    private const int MaxTitle = 512;
    private enum Phase { Staging, Publishing, Completed, Scratch }
    private sealed record Entry(int Schema, Guid Id, string Title, MediaExportFormat Format, int? Height,
        string Destination, ExportFileIdentity Identity, Phase Phase, long Bytes, DateTimeOffset? SavedAt)
    {
        public ScratchFiles? Scratch { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4
    };

    private readonly string directory;
    private readonly IExportJournalFiles files;
    private readonly Action waitForLock;
    private readonly ConcurrentDictionary<Guid, Entry> acknowledgedPublications = new();

    public DownloadJournal(string directory) : this(directory, WindowsExportJournalFiles.Instance) { }

    internal DownloadJournal(string directory, IExportJournalFiles files, Action? waitForLock = null)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use a full local journal path.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        this.files = files;
        this.waitForLock = waitForLock ?? (() => Thread.Sleep(20));
    }

    /// <summary>Project the local journal without inspecting any recorded media paths.</summary>
    public DownloadSnapshot ReadLocal() => ReadSnapshot(reconcile: false);

    /// <summary>Reconcile interrupted publications and retry cleanup for recorded obligations.</summary>
    public void RecoverPending()
    {
        foreach (var entry in ReadEntries())
        {
            if (entry.Phase == Phase.Completed) continue;
            if (entry.Phase == Phase.Publishing && InspectFinal(entry) == ExportFileState.Owned)
                RecoverCompletion(entry);
            else
                RetryCleanup(entry);
        }
    }

    public DownloadSnapshot Read() => ReadSnapshot(reconcile: true);

    private DownloadSnapshot ReadSnapshot(bool reconcile)
    {
        List<CompletedDownload> completed = [];
        List<DownloadCleanup> cleanup = [];
        foreach (var original in ReadEntries())
        {
            var entry = original;
            if (acknowledgedPublications.TryGetValue(entry.Id, out var acknowledged) && entry == acknowledged)
                entry = entry with { Phase = Phase.Completed };
            else if (reconcile && entry.Phase == Phase.Publishing && InspectFinal(entry) == ExportFileState.Owned)
                entry = RecoverCompletion(entry) ?? entry;
            if (entry.Phase == Phase.Completed)
                completed.Add(new(entry.Id, entry.Title, entry.Format, entry.Height, entry.Bytes, entry.SavedAt!.Value));
            else
                cleanup.Add(new(entry.Id, entry.Title));
        }
        return new(completed.OrderByDescending(item => item.SavedAt).ThenBy(item => item.Id).ToArray(),
            cleanup.OrderBy(item => item.Id).ToArray());
    }

    public CleanupResult RetryCleanup(Guid id)
    {
        try
        {
            if (Load(id) is not { } entry || entry.Phase == Phase.Completed) return CleanupResult.Unavailable;
            return RetryCleanup(entry);
        }
        catch (Exception e) when (IsStorageError(e)) { return CleanupResult.Unavailable; }
    }

    private CleanupResult RetryCleanup(Entry entry)
    {
        try
        {
            var id = entry.Id;
            var stage = entry.Scratch is { } scratch
                ? EncoderScratch.Clean(ScratchPath(id), new(entry.Format, entry.Height, 0), entry.Identity, scratch)
                : files.Inspect(StagePath(entry.Destination, id), entry.Identity, bytes: null, remove: true);
            if (stage == ExportFileState.Unavailable) return CleanupResult.Unavailable;
            if (stage == ExportFileState.Mismatch) return CleanupResult.IdentityMismatch;
            if (stage == ExportFileState.Missing && entry.Phase == Phase.Publishing)
            {
                // The rename might have won before a crash. Never retire the record while the
                // destination is inaccessible or a replacement makes the result ambiguous.
                switch (InspectFinal(entry))
                {
                    case ExportFileState.Owned: RecoverCompletion(entry); return CleanupResult.Unavailable;
                    case ExportFileState.Mismatch: return CleanupResult.IdentityMismatch;
                    case ExportFileState.Unavailable: return CleanupResult.Unavailable;
                }
            }
            using var gate = Lock();
            if (LoadLocked(id) != entry) return CleanupResult.Unavailable;
            File.Delete(RecordPath(id));
            return CleanupResult.Removed;
        }
        catch (Exception e) when (IsStorageError(e)) { return CleanupResult.Unavailable; }
    }


    /// <summary>Hold the verified file and parent handles through the explicit launch.</summary>
    public CompletedDownloadLease? AcquireCompleted(Guid id)
    {
        CompletedDownloadLease? lease = null;
        try
        {
            if (Load(id) is not { Phase: Phase.Completed or Phase.Publishing } entry) return null;
            lease = files.Acquire(entry.Destination, entry.Identity, entry.Bytes);
            if (lease is null) return null;
            var current = entry.Phase == Phase.Publishing ? RecoverCompletion(entry) : Load(id);
            if (current != entry && current != (entry with { Phase = Phase.Completed })) return null;
            var acquired = lease;
            lease = null;
            return acquired;
        }
        catch (Exception e) when (IsStorageError(e)) { return null; }
        finally { lease?.Dispose(); }
    }

    internal static string StagePath(string destination, Guid id) => Path.Combine(Path.GetDirectoryName(destination)!, $".yourtube-{id:N}.part");
    internal ExportFileIdentity Capture(MediaExportStaging stage) => files.Capture(stage);
    internal string ScratchPath(Guid id) => Path.Combine(directory, $"scratch-{id:N}");

    internal string CreateScratch(Guid id)
    {
        Directory.CreateDirectory(directory);
        var path = ScratchPath(id);
        if (OperatingSystem.IsWindows()) MediaExportStaging.CreatePrivateDirectory(path);
        else Directory.CreateDirectory(path); // Portable codec fixture only.
        return path;
    }

    internal void Begin(Guid id, string title, MediaExportOption option, string destination, ExportFileIdentity identity,
        ScratchFiles? scratch = null)
    {
        var safeTitle = new string(title.Take(MaxTitle).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (safeTitle.Length == 0) safeTitle = "Untitled download";
        var entry = new Entry(Schema, id, safeTitle, option.Format, option.Height, destination, identity,
            scratch is null ? Phase.Staging : Phase.Scratch, 0, null) { Scratch = scratch };
        if (!IsValid(entry, id)) throw new IOException("Invalid local export journal entry.");
        Directory.CreateDirectory(directory);
        using var gate = Lock();
        Save(entry, overwrite: false);
    }

    internal Action PreparePublication(Guid id, long bytes, DateTimeOffset savedAt)
    {
        using var gate = Lock();
        if (LoadLocked(id) is not { Phase: Phase.Staging } entry) throw new IOException("Missing local export journal entry.");
        var intent = entry with { Phase = Phase.Publishing, Bytes = bytes, SavedAt = savedAt };
        Save(intent, overwrite: true);
        return () => NotePublished(intent);
    }

    private void NotePublished(Entry intent)
    {
        // The caller has completed the owned-handle rename. Retain that exact acknowledgement
        // for local-only reads if the receipt write fails; restart still reconciles from disk.
        acknowledgedPublications[intent.Id] = intent;
        try
        {
            using var gate = Lock();
            var current = LoadLocked(intent.Id);
            if (current is null) return; // Inconclusive: a later read can still match the durable intent.
            if (current == intent) Save(intent with { Phase = Phase.Completed }, overwrite: true);
            acknowledgedPublications.TryRemove(intent.Id, out _);
        }
        catch (Exception e) when (IsStorageError(e)) { }
    }

    internal void NoteCleaned(Guid id)
    {
        try
        {
            using var gate = Lock();
            if (LoadLocked(id) is { Phase: not Phase.Completed }) File.Delete(RecordPath(id));
        }
        catch (Exception e) when (IsStorageError(e)) { }
    }

    private Entry? RecoverCompletion(Entry entry)
    {
        var completed = entry with { Phase = Phase.Completed };
        using var gate = Lock();
        var current = LoadLocked(entry.Id);
        if (current == completed) return current;
        if (current != entry) return null;
        try { Save(completed, overwrite: true); }
        catch (Exception e) when (IsStorageError(e)) { } // The verified rename is complete even if promotion must be retried.
        return completed;
    }

    private ExportFileState InspectFinal(Entry entry) => files.Inspect(entry.Destination, entry.Identity, entry.Bytes, remove: false);
    private string RecordPath(Guid id) => Path.Combine(directory, $"{id:N}.json");

    // Do not unlink the lock file: all instances and processes must lock the same entry.
    private FileStream Lock()
    {
        string path = Path.Combine(directory, ".lock");
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 10) { waitForLock(); }
        }
    }

    private Entry? Load(Guid id)
    {
        if (!Directory.Exists(directory)) return null;
        byte[]? bytes;
        using (Lock()) bytes = ReadRecordLocked(id);
        return ParseRecord(id, bytes);
    }

    private List<Entry> ReadEntries()
    {
        List<Entry> entries = [];
        if (!Directory.Exists(directory)) return entries;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && Load(id) is { } entry)
                entries.Add(entry);
        return entries;
    }

    private Entry? LoadLocked(Guid id) => ParseRecord(id, ReadRecordLocked(id));

    // The gate covers local metadata only. Windows replacement can fail if a record stream is open.
    private byte[]? ReadRecordLocked(Guid id)
    {
        try
        {
            string path = RecordPath(id);
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaxRecordBytes) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            return bytes;
        }
        catch (Exception e) when (IsStorageError(e)) { return null; }
    }

    private static Entry? ParseRecord(Guid id, byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(bytes, Json);
            return entry is not null && IsValid(entry, id) ? entry : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return null; }
    }

    private void Save(Entry entry, bool overwrite)
    {
        if (!IsValid(entry, entry.Id)) throw new IOException("Invalid local export journal entry.");
        string temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, entry, Json);
                stream.Flush(flushToDisk: true);
            }
            files.MoveRecord(temp, RecordPath(entry.Id), overwrite);
            created = false;
        }
        finally
        {
            if (created)
                try { File.Delete(temp); }
                catch (Exception e) when (IsStorageError(e)) { }
        }
    }

    private static bool IsValid(Entry entry, Guid id) =>
        entry.Schema == Schema && entry.Id == id && id != Guid.Empty &&
        entry.Title is { Length: > 0 and <= MaxTitle } && !entry.Title.Any(char.IsControl) &&
        ValidIdentity(entry.Identity) &&
        Enum.IsDefined(entry.Format) && (entry.Format is MediaExportFormat.Mp4 or MediaExportFormat.WebM
            ? entry.Height is > 0 : entry.Height is null) &&
        IsSafeDestination(entry.Destination, new MediaExportOption(entry.Format, entry.Height, 0).Extension) &&
        (entry.Phase switch
        {
            Phase.Staging => entry.Scratch is null && entry.Bytes == 0 && entry.SavedAt is null,
            Phase.Publishing or Phase.Completed => entry.Scratch is null && entry.Bytes > 0 &&
                entry.SavedAt is { Offset.Ticks: 0 } time && time > DateTimeOffset.UnixEpoch,
            Phase.Scratch => entry.Bytes == 0 && entry.SavedAt is null && entry.Format != MediaExportFormat.M4a &&
                entry.Scratch is { } scratch && ValidIdentity(scratch.Audio) && ValidIdentity(scratch.Output) &&
                (entry.Height is null ? scratch.Video is null : scratch.Video is { } video && ValidIdentity(video)),
            _ => false
        });

    private static bool ValidIdentity(ExportFileIdentity identity) =>
        identity.Volume != Guid.Empty && (identity.FileLow != 0 || identity.FileHigh != 0) && identity.CreationTime > 0;


    internal static bool IsSafeDestination(string? path, string extension)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) ||
            path.Any(char.IsControl) || !string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal)) return false;
        return !OperatingSystem.IsWindows() || WindowsExportJournalFiles.IsDrivePath(path);
    }

    internal static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;
}
