import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const path = fileURLToPath(new URL('../scripts/test-windows-native.ps1', import.meta.url));
const smoke = readFileSync(path, 'utf8');

test('installed icon verification records digests and rejects missing, empty or mismatched ICOs', t => {
  assert.match(smoke, /function Assert-InstalledIcon/);
  const result = spawnSync(process.env.PWSH ?? 'pwsh', ['-NoProfile', '-Command', `
    $ErrorActionPreference = 'Stop'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile('${path.replaceAll("'", "''")}', [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $function = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-InstalledIcon' }, $true)
    Invoke-Expression $function.Extent.Text
    function Write-Json($Value, $Name) { $script:diagnostic = $Value }
    $root = Join-Path ([IO.Path]::GetTempPath()) ('yourtube-icon-contract-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory $root
    try {
      $installed = Join-Path $root 'installed.ico'; $expected = Join-Path $root 'expected.ico'
      function Must-Reject([scriptblock]$Action) {
        $rejected = $false
        try { & $Action } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid icon accepted' }
      }
      Must-Reject { Assert-InstalledIcon $installed @($expected) }
      [IO.File]::WriteAllBytes($installed, [byte[]]@())
      Must-Reject { Assert-InstalledIcon $installed @($expected) }
      [IO.File]::WriteAllBytes($installed, [byte[]]@(0,0,1,0,1,0,42))
      Assert-InstalledIcon $installed @($expected)
      if ($diagnostic.comparison -ne 'unavailable' -or -not $diagnostic.installedSha256) { throw 'Missing no-reference diagnostic' }
      Copy-Item $installed $expected
      Assert-InstalledIcon $installed @($expected)
      if ($diagnostic.comparison -ne 'matched' -or $diagnostic.references.Count -ne 1) { throw 'Missing match evidence' }
      [IO.File]::WriteAllBytes($expected, [byte[]]@(0,0,1,0,1,0,43))
      Must-Reject { Assert-InstalledIcon $installed @($expected) }
      if ($diagnostic.comparison -ne 'mismatch') { throw 'Missing mismatch diagnostic' }
      Write-Output 'icon byte fixtures passed'
    } finally { Remove-Item $root -Recurse -Force }
  `], { encoding: 'utf8' });
  if (result.error?.code === 'ENOENT' && process.platform !== 'win32' && !process.env.PWSH) {
    return t.skip('PowerShell is unavailable; set PWSH to run the fixture');
  }
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /icon byte fixtures passed/);
});

test('installed smoke requests small and large HWND icons with explicit class fallback and saves owned copies', () => {
  assert.match(smoke, /function Save-WindowIcons/);
  assert.match(smoke, /WM_GETICON/);
  assert.match(smoke, /@\{ name = 'small'; kind = 0; classIndex = -34 \}/);
  assert.match(smoke, /@\{ name = 'large'; kind = 1; classIndex = -14 \}/);
  for (const token of ['GetClassLongPtr', 'GetClassLong32', 'CopyIcon', 'DestroyIcon', 'FromHandle', 'ToBitmap', 'window-icons.json']) {
    assert.ok(smoke.includes(token), `missing ${token}`);
  }
  assert.match(smoke, /pixelComparison = 'not-performed'/);
  assert.match(smoke, /Save-WindowIcons \$window/);
});

test('separate taskbar evidence uses visible Shell_TrayWnd desktop pixels, not synthetic rendering', () => {
  const capture = smoke.slice(smoke.indexOf('function Save-TaskbarScreenshot'), smoke.indexOf('\ntry {\n    Start-Transcript'));
  assert.match(capture, /FindWindow\('Shell_TrayWnd'/);
  for (const token of ['IsWindowVisible', 'GetWindowRect', 'VirtualScreen', 'WindowFromPoint', 'IsChild', 'CopyFromScreen', 'taskbar.png', 'taskbar.json']) {
    assert.ok(capture.includes(token), `missing taskbar capture guard/output: ${token}`);
  }
  assert.match(capture, /throw 'Taskbar/);
  assert.doesNotMatch(capture, /PrintWindow|DrawIcon|ShowWindow|SetWindowPos/);
  assert.match(smoke, /Save-WindowScreenshot \$window[\s\S]*Save-TaskbarScreenshot/);
  assert.match(smoke, /taskbarSha256 = \(Get-FileHash/);
  assert.match(smoke, /iconVerified = \$false; taskbarCaptured = \$false/);
  assert.doesNotMatch(smoke, /SHGetPropertyStoreForWindow|SHAppBarMessage|Stop-Process.*explorer|Clear-.*IconCache/);
});

test('icon evidence remains within existing disposable unauthenticated smoke', () => {
  assert.match(smoke, /Existing YourTube profile: refusing to alter user data/);
  assert.match(smoke, /No accessible input desktop/);
  assert.match(smoke, /current\\Assets\\YourTube\.ico/);
  assert.match(smoke, /publish[\\/]Assets[\\/]YourTube\.ico/);
  assert.match(smoke, /apps[\\/]windows[\\/]YourTube\.App[\\/]Assets[\\/]YourTube\.ico/);
  assert.match(smoke, /uninstallVerified = \$true/);
  assert.match(smoke, /installerSha256 = \(Get-FileHash/);
});