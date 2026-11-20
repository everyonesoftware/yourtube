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
 $names=@('YourTube.exe','YourTube.dll','YourTube.Core.dll','YourTube.Update.dll','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','coreclr.dll','resources.pri')
 foreach($name in $names){[IO.File]::WriteAllText((Join-Path $root $name),'fixture-not-binary')}
 Assert-NativePayload $root
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
foreach($file in @('build-windows.ps1','test-windows-native.ps1')){
 $tokens=$null;$errors=$null;$null=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
 if($errors.Count){throw "Invalid PowerShell: $file"}
}
'PowerShell source parsing passed.'
