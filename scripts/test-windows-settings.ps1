#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceSha,
      [string]$OutputDirectory = 'artifacts/native-settings')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SettingsCheckout([string]$ExpectedSha) {
    $head = git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedSha) { throw 'Native Settings source does not match checkout.' }
    $changes = @(git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Native Settings smoke requires clean committed inputs, including untracked files.' }
}

if (-not $IsWindows -or -not [Environment]::UserInteractive) { throw 'Native Settings smoke requires an interactive Windows desktop.' }
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
$publish = Join-Path $output 'publish'
$evidence = Join-Path $output 'evidence'
Push-Location $repo
try {
    Assert-SettingsCheckout $SourceSha
    if ((Test-Path -LiteralPath $output) -and
        (-not (Test-Path -LiteralPath $output -PathType Container) -or @(Get-ChildItem -LiteralPath $output -Force).Count -gt 0)) {
        throw 'Native Settings output must be new/empty.'
    }
    dotnet publish apps/windows/YourTube.Settings.NativeValidation/YourTube.Settings.NativeValidation.csproj -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Native Settings smoke publish failed.' }
    $null = New-Item -ItemType Directory -Path $evidence
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $publish 'YourTube.Settings.NativeValidation.exe'
    $info.WorkingDirectory = $evidence
    $info.UseShellExecute = $false
    $info.Environment['YOURTUBE_SETTINGS_EVIDENCE'] = $evidence
    $info.Environment['YOURTUBE_SETTINGS_SOURCE'] = $SourceSha
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(60000)) { throw 'Native Settings smoke timed out.' }
        if ($process.ExitCode -ne 0) { throw 'Native Settings smoke failed; see evidence/proof.json if present.' }
        $proof = Get-Content -LiteralPath (Join-Path $evidence 'proof.json') -Raw | ConvertFrom-Json
        if ($proof.sourceSha -cne $SourceSha -or $proof.result -cne 'passed' -or $proof.stage -cne 'complete' -or
            $proof.checks -ne 1 -or $proof.notesLoads -ne 1 -or $proof.downloads -ne 0 -or $proof.applies -ne 0 -or
            $proof.linkClicks -ne 0 -or @($proof.links).Count -ne 4 -or
            @($proof.links | Where-Object { $_.keyboardFocused -ne $true }).Count -ne 0) {
            throw 'Native Settings smoke evidence is incomplete or inconsistent.'
        }
        Write-Host 'Native Settings smoke passed (fixture only).'
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        $process.Dispose()
    }
} finally { Pop-Location }
