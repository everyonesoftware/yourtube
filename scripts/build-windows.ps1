#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^0\.1\.[1-9][0-9]{0,4}$')][string]$Version,
      [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceSha,
      [string]$OutputDirectory = 'artifacts/windows')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NativePayload([string]$Directory) {
    foreach ($name in @('YourTube.exe','YourTube.dll','YourTube.Core.dll','YourTube.Update.dll','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','coreclr.dll','resources.pri')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing native publish payload: $name" }
    }
}
function Assert-Feed([string]$Path, [string]$ExpectedVersion) {
    $feed = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $assets = @($feed.Assets)
    if ($assets.Count -ne 1) { throw 'Expected exactly one full Windows package' }
    $asset = $assets[0]
    if ($asset.PackageId -cne 'YourTubeWindows' -or $asset.Version -cne $ExpectedVersion -or $asset.Type -cne 'Full' -or $asset.FileName -cne "YourTubeWindows-$ExpectedVersion-win-x64-full.nupkg") { throw 'Unexpected Windows package identity' }
}

if (-not $IsWindows) { throw 'A distributable requires a real native Windows build; Linux compilation is not packaging.' }
if ([int]$Version.Split('.')[2] -gt 65534) { throw 'Version exceeds assembly component limit' }
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if ((git rev-parse HEAD).Trim() -cne $SourceSha) { throw 'Source revision does not match checkout' }
    if (@(git status --porcelain --untracked-files=all).Count -ne 0) { throw 'Release inputs must be clean, including untracked files' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path $output) -and @(Get-ChildItem $output -Force).Count -gt 0) { throw 'Packaging output must be new/empty' }
    $publish = Join-Path $output 'publish'
    $releases = Join-Path $output 'releases'
    $null = New-Item -ItemType Directory -Force $publish,$releases
    dotnet publish apps/windows/YourTube.App/YourTube.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:InformationalVersion="$Version+$SourceSha" -p:IncludeSourceRevisionInInformationalVersion=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Native Windows publish failed' }
    Assert-NativePayload $publish
    $productVersion = (Get-Item (Join-Path $publish 'YourTube.exe')).VersionInfo.ProductVersion
    if ($productVersion -cne "$Version+$SourceSha") { throw 'Published executable has incorrect source/version identity' }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Velopack tool restore failed' }
    dotnet tool run vpk -- pack --packId YourTubeWindows --packVersion $Version --packDir $publish --mainExe YourTube.exe --packTitle YourTube --packAuthors 'Dan Schulte' --channel win-x64 --runtime win-x64 --delta None --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed' }
    Assert-Feed (Join-Path $releases 'releases.win-x64.json') $Version
    $assets = @()
    foreach ($name in @('YourTubeWindows-win-x64-Setup.exe',"YourTubeWindows-$Version-win-x64-full.nupkg",'releases.win-x64.json')) {
        $path = Join-Path $releases $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing packaged asset: $name" }
        $digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        $assets += @{name=$name;sha256=$digest}
        "$digest  $name" | Set-Content -LiteralPath "$path.sha256" -Encoding utf8NoBOM
    }
    @{schemaVersion=1;platform='windows';packageId='YourTubeWindows';channel='win-x64';version=$Version;tag="v$Version";sourceSha=$SourceSha;assets=$assets} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $releases 'yourtube-windows.json') -Encoding utf8NoBOM
} finally { Pop-Location }
