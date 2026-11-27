using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace YourTube.Playback;

internal sealed class MediaExportStaging : IDisposable
{
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private readonly string path;
    private readonly SafeFileHandle? ownedHandle;
    private bool published;
    internal FileStream Stream { get; }
    internal string StagingPath => path;
    internal bool Cleaned { get; private set; }

    internal static DirectoryInfo CreateEncoderScratch()
    {
        if (!OperatingSystem.IsWindows()) return Directory.CreateTempSubdirectory("yourtube-export-");

        // Use the current user's profile, not a configurable TEMP directory or the save destination.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(profile)) throw new IOException("No private media export scratch location.");
        var scratch = Path.Combine(profile, $"yourtube-export-{Guid.NewGuid():N}");
        CreatePrivateDirectory(scratch);
        return new DirectoryInfo(scratch);
    }

    internal static void CreatePrivateDirectory(string path)
    {
        // Unlike Directory.CreateDirectory, this fails if the name already exists; don't adopt it.
        if (!CreateDirectoryW(path, IntPtr.Zero)) throw new IOException("Could not create media export scratch.");
    }

    internal MediaExportStaging(string destination, Guid? operation = null)
    {
        path = DownloadJournal.StagePath(destination, operation ?? Guid.NewGuid());
        const FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        const int bufferSize = 64 * 1024;
        if (OperatingSystem.IsWindows())
        {
            Stream = new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.Synchronize,
                FileShare.None, bufferSize, options, fileSecurity: null);
            ownedHandle = Stream.SafeFileHandle; // Acquire before any buffered write; the getter can flush.
        }
        else Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, options);
    }

    internal void Publish(string destination)
    {
        if (ownedHandle is not null)
        {
            // FILE_RENAME_INFO: ReplaceIfExists = FALSE, RootDirectory = NULL, then the absolute UTF-16 path.
            int lengthOffset = 2 * IntPtr.Size;
            var name = MemoryMarshal.AsBytes(destination.AsSpan());
            var info = new byte[lengthOffset + sizeof(uint) + name.Length + sizeof(char)];
            BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), name.Length);
            name.CopyTo(info.AsSpan(lengthOffset + sizeof(uint)));
            if (!SetFileInformationByHandle(ownedHandle, FileRenameInfo, info, info.Length))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is ErrorFileExists or ErrorAlreadyExists) throw DestinationExists();
                throw new IOException("Could not publish media export.");
            }
        }
        else
        {
            // The shipping client uses the owned-handle Windows path; offline tests also run on Unix.
            File.Move(path, destination, overwrite: false);
        }
        published = true;
    }

    internal static PlaybackException DestinationExists() =>
        new("destination_exists", "A file or folder already exists at the export destination.");

    public void Dispose()
    {
        try
        {
            if (!published && ownedHandle is { IsClosed: false })
            {
                // Never delete by a pathname that could refer to a different file after this handle closes.
                if (!MarkForDeletion(ownedHandle))
                    throw new PlaybackException("export_cleanup_failed", "The incomplete download could not be removed.");
                Cleaned = true;
            }
        }
        finally
        {
            try { Stream.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!published && ownedHandle is null)
            {
                try { File.Delete(path); Cleaned = true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal static bool MarkForDeletion(SafeFileHandle file)
    {
        byte[] delete = [1]; // FILE_DISPOSITION_INFO.DeleteFile (BOOLEAN)
        return SetFileInformationByHandle(file, FileDispositionInfo, delete, delete.Length);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        byte[] information, int bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
