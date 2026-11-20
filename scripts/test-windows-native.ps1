#Requires -Version 7.2
<#
.SYNOPSIS
Fail-closed native Windows install / visible-window / uninstall acceptance gate.
.DESCRIPTION
Run only on a disposable, unlocked interactive Windows account with no existing YourTube
installation/profile. Requires PowerShell 7.2+. EvidenceDirectory must be empty/new.
No package extraction, process-only success, or headless fallback is allowed.
Pinned primary references for velopack/1.2.161 (not latest-version CLI assumptions):
https://github.com/velopack/velopack/blob/1.2.161/src/bins/src/setup.rs
https://github.com/velopack/velopack/blob/1.2.161/src/bins/src/update.rs
https://github.com/velopack/velopack/blob/1.2.161/src/bins/src/commands/install.rs
https://github.com/velopack/velopack/blob/1.2.161/src/bins/src/commands/uninstall.rs
https://github.com/velopack/velopack/blob/1.2.161/src/bins/src/windows/registry.rs
Setup --silent suppresses first launch; launch the installed current\YourTube.exe ourselves.
Uninstall returns success even when some removals fail; independently verify removal.
A scheduled self-delete can leave Update.exe briefly; poll, never manually delete to pass.
.EXAMPLE
pwsh -File scripts/test-windows-installer.ps1 -InstallerPath dist/windows-build/releases/YourTubeWindows-win-x64-Setup.exe -EvidenceDirectory dist/windows-installer-smoke
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [ValidateRange(10, 300)][int]$WindowTimeoutSeconds = 60,
    [ValidateRange(30, 600)][int]$OperationTimeoutSeconds = 180,
    [ValidateRange(10, 180)][int]$RemovalTimeoutSeconds = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$started = [DateTime]::UtcNow
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if ((Test-Path -LiteralPath $evidence) -and (@(Get-ChildItem -LiteralPath $evidence -Force).Count -gt 0)) {
    throw 'EvidenceDirectory must be empty: refusing to overwrite retained diagnostics.'
}
$null = New-Item -ItemType Directory -Path $evidence -Force
$summary = [ordered]@{
    schemaVersion = 1; status = 'failed'; startedUtc = $started.ToString('o')
    installer = $InstallerPath; installerSha256 = $null; installRoot = $null
    evidenceDirectory = $evidence; installVerified = $false; windowVerified = $false
    screenshotSha256 = $null; uninstallVerified = $false; cleanupRemovedInstallRoot = $false
    iconVerified = $false; taskbarCaptured = $false; taskbarSha256 = $null
    cleanupErrors = @(); failure = $null
}
$installRoot = $null
$appProfile = $null
$mutationStarted = $false
$transcriptStarted = $false
$nativeReady = $false
$window = $null
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\YourTubeWindows'

