#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceSha,
      [string]$OutputDirectory = 'artifacts/native-playback')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PlaybackCheckout([string]$ExpectedSha) {
    $head = git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedSha) { throw 'Native playback source does not match checkout.' }
    $changes = @(git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Native playback proof requires clean committed inputs, including untracked files.' }
}

if (-not $IsWindows) { throw 'Real Windows playback is required; no Linux/headless substitute is accepted.' }
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw 'Native playback output must be new/empty.' }
$publish = Join-Path $output 'publish'
$evidence = Join-Path $output 'evidence'
Push-Location $repo
try {
    Assert-PlaybackCheckout $SourceSha
    dotnet publish apps/windows/YourTube.Playback.NativeValidation/YourTube.Playback.NativeValidation.csproj -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Native playback harness publish failed.' }
    $null = New-Item -ItemType Directory -Force $evidence
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class YourTubePlaybackDesktop {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
}
'@
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $publish 'YourTube.Playback.NativeValidation.exe'
    $info.WorkingDirectory = $evidence
    $info.UseShellExecute = $false
    $info.Environment['YOURTUBE_PLAYBACK_EVIDENCE'] = $evidence
    $info.Environment['YOURTUBE_PLAYBACK_SOURCE'] = $SourceSha
    $process = [Diagnostics.Process]::Start($info)
    $captured = [Collections.Generic.HashSet[string]]::new()
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    try {
        while (-not $process.HasExited) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Native playback acceptance timed out.' }
            $currentPath = Join-Path $evidence 'current.json'
            if (Test-Path -LiteralPath $currentPath) {
                $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json
                if ($current.stage -ceq 'playing' -and -not $captured.Contains($current.videoId)) {
                    if ($current.videoId -notin @('jNQXAC9IVRw','aqz-KE-bpKQ','YE7VzlLtp-4') -or $current.sourceSha -cne $SourceSha) { throw 'Unexpected native playback target/source.' }
                    $process.Refresh()
                    $handle = $process.MainWindowHandle
                    if ($handle -eq [IntPtr]::Zero) { throw 'Native playback has no visible window.' }
                    $null = [YourTubePlaybackDesktop]::ShowWindow($handle,3)
                    $null = [YourTubePlaybackDesktop]::SetForegroundWindow($handle)
                    Start-Sleep -Milliseconds 250
                    if ([YourTubePlaybackDesktop]::GetForegroundWindow() -ne $handle) { throw 'Cannot verify foreground native playback.' }
                    $rect = [YourTubePlaybackDesktop+RECT]::new()
                    if (-not [YourTubePlaybackDesktop]::GetWindowRect($handle,[ref]$rect)) { throw 'Cannot read native playback bounds.' }
                    $screen = [Windows.Forms.SystemInformation]::VirtualScreen
                    $left = [Math]::Max($rect.Left,$screen.Left); $top = [Math]::Max($rect.Top,$screen.Top)
                    $width = [Math]::Min($rect.Right,$screen.Right)-$left; $height = [Math]::Min($rect.Bottom,$screen.Bottom)-$top
                    if ($width -lt 500 -or $height -lt 300) { throw 'Native playback window is not large enough to inspect.' }
                    $bitmap = [Drawing.Bitmap]::new($width,$height)
                    $graphics = [Drawing.Graphics]::FromImage($bitmap)
                    try {
                        $graphics.CopyFromScreen($left,$top,0,0,$bitmap.Size)
                        $colors = [Collections.Generic.HashSet[int]]::new()
                        for ($x=40;$x -lt ($width-40);$x+=17) { for ($y=120;$y -lt ($height-80);$y+=17) { $null=$colors.Add($bitmap.GetPixel($x,$y).ToArgb()) } }
                        if ($colors.Count -lt 30) { throw 'Native playback image is blank or unverified.' }
                        $bitmap.Save((Join-Path $evidence "$($current.videoId).png"),[Drawing.Imaging.ImageFormat]::Png)
                        $null=$captured.Add($current.videoId)
                    } finally { $graphics.Dispose();$bitmap.Dispose() }
                }
            }
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Native playback harness failed; inspect sanitized evidence.' }
        if ($captured.Count -ne 3) { throw 'Native playback screenshots are incomplete.' }
        node scripts/verify-playback-proof.mjs (Join-Path $evidence 'proof.json') $SourceSha
        if ($LASTEXITCODE -ne 0) { throw 'Native playback proof was rejected.' }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true);$null=$process.WaitForExit(10000) }
        $process.Dispose()
    }
} finally { Pop-Location }
