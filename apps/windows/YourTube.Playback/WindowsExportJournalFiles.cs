using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YourTube.Playback;

internal sealed class WindowsExportJournalFiles : IExportJournalFiles
{
    internal static WindowsExportJournalFiles Instance { get; } = new();
    private const uint GenericRead = 0x80000000, Delete = 0x10000;
    private const uint OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint FileTypeDisk = 1, VolumeNameGuid = 1;
    private const int ErrorFileNotFound = 2, FileIdInfo = 18;

    public ExportFileIdentity Capture(MediaExportStaging stage)
        => Capture(stage.StagingPath, stage.Stream.SafeFileHandle);

    internal static ExportFileIdentity Capture(string path, SafeFileHandle handle, bool directory = false)
    {
        if (!OperatingSystem.IsWindows() || !IsDrivePath(path) ||
            !TryIdentify(handle, path, out var identity, out _, directory) || !IsSupportedVolume(handle))
            throw new IOException("The export folder does not support ownership-checked recovery.");
        return identity;
    }

    public CompletedDownloadLease? Acquire(string path, ExportFileIdentity expected, long bytes) =>
        AcquireFile(path, expected, bytes, FileShare.Read);

    internal static CompletedDownloadLease? AcquireFile(string path, ExportFileIdentity expected, long? bytes, FileShare share)
    {
        if (!OperatingSystem.IsWindows() || !IsDrivePath(path)) return null;
        string parentPath = Path.GetDirectoryName(path)!;
        SafeFileHandle? parent = Open(parentPath, GenericRead, FileShare.ReadWrite);
        SafeFileHandle? file = null;
        try
        {
            if (!MatchesParent(parent, parentPath, expected.Volume)) return null;
            file = Open(path, GenericRead, share);
            if (file.IsInvalid || !TryIdentify(file, path, out var identity, out var length) ||
                identity != expected || (bytes is not null && bytes != length)) return null;
            var lease = new CompletedDownloadLease(path, file, parent);
            file = parent = null;
            return lease;
        }
        finally
        {
            file?.Dispose();
            parent?.Dispose();
        }
    }