function Write-Json($Value, [string]$Name) {
    ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence $Name) -Encoding utf8
}
function Get-OwnedProcesses {
    if (-not $installRoot) { return }
    $prefix = $installRoot.TrimEnd('\') + '\'
    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    }
}
function Stop-OwnedProcesses {
    foreach ($owned in @(Get-OwnedProcesses)) {
        # Verify the current PID still names the same executable before terminating it.
        $current = Get-Process -Id $owned.ProcessId -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $owned.ExecutablePath) {
            $current.Kill($true)
            if (-not $current.WaitForExit(10000)) { throw "Could not stop owned PID $($owned.ProcessId)" }
        }
    }
}
function Invoke-BoundedProcess([string]$Executable, [string[]]$Arguments, [string]$Label) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = $evidence # never hold the install directory open during deletion
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    Write-Json @{ executable = $Executable; arguments = $Arguments } "$Label-command.json"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start $Label" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($OperationTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(10000)
            throw "$Label exceeded ${OperationTimeoutSeconds}s; process tree terminated"
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw "$Label log streams did not close" }
        $stdout.Result | Set-Content -LiteralPath (Join-Path $evidence "$Label-stdout.txt")
        $stderr.Result | Set-Content -LiteralPath (Join-Path $evidence "$Label-stderr.txt")
        Write-Json @{ exitCode = $process.ExitCode; exitedUtc = [DateTime]::UtcNow.ToString('o') } "$Label-result.json"
        if ($process.ExitCode -ne 0) { throw "$Label exited $($process.ExitCode)" }
    } finally {
        if ($process.Id -and -not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(10000)
        }
        $process.Dispose()
    }
}
function Get-StartMenuLinks {
    $roots = @([Environment]::GetFolderPath('StartMenu'), [Environment]::GetFolderPath('CommonStartMenu'),
        [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory'))
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($root in $roots | Select-Object -Unique) {
            if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
            foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -File) {
                $link = $shell.CreateShortcut($file.FullName)
                try {
                    if ($file.BaseName -in @('YourTube', 'YourTubeWindows') -or
                        ($installRoot -and $link.TargetPath.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase))) {
                        [pscustomobject]@{ path = $file.FullName; target = $link.TargetPath; arguments = $link.Arguments }
                    }
                } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
            }
        }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Get-RemainingInstallation {
    $files = @()
    if (Test-Path -LiteralPath $installRoot) {
        $files = @(Get-ChildItem -LiteralPath $installRoot -File -Recurse -Force | Select-Object -ExpandProperty FullName)
    }
    [pscustomobject]@{ files = $files; registryPresent = (Test-Path -LiteralPath $registryPath)
        shortcuts = @(Get-StartMenuLinks); processes = @(Get-OwnedProcesses | Select-Object ProcessId, ExecutablePath) }
}
function Assert-InstalledIcon([string]$InstalledPath, [string[]]$ExpectedPaths) {
    $diagnostic = [ordered]@{ installedPath = $InstalledPath; installedSha256 = $null
        comparison = 'unavailable'; references = @(); failure = $null }
    try {
        if (-not (Test-Path -LiteralPath $InstalledPath -PathType Leaf) -or (Get-Item -LiteralPath $InstalledPath).Length -le 0) {
            throw 'Missing or empty installed YourTube ICO'
        }
        $diagnostic.installedSha256 = (Get-FileHash -LiteralPath $InstalledPath -Algorithm SHA256).Hash
        foreach ($expected in $ExpectedPaths | Select-Object -Unique) {
            if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { continue }
            $hash = (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash
            $diagnostic.references += @{ path = $expected; sha256 = $hash }
            if ($hash -cne $diagnostic.installedSha256) {
                $diagnostic.comparison = 'mismatch'
                throw 'Installed YourTube ICO differs from available publish/source bytes'
            }
            $diagnostic.comparison = 'matched'
        }
    } catch { $diagnostic.failure = $_.ToString(); throw }
    finally { Write-Json $diagnostic 'installed-icon.json' }
}
function Save-WindowIcons($Candidate) {
    $diagnostic = [ordered]@{ windowHandle = $Candidate.Handle.ToInt64(); icons = @()
        pixelComparison = 'not-performed'; explorerGroupIconVerified = $false; failure = $null }
    try {
        foreach ($spec in @(@{ name = 'small'; kind = 0; classIndex = -34 }, @{ name = 'large'; kind = 1; classIndex = -14 })) {
            $result = [UIntPtr]::Zero
            $sent = [YourTubeSmoke.Native]::SendMessageTimeout($Candidate.Handle, 0x007F <# WM_GETICON #>,
                [UIntPtr]::new([uint32]$spec.kind), [IntPtr]::Zero, [YourTubeSmoke.Native]::SMTO_ABORTIFHUNG, 1000, [ref]$result)
            if ($sent -eq [IntPtr]::Zero) { throw "WM_GETICON timed out/failed for $($spec.name)" }
            $handle = [IntPtr]::new([long]$result.ToUInt64())
            $source = 'WM_GETICON'
            if ($handle -eq [IntPtr]::Zero) {
                # Documented fallback: GCLP_HICONSM (-34), GCLP_HICON (-14), not a fabricated system icon.
                $handle = [YourTubeSmoke.Native]::ClassIcon($Candidate.Handle, $spec.classIndex)
                $source = 'class-fallback'
            }
            $entry = [ordered]@{ size = $spec.name; source = $source; handle = $handle.ToInt64()
                classIndex = $spec.classIndex; png = "window-icon-$($spec.name).png"; sha256 = $null; width = 0; height = 0 }
            $diagnostic.icons += $entry
            if ($handle -eq [IntPtr]::Zero) { throw "No $($spec.name) HWND or class HICON" }
            # WM_GETICON/class handles are borrowed: destroy only our CopyIcon, never the app's icon.
            $copy = [YourTubeSmoke.Native]::CopyIcon($handle)
            if ($copy -eq [IntPtr]::Zero) { throw 'Cannot copy window HICON' }
            $icon = $null; $bitmap = $null
            try {
                $icon = [Drawing.Icon]::FromHandle($copy)
                $bitmap = $icon.ToBitmap()
                $png = Join-Path $evidence $entry.png
                $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
                $entry.width = $bitmap.Width; $entry.height = $bitmap.Height
                $entry.sha256 = (Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash
            } finally {
                if ($bitmap) { $bitmap.Dispose() }
                if ($icon) { $icon.Dispose() }
                $null = [YourTubeSmoke.Native]::DestroyIcon($copy)
            }
        }
    } catch { $diagnostic.failure = $_.ToString(); throw }
    finally { Write-Json $diagnostic 'window-icons.json' }
}
function Save-WindowScreenshot($Candidate) {
    # Refuse a background/occluded window: capture pixels from a foreground visible window.
    $null = [YourTubeSmoke.Native]::ShowWindow($Candidate.Handle, 3) # maximize to fit the runner display
    $null = [YourTubeSmoke.Native]::SetForegroundWindow($Candidate.Handle)
    Start-Sleep -Milliseconds 750
    if ([YourTubeSmoke.Native]::GetForegroundWindow() -ne $Candidate.Handle) { throw 'Cannot bring YourTube window to foreground for screenshot' }
    $rect = [YourTubeSmoke.Native+RECT]::new()
    # GetWindowRect includes invisible resize borders outside the display when maximized.
    # DWMWA_EXTENDED_FRAME_BOUNDS (9) gives the actual visible physical-pixel frame.
    if ([YourTubeSmoke.Native]::DwmGetWindowAttribute($Candidate.Handle, 9, [ref]$rect, [Runtime.InteropServices.Marshal]::SizeOf($rect)) -ne 0) {
        throw 'Cannot read visible screenshot bounds from DWM'
    }
    $bounds = [Windows.Forms.SystemInformation]::VirtualScreen
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -lt 100 -or $height -lt 100 -or $rect.Left -lt $bounds.Left -or $rect.Top -lt $bounds.Top -or
        $rect.Right -gt $bounds.Right -or $rect.Bottom -gt $bounds.Bottom) { throw 'YourTube window has invalid/offscreen capture bounds' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $width; $x += 17) {
            for ($y = 0; $y -lt $height; $y += 17) { $null = $colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        $bitmap.Save((Join-Path $evidence 'window.png'), [Drawing.Imaging.ImageFormat]::Png)
        if ($colors.Count -lt 8) { throw 'Screenshot is blank or has insufficient pixel variation; desktop capture not proven' }
        Write-Json @{ width = $width; height = $height; sampledColors = $colors.Count; foregroundHandle = $Candidate.Handle.ToInt64() } 'screenshot.json'
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Save-TaskbarScreenshot {
    $diagnostic = [ordered]@{ status = 'failed'; handle = 0; left = 0; top = 0; width = 0; height = 0
        sampledColors = 0; capture = 'visible-desktop-pixels'; visualInspection = 'required'; failure = $null }
    $bitmap = $null; $graphics = $null
    try {
        $taskbar = [YourTubeSmoke.Native]::FindWindow('Shell_TrayWnd', $null)
        $diagnostic.handle = $taskbar.ToInt64()
        if ($taskbar -eq [IntPtr]::Zero -or -not [YourTubeSmoke.Native]::IsWindowVisible($taskbar)) {
            throw 'Taskbar Shell_TrayWnd is not visible; no headless/synthetic fallback'
        }
        $rect = [YourTubeSmoke.Native+RECT]::new()
        if (-not [YourTubeSmoke.Native]::GetWindowRect($taskbar, [ref]$rect)) { throw 'Taskbar bounds unavailable' }
        $bounds = [Windows.Forms.SystemInformation]::VirtualScreen
        $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
        $diagnostic.left = $rect.Left; $diagnostic.top = $rect.Top
        $diagnostic.width = $width; $diagnostic.height = $height
        if ($width -lt 16 -or $height -lt 16 -or $rect.Left -lt $bounds.Left -or $rect.Top -lt $bounds.Top -or
            $rect.Right -gt $bounds.Right -or $rect.Bottom -gt $bounds.Bottom) {
            throw 'Taskbar hidden/auto-hidden or offscreen; visible taskbar capture required'
        }
        # Maximization normally respects the taskbar work area. Verify rather than change taskbar
        # settings, raise Explorer, move the pointer, clear caches, or manipulate user pins.
        foreach ($fx in @(0.1, 0.5, 0.9)) {
            foreach ($fy in @(0.25, 0.5, 0.75)) {
                $point = [YourTubeSmoke.Native+POINT]::new()
                $point.X = $rect.Left + [int](($width - 1) * $fx)
                $point.Y = $rect.Top + [int](($height - 1) * $fy)
                $hit = [YourTubeSmoke.Native]::WindowFromPoint($point)
                if ($hit -ne $taskbar -and -not [YourTubeSmoke.Native]::IsChild($taskbar, $hit)) {
                    throw 'Taskbar is occluded/auto-hidden at sampled desktop points; cannot prove visible capture'
                }
            }
        }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $width; $x += 7) {
            for ($y = 0; $y -lt $height; $y += 3) { $null = $colors.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        $diagnostic.sampledColors = $colors.Count
        $bitmap.Save((Join-Path $evidence 'taskbar.png'), [Drawing.Imaging.ImageFormat]::Png)
        if ($colors.Count -lt 8) { throw 'Taskbar capture is blank or insufficiently varied' }
        $diagnostic.status = 'captured'
    } catch { $diagnostic.failure = $_.ToString(); throw }
    finally {
        if ($graphics) { $graphics.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
        Write-Json $diagnostic 'taskbar.json'
    }
}

try {
    Start-Transcript -LiteralPath (Join-Path $evidence 'transcript.log') -Force | Out-Null
    $transcriptStarted = $true
    if (-not $IsWindows) { throw 'Native Windows is required; WSL/Linux is not a desktop acceptance environment' }
    $installer = (Resolve-Path -LiteralPath $InstallerPath).Path
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or [IO.Path]::GetExtension($installer) -ine '.exe') {
        throw 'InstallerPath must identify the actual provided Setup.exe file'
    }
    $summary.installer = $installer
    $summary.installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $installRoot = Join-Path ([IO.Path]::GetTempPath()) ('YourTubeWindows-smoke-' + [guid]::NewGuid().ToString('N'))
    $summary.installRoot = $installRoot
    $appProfile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EveryoneSoftware\YourTube'
    if (Test-Path -LiteralPath $registryPath) { throw 'Existing YourTubeWindows registration: use a disposable account' }
    if (Test-Path -LiteralPath $appProfile) { throw 'Existing YourTube profile: refusing to alter user data' }
    if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'YourTubeWindows')) { throw 'Existing default YourTubeWindows installation' }
    if (@(Get-StartMenuLinks).Count) { throw 'Existing Start menu shortcut: refusing to overwrite YourTube links' }
    if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
        throw 'An interactive non-session-0 Windows desktop is required (hosted runner capability is not assumed)'
    }
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace YourTubeSmoke {
    public class Window {
        public IntPtr Handle; public string Title; public int Pid;
        public bool Responsive; public int Width; public int Height;
    }
    public static class Native {
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll", EntryPoint="GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint="GetClassLongW")] public static extern uint GetClassLong32(IntPtr hWnd, int index);
        public static IntPtr ClassIcon(IntPtr hWnd, int index) {
            return IntPtr.Size == 8 ? GetClassLongPtr(hWnd, index) : new IntPtr(unchecked((int)GetClassLong32(hWnd, index)));
        }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extra);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool SwitchDesktop(IntPtr desktop);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        public static Window[] Find(int pid) {
            var found = new List<Window>();
            EnumWindows((h, ignored) => {
                uint owner; GetWindowThreadProcessId(h, out owner);
                if (owner != pid || !IsWindowVisible(h)) return true;
                var text = new StringBuilder(1024); GetWindowText(h, text, text.Capacity);
                RECT rect; if (!GetWindowRect(h, out rect)) return true;
                UIntPtr result;
                bool responsive = SendMessageTimeout(h, 0 /* WM_NULL */, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out result) != IntPtr.Zero;
                found.Add(new Window { Handle=h, Title=text.ToString(), Pid=pid, Responsive=responsive, Width=rect.Right-rect.Left, Height=rect.Bottom-rect.Top });
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }
    }
}
'@
    $nativeReady = $true
    $null = [YourTubeSmoke.Native]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    $desktop = [YourTubeSmoke.Native]::OpenInputDesktop(0, $false, 0x0100)
    if ($desktop -eq [IntPtr]::Zero) { throw 'No accessible input desktop (locked/headless runner)' }
    try { if (-not [YourTubeSmoke.Native]::SwitchDesktop($desktop)) { throw 'Input desktop is not usable/unlocked' } }
    finally { $null = [YourTubeSmoke.Native]::CloseDesktop($desktop) }
    Write-Json @{ machine = $env:COMPUTERNAME; userInteractive = [Environment]::UserInteractive
        sessionId = (Get-Process -Id $PID).SessionId; os = [Environment]::OSVersion.VersionString
        powershell = $PSVersionTable.PSVersion.ToString(); runnerImage = $env:ImageOS } 'environment.json'

    $mutationStarted = $true # cleanup even when Setup fails or times out partway through
    Invoke-BoundedProcess $installer @('--silent', '--installto', $installRoot, '--log', (Join-Path $evidence 'install.log')) 'install'
    $appExe = Join-Path $installRoot 'current\YourTube.exe'
    $updateExe = Join-Path $installRoot 'Update.exe'
    foreach ($relative in @('Update.exe', 'current\YourTube.exe', 'current\YourTube.dll', 'current\YourTube.runtimeconfig.json',
        'current\Microsoft.UI.Xaml.dll', 'current\Microsoft.WindowsAppRuntime.dll', 'current\coreclr.dll', 'current\hostfxr.dll', 'current\resources.pri')) {
        $file = Get-Item -LiteralPath (Join-Path $installRoot $relative)
        if ($file.Length -le 0) { throw "Empty installed file: $relative" }
    }
    Write-Json @(Get-ChildItem -LiteralPath $installRoot -Recurse -File | Select-Object FullName, Length) 'installed-files.json'
    # CI retains the sibling publish tree; source is an independent reference when a checkout exists.
    # A standalone downloaded installer may have neither: record unavailable, never claim a byte match.
    $expectedIcons = @((Join-Path (Split-Path (Split-Path $installer)) 'publish/Assets/YourTube.ico'),
        (Join-Path (Split-Path $PSScriptRoot) 'apps/windows/YourTube.App/Assets/YourTube.ico'))
    Assert-InstalledIcon (Join-Path $installRoot 'current\Assets\YourTube.ico') $expectedIcons
    if (-not (Test-Path -LiteralPath $registryPath)) { throw 'Installer did not register YourTubeWindows' }
    $registration = Get-ItemProperty -LiteralPath $registryPath
    if ($registration.InstallLocation.TrimEnd('\') -ine $installRoot.TrimEnd('\')) { throw 'Installer registered a different install location' }
    # Registry provider metadata includes recursive PSDrive/PSProvider object graphs.
    # Serializing that live object can hang despite the bounded installer process.
    Write-Json ($registration | Select-Object DisplayName, DisplayVersion, InstallLocation, UninstallString) 'registration.json'
    $links = @(Get-StartMenuLinks)
    Write-Json $links 'installed-shortcuts.json'
    if (-not @($links | Where-Object { $_.target.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw 'No installed YourTube Start menu shortcut targets the isolated installation'
    }
    $summary.installVerified = $true
    $previousDiagnostics = $env:MUSIC_STARTUP_DIAGNOSTICS
    try {
        $env:MUSIC_STARTUP_DIAGNOSTICS = $evidence
        $app = Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe) -PassThru `
            -RedirectStandardOutput (Join-Path $evidence 'app-stdout.txt') -RedirectStandardError (Join-Path $evidence 'app-stderr.txt')
    } finally { $env:MUSIC_STARTUP_DIAGNOSTICS = $previousDiagnostics }
    $appPid = $app.Id
    $deadline = [DateTime]::UtcNow.AddSeconds($WindowTimeoutSeconds)
    $stableSamples = 0
    $lastHandle = [IntPtr]::Zero
    $observations = [Collections.Generic.List[object]]::new()
    while ([DateTime]::UtcNow -lt $deadline) {
        $ownedApp = @(Get-OwnedProcesses | Where-Object { $_.ProcessId -eq $appPid -and $_.ExecutablePath -ieq $appExe })
        if (-not $ownedApp.Count) {
            $app.Refresh()
            $exitCode = if ($app.HasExited) { $app.ExitCode } else { $null }
            Write-Json @{ pid = $appPid; hasExited = $app.HasExited; exitCode = $exitCode; executable = $appExe } 'app-exit.json'
            throw "Installed YourTube.exe unavailable before responsive-window acceptance (exit code: $exitCode)"
        }
        $candidates = @([YourTubeSmoke.Native]::Find($appPid))
        $candidate = $null
        foreach ($item in $candidates) {
            $title = $item.Title
            $observations.Add(@{ utc = [DateTime]::UtcNow.ToString('o'); pid = $appPid; title = $title
                handle = $item.Handle.ToInt64(); responsive = $item.Responsive; width = $item.Width; height = $item.Height })
            if ($title -eq 'YourTube' -and $item.Responsive -and $item.Width -ge 100 -and $item.Height -ge 100) { $candidate = $item }
        }
        if ($candidate) {
            if ($candidate.Handle -eq $lastHandle) { $stableSamples++ } else { $stableSamples = 1 }
            $lastHandle = $candidate.Handle
            if ($stableSamples -ge 3) { $window = $candidate; break }
        } else { $stableSamples = 0; $lastHandle = [IntPtr]::Zero }
        Start-Sleep -Milliseconds 1000
    }
    Write-Json @($observations.ToArray()) 'window-observations.json'
    if (-not $window) { throw "No real responsive visible YourTube title/handle within ${WindowTimeoutSeconds}s" }
    Write-Json @{ pid = $appPid; executable = $appExe; title = $window.Title; handle = $window.Handle.ToInt64()
        responsiveSamples = $stableSamples; width = $window.Width; height = $window.Height } 'window.json'
    Save-WindowScreenshot $window
    $summary.screenshotSha256 = (Get-FileHash -LiteralPath (Join-Path $evidence 'window.png') -Algorithm SHA256).Hash
    Save-WindowIcons $window
    $summary.iconVerified = $true # installed nonempty ICO and real HICONs, NOT a pixel/artwork match
    Save-TaskbarScreenshot
    $summary.taskbarSha256 = (Get-FileHash -LiteralPath (Join-Path $evidence 'taskbar.png') -Algorithm SHA256).Hash
    $summary.taskbarCaptured = $true # visual inspection of Explorer's grouped icon remains separate
    # Re-probe after capture, not just a window that once existed.
    if (-not @([YourTubeSmoke.Native]::Find($appPid) | Where-Object { $_.Handle -eq $window.Handle -and $_.Responsive }).Count) {
        throw 'YourTube window stopped responding during screenshot capture'
    }
    $summary.windowVerified = $true
    $null = [YourTubeSmoke.Native]::PostMessage($window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) # WM_CLOSE
    $closeDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (@(Get-OwnedProcesses).Count -and [DateTime]::UtcNow -lt $closeDeadline) { Start-Sleep -Milliseconds 250 }
    if (@(Get-OwnedProcesses).Count) { throw 'YourTube did not exit after WM_CLOSE (forced termination is cleanup only)' }
    Invoke-BoundedProcess $updateExe @('--uninstall', '--silent', '--log', (Join-Path $evidence 'uninstall.log')) 'uninstall'
    $removeDeadline = [DateTime]::UtcNow.AddSeconds($RemovalTimeoutSeconds)
    do {
        $remaining = Get-RemainingInstallation
        if (-not $remaining.files.Count -and -not $remaining.registryPresent -and -not $remaining.shortcuts.Count -and -not $remaining.processes.Count) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $removeDeadline)
    Write-Json $remaining 'uninstallRemaining.json'
    if ($remaining.files.Count -or $remaining.registryPresent -or $remaining.shortcuts.Count -or $remaining.processes.Count) {
        throw 'Remaining installed payload, registry, shortcuts, or processes after silent uninstall'
    }
    $summary.uninstallVerified = $true
    $summary.status = 'passed'
} catch {
    $summary.status = 'failed'
    $summary.failure = $_.ToString()
    ($_ | Format-List * -Force | Out-String) | Set-Content -LiteralPath (Join-Path $evidence 'failure.txt')
} finally {
    if ($IsWindows) {
        # Windows Error Reporting can publish events shortly after the failing process exits.
        if ($summary.installVerified -and -not $summary.windowVerified) { Start-Sleep -Seconds 5 }
        try {
            Write-Json @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started.ToLocalTime() } -ErrorAction Stop |
                Where-Object { $_.ProviderName -in @('Application Error', '.NET Runtime', 'Windows Error Reporting') } |
                Select-Object TimeCreated, Id, ProviderName, Message) 'events.json'
        } catch { $_.ToString() | Set-Content -LiteralPath (Join-Path $evidence 'events-unavailable.txt') }
    }
    if ($mutationStarted) {
        try {
            Write-Json @(Get-OwnedProcesses | Select-Object ProcessId, ExecutablePath, CommandLine) 'cleanup-processes.json'
            if (Test-Path -LiteralPath $installRoot) {
                Write-Json @(Get-ChildItem -LiteralPath $installRoot -Recurse -File -Force | Select-Object FullName, Length) 'cleanup-files.json'
                foreach ($log in Get-ChildItem -LiteralPath $installRoot -Recurse -File -Filter '*.log') {
                    $relative = [IO.Path]::GetRelativePath($installRoot, $log.FullName).Replace('\', '_')
                    Copy-Item -LiteralPath $log.FullName -Destination (Join-Path $evidence "retained-$relative")
                }
            }
        } catch { $summary.cleanupErrors += "Diagnostics: $_" }
        try { Stop-OwnedProcesses } catch { $summary.cleanupErrors += "Stop: $_" }
        # Always attempt supported uninstall on partial installation/failure, BEFORE fallback removal.
        if ($summary.status -ne 'passed' -and (Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe'))) {
            try {
                Invoke-BoundedProcess (Join-Path $installRoot 'Update.exe') @('--uninstall', '--silent', '--log', (Join-Path $evidence 'cleanup-uninstall.log')) 'cleanup-uninstall'
            } catch { $summary.cleanupErrors += "Uninstall: $_" }
        }
        try { Stop-OwnedProcesses } catch { $summary.cleanupErrors += "Stop after uninstall: $_" }
        # These are only newly created test-owned links/profile/key, never existing user state.
        try {
            foreach ($link in @(Get-StartMenuLinks)) {
                if ($link.target.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $link.path -Force }
            }
            if (Test-Path -LiteralPath $registryPath) {
                $entry = Get-ItemProperty -LiteralPath $registryPath
                if ($entry.InstallLocation.TrimEnd('\') -ieq $installRoot.TrimEnd('\')) { Remove-Item -LiteralPath $registryPath -Recurse -Force }
                else { throw 'Refusing cleanup of registry entry not owned by isolated install' }
            }
            if (Test-Path -LiteralPath $appProfile) { Remove-Item -LiteralPath $appProfile -Recurse -Force }
        } catch { $summary.cleanupErrors += "User state: $_" }
        try {
            # Bounded retry allows Velopack's delayed Update.exe self-delete to finish first.
            $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                try {
                    if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
                    break
                } catch { if ([DateTime]::UtcNow -ge $cleanupDeadline) { throw }; Start-Sleep -Milliseconds 500 }
            } while ($true)
            $summary.cleanupRemovedInstallRoot = -not (Test-Path -LiteralPath $installRoot)
            if (-not $summary.cleanupRemovedInstallRoot) { throw 'Isolated install directory survived cleanup' }
        } catch { $summary.cleanupErrors += "Directory: $_" }
    }
    if ($summary.cleanupErrors.Count) { $summary.status = 'failed' }
    $summary.finishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { $summary.cleanupErrors += "Transcript: $_"; $summary.status = 'failed' }
    }
    Write-Json $summary 'summary.json'
}
$summary | ConvertTo-Json -Depth 12 | Write-Output
if ($summary.status -ne 'passed') { exit 1 }
exit 0