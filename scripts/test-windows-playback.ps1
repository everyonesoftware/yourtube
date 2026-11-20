#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceSha,
      [switch]$Fixture,
      [string]$OutputDirectory = $(if ($Fixture) { 'artifacts/native-playback-fixture' } else { 'artifacts/native-playback' }))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PlaybackCheckout([string]$ExpectedSha) {
    $head = git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedSha) { throw 'Native playback source does not match checkout.' }
    $changes = @(git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Native playback proof requires clean committed inputs, including untracked files.' }
}

function Assert-FixturePlaybackProof($Proof, [string]$ExpectedSha) {
    if ($Proof.schemaVersion -ne 2 -or $Proof.mode -cne 'fixture' -or $Proof.sourceSha -cne $ExpectedSha -or
        $Proof.status -cne 'passed' -or $Proof.native -isnot [bool] -or -not $Proof.native -or
        $Proof.authenticated -isnot [bool] -or $Proof.authenticated -or $Proof.adapter -cne 'NativePlaybackSource' -or
        @($Proof.results).Count -ne 1 -or $Proof.results[0].videoId -cne 'yt-fixture0' -or
        $Proof.results[0].status -cne 'passed') { throw 'Controlled native playback proof was rejected.' }
    foreach ($check in @('callbackFailureLatched','failureTeardown','pendingAtReplacement','pendingReadCanceled',
            'retiredCallbackDrained','lateResultIgnored','replacementAdvanced','replacementTeardown')) {
        $value = $Proof.lifecycle.$check
        if ($value -isnot [bool] -or -not $value) { throw 'Controlled native lifecycle proof was rejected.' }
    }
    $row = $Proof.results[0]
    foreach ($check in @('disposed','seekCompleted','pausedAtSeekLanding','readRangedAudio','readRangedVideo')) {
        $value = $row.$check
        if ($value -isnot [bool] -or -not $value) { throw 'Controlled native playback or teardown was not verified.' }
    }
    foreach ($field in @('videoWidth','videoHeight','audioTracks','videoTracks','durationSeconds','resourceReads','resourceBytes',
            'initialPosition','positionBeforeSeek','seekTarget','positionAtSeekLanding','positionBeforeResume','positionAfterSeek','pausedAdvance')) {
        $value = $row.$field
        if (($value -isnot [long] -and $value -isnot [int] -and $value -isnot [double]) -or
            -not [double]::IsFinite($value) -or $value -lt 0) { throw 'Controlled native playback metrics are invalid.' }
    }
    if ($row.videoWidth -le 0 -or $row.videoHeight -le 0 -or $row.audioTracks -le 0 -or $row.videoTracks -le 0 -or
        $row.resourceReads -le 0 -or $row.resourceBytes -le 0 -or $row.pausedAdvance -gt 0.35 -or
        $row.positionBeforeSeek -lt ($row.initialPosition + 1) -or $row.seekTarget -lt ($row.positionBeforeSeek + 2) -or
        $row.seekTarget -ge $row.durationSeconds -or [Math]::Abs($row.positionAtSeekLanding - $row.seekTarget) -gt 0.75 -or
        [Math]::Abs($row.positionBeforeResume - $row.seekTarget) -gt 0.75 -or
        [Math]::Abs($row.positionBeforeResume - $row.positionAtSeekLanding) -gt 0.35 -or
        $row.positionAfterSeek -lt ([Math]::Max([double]$row.seekTarget, [double]$row.positionBeforeResume) + 1) -or
        $row.positionAfterSeek -gt ($row.durationSeconds + 0.5)) { throw 'Controlled native playback metrics did not meet acceptance.' }
}

if (-not $IsWindows) { throw 'Real Windows playback is required; no Linux/headless substitute is accepted.' }
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
[string[]]$targetIds = if ($Fixture) { 'yt-fixture0' } else { 'jNQXAC9IVRw','aqz-KE-bpKQ','YE7VzlLtp-4' }
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
    $info.Environment['YOURTUBE_PLAYBACK_MODE'] = if ($Fixture) { 'fixture' } else { 'live' }
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
                    if ($current.videoId -cnotin $targetIds -or $current.sourceSha -cne $SourceSha) { throw 'Unexpected native playback target/source.' }
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
        if ($captured.Count -ne $targetIds.Count) { throw 'Native playback screenshots are incomplete.' }
        $proofPath = Join-Path $evidence 'proof.json'
        if ($Fixture) {
            Assert-FixturePlaybackProof (Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json) $SourceSha
            Write-Host 'PASS: controlled native DASH audio/video, paused seek, callback failure and pending replacement teardown.'
        } else {
            node scripts/verify-playback-proof.mjs $proofPath $SourceSha
            if ($LASTEXITCODE -ne 0) { throw 'Native playback proof was rejected.' }
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true);$null=$process.WaitForExit(10000) }
        $process.Dispose()
    }
} finally { Pop-Location }