    private static bool MatchesParent(SafeFileHandle parent, string path, Guid volume) =>
        !parent.IsInvalid && GetFileType(parent) == FileTypeDisk && GetFileInformationByHandle(parent, out var info) &&
        (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory &&
        HasExactPath(parent, path, out var actualVolume) && actualVolume == volume;

    public ExportFileState Inspect(string path, ExportFileIdentity expected, long? bytes, bool remove)
    {
        if (!OperatingSystem.IsWindows() || !IsDrivePath(path)) return ExportFileState.Unavailable;
        // Establish that the recorded parent/volume is reachable before interpreting file-not-found.
        // Deny delete-sharing on the parent while inspecting the child. Neither open follows a leaf reparse point.
        string parentPath = Path.GetDirectoryName(path)!;
        using var parent = Open(parentPath, GenericRead, FileShare.ReadWrite);
        if (parent.IsInvalid) return ExportFileState.Unavailable;
        if (!MatchesParent(parent, parentPath, expected.Volume)) return ExportFileState.Mismatch;
        return InspectChild(path, expected, bytes, remove);
    }

    private static ExportFileState InspectChild(string path, ExportFileIdentity expected, long? bytes, bool remove)
    {
        // GENERIC_READ participates in share checks, even for receipt lookup. A live exporter
        // still holding its exclusive handle (including after rename) is not recoverable yet.
        using var file = Open(path, GenericRead | (remove ? Delete : 0), remove ? FileShare.None : FileShare.Read);
        var state = IdentifyChild(file, path, expected, bytes);
        return state == ExportFileState.Owned && remove && !MediaExportStaging.MarkForDeletion(file)
            ? ExportFileState.Unavailable : state;
    }

    private static ExportFileState IdentifyChild(SafeFileHandle file, string path, ExportFileIdentity expected, long? bytes)
    {
        if (file.IsInvalid)
            return Marshal.GetLastPInvokeError() == ErrorFileNotFound ? ExportFileState.Missing : ExportFileState.Unavailable;
        if (!TryIdentify(file, path, out var identity, out var length) || identity != expected || (bytes is not null && bytes != length))
            return ExportFileState.Mismatch;
        return ExportFileState.Owned;
    }

    internal static ExportFileState CleanScratch(string path, ExportFileIdentity identity,
        IEnumerable<(string Path, ExportFileIdentity Identity)> children)
    {
        if (!OperatingSystem.IsWindows() || !IsDrivePath(path)) return ExportFileState.Unavailable;
        string parentPath = Path.GetDirectoryName(path)!;
        using var parent = Open(parentPath, GenericRead, FileShare.ReadWrite);
        if (parent.IsInvalid) return ExportFileState.Unavailable;
        if (!MatchesParent(parent, parentPath, identity.Volume)) return ExportFileState.Mismatch;
        using var scratch = OpenScratchDirectory(path, remove: true);
        if (scratch.IsInvalid)
            return Marshal.GetLastPInvokeError() == ErrorFileNotFound ? ExportFileState.Missing : ExportFileState.Unavailable;
        if (!TryIdentify(scratch, path, out var actual, out _, directory: true) || identity != actual)
            return ExportFileState.Mismatch;
        // Never enumerate or recurse. The empty-directory disposition fails if any unexpected entry remains.
        List<SafeFileHandle> owned = [];
        try
        {
            foreach (var child in children)
            {
                var file = Open(child.Path, GenericRead | Delete, FileShare.None);
                var state = IdentifyChild(file, child.Path, child.Identity, bytes: null);
                if (state == ExportFileState.Owned) owned.Add(file);
                else
                {
                    file.Dispose();
                    if (state != ExportFileState.Missing) return state;
                }
            }
            foreach (var file in owned)
                if (!MediaExportStaging.MarkForDeletion(file)) return ExportFileState.Unavailable;
        }
        finally { foreach (var file in owned) file.Dispose(); }
        return MediaExportStaging.MarkForDeletion(scratch) ? ExportFileState.Owned : ExportFileState.Unavailable;
    }

    internal static SafeFileHandle OpenScratchDirectory(string path, bool remove) =>
        Open(path, GenericRead | (remove ? Delete : 0), FileShare.ReadWrite);

    private static SafeFileHandle Open(string path, uint access, FileShare share) =>
        CreateFileW(@"\\?\" + path, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);

    internal static bool TryIdentify(SafeFileHandle file, string path, out ExportFileIdentity identity, out long bytes, bool directory = false)
    {
        identity = default;
        bytes = 0;
        if (GetFileType(file) != FileTypeDisk || !GetFileInformationByHandle(file, out var info) ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) !=
                (directory ? FileAttributes.Directory : 0) ||
            (!directory && info.Links != 1) || !HasExactPath(file, path, out Guid volume) ||
            !GetFileInformationByHandleEx(file, FileIdInfo, out var id, Marshal.SizeOf<NativeFileId>())) return false;
        identity = new(volume, id.VolumeSerial, id.Low, id.High, Join(info.CreationHigh, info.CreationLow));
        bytes = Join(info.SizeHigh, info.SizeLow);
        return identity.CreationTime > 0 && (identity.FileLow != 0 || identity.FileHigh != 0);
    }

    private static long Join(uint high, uint low) => unchecked((long)(((ulong)high << 32) | low));

    private static bool HasExactPath(SafeFileHandle file, string path, out Guid volume)
    {
        volume = default;
        var resolved = new StringBuilder(path.Length + 64);
        uint length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, 0);
        if (length == 0 || length >= resolved.Capacity || !string.Equals(resolved.ToString().TrimEnd('\\'),
            (@"\\?\" + path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        resolved.Clear();
        length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, VolumeNameGuid);
        if (length == 0 || length >= resolved.Capacity) return false;
        const string prefix = @"\\?\Volume{";
        string guidPath = resolved.ToString();
        int close = guidPath.IndexOf('}');
        return guidPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && close == prefix.Length + 36 &&
            Guid.TryParseExact(guidPath.AsSpan(prefix.Length, 36), "D", out volume) && volume != Guid.Empty &&
            // Refuse directory mount points and aliases rather than mistaking another volume for absence.
            guidPath.AsSpan(close + 1).TrimEnd('\\').Equals(path.AsSpan(2).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedVolume(SafeFileHandle file)
    {
        var filesystem = new StringBuilder(32);
        return GetVolumeInformationByHandleW(file, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            filesystem, (uint)filesystem.Capacity) && filesystem.ToString() is "NTFS" or "ReFS";
    }

    internal static bool IsDrivePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        if (path.Length == 3) return true;
        foreach (string part in path[3..].Split('\\'))
        {
            if (part.Length == 0 || part[^1] is '.' or ' ' || part.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
            int dot = part.IndexOf('.');
            string stem = (dot < 0 ? part : part[..dot]).TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "0123456789¹²³".Contains(stem[3]))) return false;
        }
        return true;
    }

    internal static void MoveJournalRecord(string source, string destination, bool overwrite)
    {
        const uint replaceExisting = 1, writeThrough = 8;
        if (!MoveFileExW(source, destination, writeThrough | (overwrite ? replaceExisting : 0)))
            throw new IOException("Could not persist local export journal.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId { public ulong VolumeSerial, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public FileAttributes Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial,
            SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeFileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out NativeFileId info, int bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint chars, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr volumeName, uint volumeNameSize,
        IntPtr serial, IntPtr maxComponent, IntPtr flags, StringBuilder filesystemName, uint filesystemNameSize);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);
}
