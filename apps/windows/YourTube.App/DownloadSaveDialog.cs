using System.Runtime.InteropServices;

namespace YourTube.App;

// The Windows App SDK 1.8 FileSavePicker creates an empty destination. Use the
// Shell's native Save As dialog without that probe so only export can publish a file.
internal static class DownloadSaveDialog
{
    private const int Canceled = unchecked((int)0x800704C7);
    private const uint OverwritePrompt = 0x2;
    private const uint StrictFileTypes = 0x4;
    private const uint NoChangeDirectory = 0x8;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint NoTestFileCreate = 0x10000;
    private const uint DontAddToRecent = 0x2000000;
    private const uint FileSystemPath = 0x80058000;

    // Called and canceled on the owning window's STA. Show pumps the native modal loop.
    public static string? Pick(nint owner, string fileName, string formatLabel, string extension, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = (IFileDialog)new FileSaveDialog();
        try
        {
            dialog.GetOptions(out var flags);
            dialog.SetOptions((flags & ~OverwritePrompt) | StrictFileTypes | NoChangeDirectory |
                ForceFileSystem | PathMustExist | NoTestFileCreate | DontAddToRecent);
            dialog.SetFileTypes(1, [new FileTypeFilter(formatLabel, "*" + extension)]);
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension(extension[1..]);
            dialog.SetFileName(fileName);
            dialog.SetTitle("Save download as");
            using var cancel = token.Register(() => dialog.Close(Canceled));
            token.ThrowIfCancellationRequested();
            var result = dialog.Show(owner);
            if (result == Canceled) return null;
            Marshal.ThrowExceptionForHR(result);
            token.ThrowIfCancellationRequested();
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(FileSystemPath, out var path);
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileTypeFilter(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Pattern = pattern;
    }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialog { }

    // Native vtable order: IModalWindow followed by IFileDialog.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FileTypeFilter[] types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
    }
}
