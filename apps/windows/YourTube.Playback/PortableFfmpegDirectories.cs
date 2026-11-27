using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace YourTube.Playback;

// Hold each local directory against rename while resolving or using the executable. The
// FFmpeg tree itself is private to this Windows user, SYSTEM, and local administrators.
internal sealed class PortableFfmpegDirectories : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];

    internal void Pin(string path, bool create = false, bool privateDirectory = false)
    {
        if (OperatingSystem.IsWindows()) PinWindows(path, create, privateDirectory);
        else
        {
            if (create) Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Unexpected FFmpeg storage directory.");
        }
    }

    [SupportedOSPlatform("windows")]
    private void PinWindows(string path, bool create, bool privateDirectory)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is null) return;
        if (handles.Count == 0) PinWindows(directory.Parent.FullName, create, privateDirectory: false);
        if (create)
        {
            if (privateDirectory) directory.Create(CreatePrivateAccess());
            else if (!directory.Exists) directory.Create();
        }
        var handle = WindowsExportJournalFiles.OpenScratchDirectory(path, remove: false);
        if (handle.IsInvalid || !WindowsExportJournalFiles.TryIdentify(handle, path, out _, out _, directory: true))
        {
            handle.Dispose();
            throw new IOException("Could not protect the FFmpeg storage directory.");
        }
        handles.Add(handle);
        if (privateDirectory) VerifyPrivateAccess(directory);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] TrustedIdentities()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [identity.User ?? throw new IOException("No Windows profile identity."),
            new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreatePrivateAccess()
    {
        var identities = TrustedIdentities();
        var access = new DirectorySecurity();
        access.SetOwner(identities[0]);
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in identities)
            access.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return access;
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPrivateAccess(DirectoryInfo directory)
    {
        var identities = TrustedIdentities();
        var access = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!Equals(access.GetOwner(typeof(SecurityIdentifier)), identities[0]) ||
            !access.AreAccessRulesProtected || access.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow && !identities.Contains(rule.IdentityReference)))
            throw new IOException("FFmpeg storage is not private to this Windows profile.");
    }

    public void Dispose()
    {
        for (int i = handles.Count - 1; i >= 0; --i) handles[i].Dispose();
        handles.Clear();
    }
}
