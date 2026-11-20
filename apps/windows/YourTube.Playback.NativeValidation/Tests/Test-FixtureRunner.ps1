#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath('../../../..', $PSScriptRoot)
$runner = Join-Path $repo 'scripts/test-windows-playback.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
$validator = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-FixturePlaybackProof' }, $false)
. ([scriptblock]::Create($validator.Extent.Text))
$sha = 'a' * 40
$valid = @'
{"schemaVersion":2,"mode":"fixture","status":"passed","native":true,"authenticated":false,"adapter":"NativePlaybackSource","results":[{"videoId":"yt-fixture0","status":"passed","disposed":true,"videoWidth":320,"videoHeight":180,"audioTracks":1,"videoTracks":1,"durationSeconds":18,"initialPosition":0,"positionBeforeSeek":1.5,"seekTarget":8,"seekCompleted":true,"positionAtSeekLanding":8,"pausedAtSeekLanding":true,"positionBeforeResume":8,"positionAfterSeek":9.2,"pausedAdvance":0.05,"resourceReads":6,"resourceBytes":150000}],"lifecycle":{"callbackFailureLatched":true,"failureTeardown":true,"pendingAtReplacement":true,"pendingReadCanceled":true,"retiredCallbackDrained":true,"lateResultIgnored":true,"replacementAdvanced":true,"replacementTeardown":true}}
'@
function New-Proof {
    $proof = $valid | ConvertFrom-Json
    $proof | Add-Member -NotePropertyName sourceSha -NotePropertyValue $sha
    $proof.results[0] | Add-Member -NotePropertyName readRangedAudio -NotePropertyValue $true
    $proof.results[0] | Add-Member -NotePropertyName readRangedVideo -NotePropertyValue $true
    return $proof
}
Assert-FixturePlaybackProof (New-Proof) $sha
$mutations = @(
    { param($p) $p.mode = 'live' },
    { param($p) $p.sourceSha = 'b' * 40 },
    { param($p) $p.results[0].videoId = 'jNQXAC9IVRw' },
    { param($p) $p.lifecycle.lateResultIgnored = 'true' },
    { param($p) $p.results[0].audioTracks = 0 },
    { param($p) $p.results[0].readRangedAudio = $false },
    { param($p) $p.results[0].PSObject.Properties.Remove('readRangedVideo') },
    { param($p) $p.results[0].videoWidth = '320' },
    { param($p) $p.results[0].disposed = $false },
    { param($p) $p.results[0].seekCompleted = $false },
    { param($p) $p.results[0].positionAtSeekLanding = 9 },
    { param($p) $p.results[0].positionAfterSeek = 8.9 },
    { param($p) $p.results[0].PSObject.Properties.Remove('resourceBytes') }
)
foreach ($mutate in $mutations) {
    $proof = New-Proof
    $null = & $mutate $proof
    $rejected = $false
    try { Assert-FixturePlaybackProof $proof $sha } catch { $rejected = $true }
    if (-not $rejected) { throw "Fixture proof mutation was accepted: $mutate" }
}
'PASS: controlled playback proof accepts complete fixture evidence and rejects mismatched or incomplete evidence.'
