#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Build script syntax invalid'}
$functions=$ast.FindAll({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst]},$true)
foreach($function in $functions){Invoke-Expression $function.Extent.Text}
$root=Join-Path ([IO.Path]::GetTempPath()) ('yourtube-packaging-'+[guid]::NewGuid())
$null=New-Item -ItemType Directory $root
try {
 $names=@('YourTube.exe','YourTube.dll','YourTube.Core.dll','YourTube.Update.dll','YourTube.Playback.dll','YoutubeExplode.dll','THIRD-PARTY-NOTICES.md','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','coreclr.dll','resources.pri')
 foreach($name in $names){[IO.File]::WriteAllText((Join-Path $root $name),'fixture-not-binary')}
 $rejected=$false; try {Assert-NativePayload $root}catch{$rejected=$true}
 if(-not $rejected){throw 'Missing runtime icon accepted'}
 $assets=Join-Path $root 'Assets'
 $null=New-Item -ItemType Directory $assets
 [IO.File]::WriteAllText((Join-Path $assets 'YourTube.ico'),'fixture-not-binary')
 Assert-NativePayload $root
 foreach($name in @('YourTube.Playback.dll','YoutubeExplode.dll','THIRD-PARTY-NOTICES.md')) {
  Remove-Item (Join-Path $root $name)
  $rejected=$false;try{Assert-NativePayload $root}catch{$rejected=$true}
  if(-not $rejected){throw "Missing playback dependency or notice accepted: $name"}
  [IO.File]::WriteAllText((Join-Path $root $name),'fixture-not-binary')
 }
 [IO.File]::WriteAllText((Join-Path $assets 'YourTube.ico'),'')
 $rejected=$false; try {Assert-NativePayload $root}catch{$rejected=$true}
 if(-not $rejected){throw 'Empty runtime icon accepted'}
 [IO.File]::WriteAllText((Join-Path $assets 'YourTube.ico'),'fixture-not-binary')
 Remove-Item (Join-Path $root 'Microsoft.UI.Xaml.dll')
 $rejected=$false; try {Assert-NativePayload $root}catch{$rejected=$true}
 if(-not $rejected){throw 'Incomplete native payload accepted'}
 $feed=Join-Path $root 'feed.json'
 @{Assets=@(@{PackageId='YourTubeWindows';Version='0.1.1';Type='Full';FileName='YourTubeWindows-0.1.1-win-x64-full.nupkg'})}|ConvertTo-Json -Depth 5|Set-Content $feed
 Assert-Feed $feed '0.1.1'
 $rejected=$false; try {Assert-Feed $feed '0.1.2'}catch{$rejected=$true}
 if(-not $rejected){throw 'Wrong package version accepted'}
 'Packaging validators passed accepted and rejected fixtures.'
}finally{Remove-Item $root -Recurse -Force}
foreach($file in @('build-windows.ps1','test-windows-native.ps1','test-windows-playback.ps1')){
 $tokens=$null;$errors=$null;$null=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
 if($errors.Count){throw "Invalid PowerShell: $file"}
}
'PowerShell source parsing passed.'

$tokens=$null;$errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'test-windows-playback.ps1'),[ref]$tokens,[ref]$errors)
$gate=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-PlaybackCheckout'},$true)
if (-not $gate) { throw 'Missing exact-source playback checkout gate.' }
Invoke-Expression $gate.Extent.Text
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('yourtube-proof-git-'+[guid]::NewGuid())
$null=New-Item -ItemType Directory $fixture
Push-Location $fixture
try {
 git init -q
 'clean' | Set-Content Input.cs
 git add Input.cs
 git -c user.name=Fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit -qm 'Playback proof fixture'
 if ($LASTEXITCODE -ne 0) { throw 'Could not create checkout fixture.' }
 $sha=(git rev-parse HEAD).Trim()
 Assert-PlaybackCheckout $sha
 foreach ($case in @('mismatched SHA','modified source','untracked input')) {
  if ($case -eq 'modified source') { 'modified' | Set-Content Input.cs }
  if ($case -eq 'untracked input') { git restore Input.cs; 'extra' | Set-Content Extra.cs }
  $rejected=$false
  try { Assert-PlaybackCheckout $(if ($case -eq 'mismatched SHA') { '0'*40 } else { $sha }) } catch { $rejected=$true }
  if (-not $rejected) { throw "Playback proof accepted $case." }
 }
} finally { Pop-Location; Remove-Item $fixture -Recurse -Force }
'Playback proof checkout gate passed accepted and rejected real Git fixtures.'
